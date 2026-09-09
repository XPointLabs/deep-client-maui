using System.Security.Cryptography;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Domain.ContactV1;
using Deep.Client.Shared.Services.ContactV1;
using Deep.Client.Shared.Services.XPointNetworkV1;
using Deep.Protocol.DeepExtension.PrivacyRouting;
using Deep.Protocol.XPointNetworkV1;

namespace Deep.Client.Maui.Services;

internal enum ContactResolveRuntimeUnavailableReason
{
    GenesisPin = 1,
    MonotonicClock = 2,
    PrivacyRoute = 3,
    AuthoritySource = 4,
    DirectorySource = 5,
    MalformedConfiguration = 6,
    CrossNetworkConfiguration = 7,
}

internal sealed record ContactResolveRuntimePrerequisites(
    XPointNetworkGenesisPin? GenesisPin,
    IOnionMonotonicClock? MonotonicClock,
    Func<PrivacyRoutedContactResolverTransport>? PrivacyRoutedTransportFactory,
    Func<ContactResolverTrustedVerifier>? TrustedAuthorityVerifierFactory,
    Func<IContactResolvePlacementContextSource?>? PlacementContextSourceFactory,
    ushort SupportedDirectoryReader = 1,
    ContactResolveRuntimeUnavailableReason? UnavailableReason = null);

internal interface IContactResolveRuntimePrerequisitesSource
{
    ValueTask<ContactResolveRuntimePrerequisites> GetCurrentAsync(
        CancellationToken cancellationToken = default);
}

internal sealed class DeepContactResolveRuntimeAccessor : IDeepContactRuntimeAccessor
{
    private readonly DeepAccountRuntimeAccessor accounts;
    private readonly IContactResolveRuntimePrerequisitesSource prerequisites;

    internal DeepContactResolveRuntimeAccessor(
        DeepAccountRuntimeAccessor accounts,
        IContactResolveRuntimePrerequisitesSource prerequisites)
    {
        this.accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        this.prerequisites = prerequisites
            ?? throw new ArgumentNullException(nameof(prerequisites));
    }

    public Task<string> GetPermanentDeepIdAsync(
        CancellationToken cancellationToken = default) =>
        accounts.GetPermanentDeepIdAsync(cancellationToken);

    public async Task<ContactImportAndResolveResult> ImportAndEnqueueResolveAsync(
        string input,
        CancellationToken cancellationToken = default)
    {
        var importer = await accounts.GetContactImporterAsync(cancellationToken)
            .ConfigureAwait(false);
        var imported = await importer.ImportAsync(input, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var persistence = await accounts.GetContactResolvePersistenceBindingAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            var current = await prerequisites.GetCurrentAsync(cancellationToken)
                .ConfigureAwait(false);
            var composition = ProductionContactResolveRuntimeComposition.TryCreate(
                persistence,
                current);
            if (composition.UnavailableReason is { } unavailable)
            {
                return Pending(imported.Disposition, unavailable);
            }

            using var runtime = composition.Runtime!;
            return await RunDurableAsync(
                    imported.Disposition,
                    imported.PendingAddress,
                    runtime,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return Pending(imported.Disposition, ContactResolvePendingStatus.RetryableFailure);
        }
        catch (IOException)
        {
            return Pending(imported.Disposition, ContactResolvePendingStatus.RetryableFailure);
        }
        catch (ContactResolvePathException exception) when (
            exception.Code.Contains("unavailable", StringComparison.Ordinal))
        {
            return Pending(
                imported.Disposition,
                ContactResolvePendingStatus.DirectorySourceUnavailable);
        }
        catch (ContactResolveOperationFailClosedException)
        {
            return FailClosed(imported.Disposition);
        }
        catch (CryptographicException)
        {
            return FailClosed(imported.Disposition);
        }
    }

    private static async Task<ContactImportAndResolveResult> RunDurableAsync(
        Deep.Client.Shared.Persistence.ContactV1.PendingContactAddressWriteDisposition
            importDisposition,
        PendingContactAddress pendingAddress,
        ProductionContactResolveRuntime runtime,
        CancellationToken cancellationToken)
    {
        var operations = await runtime.Coordinator.ReadAllAsync(cancellationToken)
            .ConfigureAwait(false);
        var existing = operations
            .Where(operation => SamePending(operation.PendingAddress, pendingAddress))
            .OrderByDescending(static operation => operation.UpdatedAt)
            .FirstOrDefault();

        ContactResolveOperationResult resolved;
        if (existing is null)
        {
            var context = await runtime.PlacementSource.MintPlacementContextAsync(
                    runtime.ContactScope,
                    pendingAddress,
                    cancellationToken)
                .ConfigureAwait(false);
            resolved = await runtime.Coordinator.StartAsync(
                    pendingAddress,
                    context,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        else if (existing.State ==
                 Deep.Client.Shared.Persistence.ContactV1.ContactResolveOperationState
                     .AwaitingRefreshedContext)
        {
            var context = await runtime.PlacementSource.MintPlacementContextAsync(
                    runtime.ContactScope,
                    pendingAddress,
                    cancellationToken)
                .ConfigureAwait(false);
            resolved = await runtime.Coordinator.RestartAfterStaleViewAsync(
                    existing.OperationId,
                    context,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            resolved = await runtime.Coordinator.ResumeAsync(
                    existing.OperationId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var presentation = ContactResolverOutcomeUiMapper.Map(
            resolved.Result.Disposition,
            resolved.Result.Retry,
            resolved.Result.RetryAfter);
        var queueState = presentation.IsVerified
            ? ContactResolveQueueState.Verified
            : presentation.IsFailClosed
                ? ContactResolveQueueState.FailClosed
                : presentation.CanRetrySameExactRequest || presentation.RequiresFreshOperation
                    ? ContactResolveQueueState.PendingRetry
                    : ContactResolveQueueState.Terminal;
        VerifiedDirectConversationTarget? verifiedConversation = null;
        if (queueState == ContactResolveQueueState.Verified)
        {
            var relationship = resolved.Result.Commit?.Relationship
                ?? throw new CryptographicException(
                    "A verified ContactV1 outcome did not link a relationship.");
            if (!relationship.RelationshipId.Equals(resolved.DurableState.RelationshipId))
            {
                throw new CryptographicException(
                    "The verified ContactV1 relationship differs from the durable operation.");
            }
            verifiedConversation = new VerifiedDirectConversationTarget(
                relationship.RelationshipId,
                relationship.ConversationId);
        }
        return new ContactImportAndResolveResult(
            importDisposition,
            queueState,
            presentation.Title,
            presentation.Message,
            queueState == ContactResolveQueueState.PendingRetry,
            resolved.Result.Disposition,
            resolved.Result.Retry,
            verifiedConversation);
    }

    private static bool SamePending(
        PendingContactAddress left,
        PendingContactAddress right) =>
        left.ImportedAt == right.ImportedAt
        && left.Address.Kind == right.Address.Kind
        && left.Address.CanonicalBytes.Length == right.Address.CanonicalBytes.Length
        && CryptographicOperations.FixedTimeEquals(
            left.Address.CanonicalBytes.Span,
            right.Address.CanonicalBytes.Span);

    private static ContactImportAndResolveResult Pending(
        Deep.Client.Shared.Persistence.ContactV1.PendingContactAddressWriteDisposition
            disposition,
        ContactResolveRuntimeUnavailableReason reason) =>
        Pending(disposition, reason switch
        {
            ContactResolveRuntimeUnavailableReason.GenesisPin =>
                ContactResolvePendingStatus.GenesisPinUnavailable,
            ContactResolveRuntimeUnavailableReason.MonotonicClock =>
                ContactResolvePendingStatus.MonotonicClockUnavailable,
            ContactResolveRuntimeUnavailableReason.PrivacyRoute =>
                ContactResolvePendingStatus.PrivacyRouteUnavailable,
            ContactResolveRuntimeUnavailableReason.AuthoritySource =>
                ContactResolvePendingStatus.AuthoritySourceUnavailable,
            ContactResolveRuntimeUnavailableReason.DirectorySource =>
                ContactResolvePendingStatus.DirectorySourceUnavailable,
            ContactResolveRuntimeUnavailableReason.MalformedConfiguration =>
                ContactResolvePendingStatus.MalformedConfiguration,
            ContactResolveRuntimeUnavailableReason.CrossNetworkConfiguration =>
                ContactResolvePendingStatus.CrossNetworkConfiguration,
            _ => throw new ArgumentOutOfRangeException(nameof(reason)),
        });

    private static ContactImportAndResolveResult Pending(
        Deep.Client.Shared.Persistence.ContactV1.PendingContactAddressWriteDisposition
            disposition,
        string message) =>
        new(
            disposition,
            ContactResolveQueueState.PendingRetry,
            ContactResolvePendingStatus.Title,
            message,
            CanRetry: true);

    private static ContactImportAndResolveResult FailClosed(
        Deep.Client.Shared.Persistence.ContactV1.PendingContactAddressWriteDisposition
            disposition) =>
        new(
            disposition,
            ContactResolveQueueState.FailClosed,
            "Проверка не пройдена",
            ContactResolvePendingStatus.VerificationFailed,
            CanRetry: false,
            ContactResolverDisposition.ProtocolRejected,
            ContactResolverRetryClassification.FailClosed);
}

internal sealed record ContactResolveRuntimeCompositionResult(
    ProductionContactResolveRuntime? Runtime,
    ContactResolveRuntimeUnavailableReason? UnavailableReason);

internal static class ProductionContactResolveRuntimeComposition
{
    internal static ContactResolveRuntimeCompositionResult TryCreate(
        DeepContactResolvePersistenceBinding persistence,
        ContactResolveRuntimePrerequisites prerequisites)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(prerequisites);
        if (prerequisites.UnavailableReason is { } unavailableReason)
        {
            return Unavailable(unavailableReason);
        }
        if (prerequisites.GenesisPin is null)
        {
            return Unavailable(ContactResolveRuntimeUnavailableReason.GenesisPin);
        }
        if (prerequisites.MonotonicClock is null)
        {
            return Unavailable(ContactResolveRuntimeUnavailableReason.MonotonicClock);
        }
        if (prerequisites.PrivacyRoutedTransportFactory is null)
        {
            return Unavailable(ContactResolveRuntimeUnavailableReason.PrivacyRoute);
        }
        if (prerequisites.TrustedAuthorityVerifierFactory is null)
        {
            return Unavailable(ContactResolveRuntimeUnavailableReason.AuthoritySource);
        }
        if (prerequisites.PlacementContextSourceFactory is null)
        {
            return Unavailable(ContactResolveRuntimeUnavailableReason.DirectorySource);
        }
        if (prerequisites.SupportedDirectoryReader == 0)
        {
            return Unavailable(ContactResolveRuntimeUnavailableReason.AuthoritySource);
        }

        PrivacyRoutedContactResolverTransport? transport = null;
        try
        {
            var placement = prerequisites.PlacementContextSourceFactory();
            transport = prerequisites.PrivacyRoutedTransportFactory();
            var trustedVerifier = prerequisites.TrustedAuthorityVerifierFactory();
            if (placement is null || transport is null || trustedVerifier is null)
            {
                transport?.Dispose();
                return Unavailable(
                    ContactResolveRuntimeUnavailableReason.MalformedConfiguration);
            }
            var coordinator = new ContactResolveOperationCoordinator(
                persistence.ContactStore,
                transport,
                trustedVerifier);
            return new ContactResolveRuntimeCompositionResult(
                new ProductionContactResolveRuntime(
                    transport,
                    persistence.ContactStore.Scope,
                    placement,
                    coordinator),
                null);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or UriFormatException)
        {
            transport?.Dispose();
            return Unavailable(ContactResolveRuntimeUnavailableReason.MalformedConfiguration);
        }
        catch
        {
            transport?.Dispose();
            throw;
        }
    }

    private static ContactResolveRuntimeCompositionResult Unavailable(
        ContactResolveRuntimeUnavailableReason reason) => new(null, reason);
}

internal sealed class ProductionContactResolveRuntime : IDisposable
{
    private PrivacyRoutedContactResolverTransport? transport;

    internal ProductionContactResolveRuntime(
        PrivacyRoutedContactResolverTransport transport,
        ContactStoreScope contactScope,
        IContactResolvePlacementContextSource placementSource,
        ContactResolveOperationCoordinator coordinator)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ContactScope = contactScope ?? throw new ArgumentNullException(nameof(contactScope));
        PlacementSource = placementSource
            ?? throw new ArgumentNullException(nameof(placementSource));
        Coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    internal ContactStoreScope ContactScope { get; }
    internal IContactResolvePlacementContextSource PlacementSource { get; }
    internal ContactResolveOperationCoordinator Coordinator { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref transport, null)?.Dispose();
    }
}
