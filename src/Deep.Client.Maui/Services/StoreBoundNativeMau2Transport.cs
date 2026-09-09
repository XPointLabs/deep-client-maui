using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Features;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using System.Security.Cryptography;
using System.Text.Json;

namespace Deep.Client.Maui.Services;

internal sealed record ProvisionedMailboxRuntime(
    VerifiedOfficialMailboxAuthority Authority,
    ClientMailboxActivation Activation,
    IMailboxClientDecodePolicyProvider DecodePolicies,
    SessionId LocalSessionId,
    MailboxCredentialSelector SelfSelector,
    Func<SessionId, CancellationToken, Task<MailboxCredentialSelector?>>
        ResolveRecipientAsync,
    IClientMailboxBinaryIngress Ingress,
    TimeProvider TimeProvider);

internal interface IReactiveMau2ReadRuntime
{
    Task<IReadOnlyList<InboundMessageEnvelope>> ReceiveAuthenticatedAsync(
        SessionIdentityProvider identity,
        CancellationToken cancellationToken);

    Task<AuthenticatedInboxBatch> RetrieveAuthenticatedAsync(
        SessionIdentityProvider identity,
        string? cursor,
        int limit,
        CancellationToken cancellationToken);

    Task<OpaqueMailboxInboxPage> RetrieveOpaqueMailboxInboxAsync(
        IMailboxOperationSigner signer,
        OpaqueMailboxContinuation continuation,
        CancellationToken cancellationToken);

    Task RetireTerminallyRejectedRetrieveAsync(
        SessionId account,
        ClientMailboxTransportException terminalFailure,
        CancellationToken cancellationToken);
}

internal sealed class NativeReactiveMau2ReadRuntime(
    NativeMau2MailboxTransport transport) : IReactiveMau2ReadRuntime
{
    private readonly NativeMau2MailboxTransport transport = transport ??
        throw new ArgumentNullException(nameof(transport));

    public Task<IReadOnlyList<InboundMessageEnvelope>> ReceiveAuthenticatedAsync(
        SessionIdentityProvider identity,
        CancellationToken cancellationToken) =>
        transport.ReceiveAuthenticatedAsync(identity, cancellationToken);

    public Task<AuthenticatedInboxBatch> RetrieveAuthenticatedAsync(
        SessionIdentityProvider identity,
        string? cursor,
        int limit,
        CancellationToken cancellationToken) =>
        transport.RetrieveAuthenticatedAsync(identity, cursor, limit, cancellationToken);

    public Task<OpaqueMailboxInboxPage> RetrieveOpaqueMailboxInboxAsync(
        IMailboxOperationSigner signer,
        OpaqueMailboxContinuation continuation,
        CancellationToken cancellationToken) =>
        transport.RetrieveOpaqueMailboxInboxAsync(signer, continuation, cancellationToken);

    public Task RetireTerminallyRejectedRetrieveAsync(
        SessionId account,
        ClientMailboxTransportException terminalFailure,
        CancellationToken cancellationToken) =>
        transport.RetireTerminallyRejectedRetrieveAsync(
            account, terminalFailure, cancellationToken);
}

internal interface IMailboxRuntimeProvisioningSource
{
    bool SupportsReactiveRejectedRetrieveRefresh => false;

    void AttachRuntimeState(
        SqliteSessionStore store,
        SecureRecoverySessionStore secureStore)
    {
    }

    Task<ProvisionedMailboxRuntime> ProvisionAsync(
        SqliteSessionStore store,
        MailboxHolderIdentity holder,
        MailboxInfrastructureOwnership ownership,
        CancellationToken cancellationToken = default);

    Task<ProvisionedMailboxRuntime?> RefreshAfterRejectedRetrieveAsync(
        SqliteSessionStore store,
        MailboxHolderIdentity holder,
        MailboxInfrastructureOwnership ownership,
        ulong failedRuntimeGeneration,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<ProvisionedMailboxRuntime?>(null);

    Task ReleaseAsync(
        SessionId account,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class PersistedMailboxPeerSelectorStore(
    IAtomicBoundedSettingsRepository settings)
{
    private const byte Schema = 1;
    private const int SessionIdBytes = 33;
    private const int OpaqueBytes = 32;
    private const int CanonicalBytes = 1 + (2 * SessionIdBytes) + (4 * OpaqueBytes) + 1;
    private const int MaximumEncodedBytes = 384;
    private const int MaximumAttempts = 4;
    private readonly IAtomicBoundedSettingsRepository settings = settings ??
        throw new ArgumentNullException(nameof(settings));

    public async Task SaveAsync(
        SessionId local,
        SessionId peer,
        MailboxCredentialSelector selector,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selector);
        if (local == peer || selector.Kind != MailboxCredentialScopeKind.Peer)
            throw new ArgumentException(
                "Only a peer selector for another account can be persisted.",
                nameof(selector));
        var encoded = Encode(local, peer, selector);
        try
        {
            for (var attempt = 0; attempt < MaximumAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = await settings.ReadAtomicBoundedSettingAsync(
                    Key(local, peer), MaximumEncodedBytes, cancellationToken)
                    .ConfigureAwait(false);
                AtomicBoundedSettingMutationResult result;
                switch (current.Result)
                {
                    case AtomicBoundedSettingReadResult.Missing:
                        result = await settings.CreateAtomicBoundedSettingAsync(
                            Key(local, peer), encoded, MaximumEncodedBytes, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case AtomicBoundedSettingReadResult.Found when current.Revision is not null:
                        if (Fixed(current.GetValueCopy(), encoded)) return;
                        result = await settings.ReplaceAtomicBoundedSettingAsync(
                            Key(local, peer), current.Revision, encoded, MaximumEncodedBytes,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case AtomicBoundedSettingReadResult.Oversized:
                        throw new InvalidDataException(
                            "Persisted production peer selector is oversized.");
                    default:
                        throw new InvalidOperationException(
                            "Production peer selector storage is unavailable.");
                }

                if (result == AtomicBoundedSettingMutationResult.Applied) return;
                if (result is AtomicBoundedSettingMutationResult.Conflict or
                    AtomicBoundedSettingMutationResult.Missing)
                    continue;
                if (result == AtomicBoundedSettingMutationResult.OutcomeUnknown)
                {
                    var observed = await settings.ReadAtomicBoundedSettingAsync(
                        Key(local, peer), MaximumEncodedBytes, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (observed.Result == AtomicBoundedSettingReadResult.Found &&
                        Fixed(observed.GetValueCopy(), encoded))
                        return;
                }
                throw new InvalidOperationException(
                    "Production peer selector did not persist atomically.");
            }
            throw new InvalidOperationException(
                "Production peer selector changed concurrently.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    public async Task<MailboxCredentialSelector?> LoadAsync(
        SessionId local,
        SessionId peer,
        CancellationToken cancellationToken = default)
    {
        var outcome = await settings.ReadAtomicBoundedSettingAsync(
            Key(local, peer), MaximumEncodedBytes, cancellationToken).ConfigureAwait(false);
        return outcome.Result switch
        {
            AtomicBoundedSettingReadResult.Missing => null,
            AtomicBoundedSettingReadResult.Found => Decode(
                local, peer, outcome.GetValueCopy()),
            AtomicBoundedSettingReadResult.Oversized => throw new InvalidDataException(
                "Persisted production peer selector is oversized."),
            _ => throw new InvalidOperationException(
                "Production peer selector storage is unavailable.")
        };
    }

    private static byte[] Encode(
        SessionId local,
        SessionId peer,
        MailboxCredentialSelector selector)
    {
        var localBytes = Convert.FromHexString(local.Value);
        var peerBytes = Convert.FromHexString(peer.Value);
        var accountScope = selector.AccountScope.ToArray();
        var canonical = new byte[CanonicalBytes];
        try
        {
            if (localBytes.Length != SessionIdBytes || peerBytes.Length != SessionIdBytes ||
                accountScope.Length != OpaqueBytes ||
                selector.SubjectId.Length != OpaqueBytes ||
                selector.IssuerContext.Length != OpaqueBytes ||
                selector.ScopeId.Length != OpaqueBytes ||
                !selector.GroupMembershipCommitment.IsEmpty)
                throw new InvalidDataException(
                    "Production peer selector has an invalid canonical shape.");
            var offset = 0;
            canonical[offset++] = Schema;
            localBytes.CopyTo(canonical, offset);
            offset += SessionIdBytes;
            peerBytes.CopyTo(canonical, offset);
            offset += SessionIdBytes;
            accountScope.CopyTo(canonical.AsSpan(offset));
            offset += OpaqueBytes;
            canonical[offset++] = (byte)selector.Kind;
            selector.SubjectId.Span.CopyTo(canonical.AsSpan(offset));
            offset += OpaqueBytes;
            selector.IssuerContext.Span.CopyTo(canonical.AsSpan(offset));
            offset += OpaqueBytes;
            selector.ScopeId.Span.CopyTo(canonical.AsSpan(offset));
            var output = JsonSerializer.SerializeToUtf8Bytes(
                new StoredPeerSelector(Schema, Convert.ToBase64String(canonical)));
            if (output.Length is 0 or > MaximumEncodedBytes)
            {
                CryptographicOperations.ZeroMemory(output);
                throw new InvalidDataException(
                    "Production peer selector encoding is oversized.");
            }
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
            CryptographicOperations.ZeroMemory(localBytes);
            CryptographicOperations.ZeroMemory(peerBytes);
            CryptographicOperations.ZeroMemory(accountScope);
        }
    }

    private static MailboxCredentialSelector Decode(
        SessionId local,
        SessionId peer,
        byte[] encoded)
    {
        byte[]? canonical = null;
        try
        {
            if (encoded.Length is 0 or > MaximumEncodedBytes)
                throw new InvalidDataException(
                    "Persisted production peer selector is invalid.");
            using var document = JsonDocument.Parse(encoded, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 3
            });
            var root = document.RootElement;
            var properties = root.EnumerateObject().Select(static value => value.Name).ToArray();
            if (root.ValueKind != JsonValueKind.Object || properties.Length != 2 ||
                !properties.Contains("Schema", StringComparer.Ordinal) ||
                !properties.Contains("Selector", StringComparer.Ordinal) ||
                root.GetProperty("Schema").GetByte() != Schema ||
                root.GetProperty("Selector").ValueKind != JsonValueKind.String)
                throw new InvalidDataException(
                    "Persisted production peer selector is invalid.");
            canonical = Convert.FromBase64String(
                root.GetProperty("Selector").GetString() ?? string.Empty);
            if (canonical.Length != CanonicalBytes || canonical[0] != Schema)
                throw new InvalidDataException(
                    "Persisted production peer selector is invalid.");
            var offset = 1;
            var encodedLocal = SessionId.Parse(Convert.ToHexStringLower(
                canonical.AsSpan(offset, SessionIdBytes)));
            offset += SessionIdBytes;
            var encodedPeer = SessionId.Parse(Convert.ToHexStringLower(
                canonical.AsSpan(offset, SessionIdBytes)));
            offset += SessionIdBytes;
            if (encodedLocal != local || encodedPeer != peer ||
                canonical[offset + OpaqueBytes] != (byte)MailboxCredentialScopeKind.Peer)
                throw new InvalidDataException(
                    "Persisted production peer selector binding is invalid.");
            var account = OutboxAccountScope.FromBytes(
                canonical.AsSpan(offset, OpaqueBytes));
            offset += OpaqueBytes + 1;
            var selector = new MailboxCredentialSelector(
                account,
                MailboxCredentialScopeKind.Peer,
                canonical.AsSpan(offset, OpaqueBytes),
                canonical.AsSpan(offset + OpaqueBytes, OpaqueBytes));
            offset += 2 * OpaqueBytes;
            if (!Fixed(selector.ScopeId.Span, canonical.AsSpan(offset, OpaqueBytes)))
                throw new InvalidDataException(
                    "Persisted production peer selector scope is invalid.");
            return selector;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or
            JsonException or InvalidOperationException)
        {
            throw new InvalidDataException(
                "Persisted production peer selector is invalid.", exception);
        }
        finally
        {
            if (canonical is not null)
                CryptographicOperations.ZeroMemory(canonical);
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private static string Key(SessionId local, SessionId peer) =>
        "deep.mailbox.peer-selector.v1:" + local.Value + ":" + peer.Value;

    private static bool Fixed(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);

    private sealed record StoredPeerSelector(byte Schema, string Selector);
}

/// <summary>Debug-only adapter for the exact local Android/Windows fixture bundle.</summary>
internal sealed class DevelopmentMailboxRuntimeProvisioningSource :
    IMailboxRuntimeProvisioningSource
{
    internal DevelopmentMailboxRuntimeProvisioningSource()
    {
    }

    internal DevelopmentMailboxRuntimeProvisioningSource(
        Func<MailboxRuntimeProvisioning> provisioningFactory,
        Action<MailboxHolderIdentity> holderAvailable,
        HttpServiceTransportFactory transportFactory,
        HttpServiceClientOptions clientOptions,
        IPrivacyMailboxRouteSelectionObserver? routeSelectionObserver)
    {
        ArgumentNullException.ThrowIfNull(provisioningFactory);
        ArgumentNullException.ThrowIfNull(holderAvailable);
        ArgumentNullException.ThrowIfNull(transportFactory);
        ArgumentNullException.ThrowIfNull(clientOptions);
        _ = routeSelectionObserver;
    }

    public Task<ProvisionedMailboxRuntime> ProvisionAsync(
        SqliteSessionStore store,
        MailboxHolderIdentity holder,
        MailboxInfrastructureOwnership ownership,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(holder);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<ProvisionedMailboxRuntime>(
            ProductionMailboxPrivacyRouteBootstrap.CreateUnavailableException());
    }
}

/// <summary>
/// App-private, store-bound MAU2 composition. Binding is deferred until an account exists;
/// the injected source must return already verified runtime material and an exact ingress.
/// </summary>
internal sealed class StoreBoundNativeMau2Transport :
    IAuthenticatedOpaqueMailboxTransport,
    IResumableMailboxIdentityAuthenticatedRawTransport,
    IAuthenticatedInboxTransport,
    IMailboxAckCorrelationProjectionSource,
    IMetadataPrivateSessionMessageTransport,
    IMailboxDeliveryPolicy,
    IAccountGenerationLifecycle,
    IDisposable
{
    private readonly SqliteSessionStore store;
    private readonly SecureRecoverySessionStore secureStore;
    private readonly IMailboxRuntimeProvisioningSource provisioningSource;
    private readonly MailboxInfrastructureOwnership ownership;
    private readonly ClientFeatureFlags featureFlags;
    private readonly IMailboxDispatchRouteUsageObserver? routeUsageObserver;
    private readonly Func<NativeMau2MailboxTransport, IReactiveMau2ReadRuntime>
        reactiveReadRuntimeFactory;
    private readonly SemaphoreSlim bindGate = new(1, 1);
    private readonly object operationGate = new();
    private BoundRuntime? bound;
    private Task<BoundRuntime?>? refreshTransition;
    private TaskCompletionSource? operationsDrained;
    private int activeOperations;
    private LifecycleState lifecycleState = LifecycleState.Active;
    private int disposed;

    public StoreBoundNativeMau2Transport(
        SqliteSessionStore store,
        SecureRecoverySessionStore secureStore,
        Func<MailboxRuntimeProvisioning> provisioningFactory,
        Action<MailboxHolderIdentity> holderAvailable,
        MailboxInfrastructureOwnership ownership,
        ClientFeatureFlags featureFlags,
        HttpServiceTransportFactory transportFactory,
        HttpServiceClientOptions clientOptions,
        IPrivacyMailboxRouteSelectionObserver? routeSelectionObserver = null,
        IMailboxDispatchRouteUsageObserver? routeUsageObserver = null)
        : this(
            store,
            secureStore,
            new DevelopmentMailboxRuntimeProvisioningSource(
                provisioningFactory,
                holderAvailable,
                transportFactory,
                clientOptions,
                routeSelectionObserver),
            ownership,
            featureFlags,
            routeUsageObserver)
    {
    }

    public StoreBoundNativeMau2Transport(
        SqliteSessionStore store,
        SecureRecoverySessionStore secureStore,
        IMailboxRuntimeProvisioningSource provisioningSource,
        MailboxInfrastructureOwnership ownership,
        ClientFeatureFlags featureFlags,
        IMailboxDispatchRouteUsageObserver? routeUsageObserver = null,
        Func<NativeMau2MailboxTransport, IReactiveMau2ReadRuntime>?
            reactiveReadRuntimeFactory = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.secureStore = secureStore ?? throw new ArgumentNullException(nameof(secureStore));
        this.provisioningSource = provisioningSource ??
            throw new ArgumentNullException(nameof(provisioningSource));
        if (ownership is not (MailboxInfrastructureOwnership.UserManaged or
            MailboxInfrastructureOwnership.OfficialManaged))
            throw new ArgumentOutOfRangeException(nameof(ownership));
        this.ownership = ownership;
        this.featureFlags = featureFlags ?? throw new ArgumentNullException(nameof(featureFlags));
        this.routeUsageObserver = routeUsageObserver;
        this.reactiveReadRuntimeFactory = reactiveReadRuntimeFactory ??
            (static transport => new NativeReactiveMau2ReadRuntime(transport));
        if (!featureFlags.ClientMailboxAdapterEnabled ||
            !featureFlags.MetadataPrivateTransportRequired)
        {
            throw new InvalidOperationException(
                "Authenticated MAU2 requires the native adapter and metadata-private release gates.");
        }
        provisioningSource.AttachRuntimeState(store, secureStore);
    }

    public int InboxNamespace => unchecked((int)0x4d415532);
    public bool UsesMetadataPrivateTransport => true;

    public async Task<MailboxDeliveryDecision> DecideAsync(
        MailboxDeliveryRequest request,
        CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation();
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Envelope);
        if (request.Kind is not (MailboxDeliveryKind.Direct or
            MailboxDeliveryKind.GroupState or
            MailboxDeliveryKind.GroupMessage))
        {
            throw new NotSupportedException(
                "Authenticated MAU2 does not support this mailbox delivery kind.");
        }

        var runtime = await EnsureBoundAsync(
            request.Envelope.Sender, holderPublicKey: null, cancellationToken)
            .ConfigureAwait(false);
        var selector = await runtime.Provisioned.ResolveRecipientAsync(
            request.Envelope.Recipient,
            cancellationToken).ConfigureAwait(false) ??
            throw new NotSupportedException(
                "No verified mailbox credential exists for this recipient.");
        var decision = new MailboxDeliveryDecision(
            MailboxTransportProtocol.AuthenticatedMau2,
            ownership,
            runtime.Provisioned.Authority,
            selector);
        decision.Validate();
        return decision;
    }

    public async Task<IReadOnlyList<IPreparedMailboxAuthenticatedSend>>
        PrepareScopedMailboxLogicalBatchAsync(
        IMailboxOperationSigner signer,
        MailboxLogicalSendBatch logicalBatch,
        IReadOnlyList<MailboxAuthenticatedSendTarget> targets,
        CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation();
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(logicalBatch);
        ArgumentNullException.ThrowIfNull(targets);
        var publicKey = signer.GetEd25519PublicKey();
        try
        {
            var runtime = await EnsureBoundAsync(
                signer.SessionId, publicKey, cancellationToken).ConfigureAwait(false);
            return await runtime.Transport.PrepareScopedMailboxLogicalBatchAsync(
                signer, logicalBatch, targets, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(publicKey);
        }
    }

    public async Task<IReadOnlyList<IPreparedMailboxAuthenticatedSend>?>
        TryResumeScopedMailboxBatchAsync(
        IMailboxOperationSigner signer,
        MailboxLogicalSendBatch batch,
        CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation();
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(batch);
        var publicKey = signer.GetEd25519PublicKey();
        try
        {
            var runtime = await EnsureBoundAsync(
                signer.SessionId, publicKey, cancellationToken).ConfigureAwait(false);
            return await runtime.Transport.TryResumeScopedMailboxBatchAsync(
                signer, batch, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(publicKey);
        }
    }

    public async Task<IReadOnlyList<IPreparedMailboxAuthenticatedSend>>
        PrepareScopedMailboxBatchAsync(
            IMailboxOperationSigner signer,
            IReadOnlyList<MailboxAuthenticatedSendTarget> targets,
            CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation();
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(signer);
        var publicKey = signer.GetEd25519PublicKey();
        try
        {
            var runtime = await EnsureBoundAsync(
                signer.SessionId, publicKey, cancellationToken).ConfigureAwait(false);
            return await runtime.Transport.PrepareScopedMailboxBatchAsync(
                signer, targets, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(publicKey);
        }
    }

    public async Task SendPreparedMailboxAuthenticatedAsync(
        IPreparedMailboxAuthenticatedSend preparedSend,
        CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation();
        var runtime = RequireBound();
#if DEBUG && DEEP_PHYSICAL_E2E
        try
        {
            await runtime.Transport.SendPreparedMailboxAuthenticatedAsync(
                preparedSend, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            CrashDiagnostics.LogException(
                "PhysicalE2E.MailboxDispatch", exception,
                "The physical mailbox dispatch failed before a durable receipt.");
            throw;
        }
#else
        await runtime.Transport.SendPreparedMailboxAuthenticatedAsync(
            preparedSend, cancellationToken).ConfigureAwait(false);
#endif
    }

    public Task SendAsync(
        OutboundMessageEnvelope envelope,
        CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException(
            "Native MAU2 forbids raw-send or routed-storage fallback."));

    public Task<IReadOnlyList<InboundMessageEnvelope>> ReceiveAsync(
        SessionId recipient,
        CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<InboundMessageEnvelope>>(
            new InvalidOperationException("Native MAU2 inbox requires the holder identity."));

    public async Task<IReadOnlyList<InboundMessageEnvelope>> ReceiveAuthenticatedAsync(
        SessionIdentityProvider identity,
        CancellationToken cancellationToken = default)
    {
        var publicKey = identity.GetEd25519PublicKey();
        try
        {
            return await ExecuteReadOnlyRetrieveWithReactiveRefreshAsync(
                identity.SessionId,
                publicKey,
                (runtime, token) => runtime.ReceiveAuthenticatedAsync(identity, token),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(publicKey);
        }
    }

    public async Task<AuthenticatedInboxBatch> RetrieveAuthenticatedAsync(
        SessionIdentityProvider identity,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var publicKey = identity.GetEd25519PublicKey();
        try
        {
            return await ExecuteReadOnlyRetrieveWithReactiveRefreshAsync(
                identity.SessionId,
                publicKey,
                (runtime, token) => runtime.RetrieveAuthenticatedAsync(
                    identity, cursor, limit, token),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(publicKey);
        }
    }

    public bool TryDecodeInboxEntry(
        DurableInboxWireEntry entry,
        SessionId recipient,
        out InboundMessageEnvelope envelope)
    {
        using var operation = EnterOperation();
        return RequireBound().Transport.TryDecodeInboxEntry(entry, recipient, out envelope);
    }

    public async Task<OpaqueMailboxInboxPage> RetrieveOpaqueMailboxInboxAsync(
        IMailboxOperationSigner signer,
        OpaqueMailboxContinuation continuation,
        CancellationToken cancellationToken = default)
    {
        var publicKey = signer.GetEd25519PublicKey();
        try
        {
            return await ExecuteReadOnlyRetrieveWithReactiveRefreshAsync(
                signer.SessionId,
                publicKey,
                (runtime, token) => runtime.RetrieveOpaqueMailboxInboxAsync(
                    signer, continuation, token),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(publicKey);
        }
    }

    private async Task<TResult> ExecuteReadOnlyRetrieveWithReactiveRefreshAsync<TResult>(
        SessionId account,
        byte[] holderPublicKey,
        Func<IReactiveMau2ReadRuntime, CancellationToken, Task<TResult>> retrieve,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(retrieve);
        BoundRuntime? failedRuntime = null;
        ClientMailboxTransportException? terminalFailure = null;
        using (var operation = EnterOperation())
        {
            var runtime = await EnsureBoundAsync(
                account, holderPublicKey, cancellationToken).ConfigureAwait(false);
            try
            {
                return await retrieve(runtime.ReactiveReads, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ClientMailboxTransportException exception) when (
                ownership == MailboxInfrastructureOwnership.OfficialManaged &&
                provisioningSource.SupportsReactiveRejectedRetrieveRefresh &&
                IsReactiveRefreshTerminal(exception))
            {
                failedRuntime = runtime;
                terminalFailure = exception;
            }
        }

        var refreshed = await RefreshAfterRejectedRetrieveAsync(
                failedRuntime!, terminalFailure!, account, holderPublicKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (refreshed is null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(terminalFailure!).Throw();
            throw new InvalidOperationException("Unreachable terminal retrieve path.");
        }

        using var retryOperation = EnterOperation();
        var retryRuntime = RequireBound();
        RequireSession(retryRuntime, account);
        if (!ReferenceEquals(retryRuntime, refreshed))
            throw new OperationCanceledException(
                "The refreshed MAU2 runtime changed before the one-shot retrieve retry.");
        return await retrieve(retryRuntime.ReactiveReads, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsReactiveRefreshTerminal(
        ClientMailboxTransportException exception) =>
        !exception.Retryable &&
        exception.Failure is ClientMailboxTransportFailure.AuthorizationRejected or
            ClientMailboxTransportFailure.ConflictOrExpired;

    public async Task AcknowledgeOpaqueMailboxInboxAsync(
        IMailboxOperationSigner signer,
        string opaqueItemHandle,
        CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation();
        var runtime = RequireBound();
        await runtime.Transport.AcknowledgeOpaqueMailboxInboxAsync(
            signer, opaqueItemHandle, cancellationToken).ConfigureAwait(false);
    }

    async Task<MailboxAckCorrelationProjection?>
        IMailboxAckCorrelationProjectionSource.ProjectMailboxAckCorrelationAsync(
            SessionId account,
            string serverHash,
            CancellationToken cancellationToken)
    {
        using var operation = EnterOperation();
        var runtime = RequireBound();
        RequireSession(runtime, account);
        return await ((IMailboxAckCorrelationProjectionSource)runtime.Transport)
            .ProjectMailboxAckCorrelationAsync(account, serverHash, cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        Task drain;
        while (true)
        {
            Task<BoundRuntime?>? pendingRefresh = null;
            lock (operationGate)
            {
                if (Volatile.Read(ref disposed) != 0) return;
                if (lifecycleState == LifecycleState.Refreshing)
                {
                    pendingRefresh = refreshTransition ?? throw new InvalidOperationException(
                        "The MAU2 refresh transition is unavailable.");
                }
                else
                {
                    if (Interlocked.CompareExchange(ref disposed, 1, 0) != 0) return;
                    lifecycleState = LifecycleState.Disposed;
                    drain = activeOperations == 0
                        ? Task.CompletedTask
                        : (operationsDrained ??= new TaskCompletionSource(
                            TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                    break;
                }
            }

            try
            {
                pendingRefresh.GetAwaiter().GetResult();
            }
            catch
            {
                // Refresh failure restores Active before completing the transition.
            }
        }
        drain.GetAwaiter().GetResult();
        bindGate.Wait();
        try
        {
            Interlocked.Exchange(ref bound, null)?.Transport.Dispose();
        }
        finally
        {
            bindGate.Release();
        }
    }

    public async Task StopAsync(
        SessionId account,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        Task drain;
        while (true)
        {
            Task<BoundRuntime?>? pendingRefresh = null;
            lock (operationGate)
            {
                ThrowIfDisposed();
                if (lifecycleState == LifecycleState.Refreshing)
                {
                    pendingRefresh = refreshTransition ?? throw new InvalidOperationException(
                        "The MAU2 refresh transition is unavailable.");
                }
                else
                {
                    if (lifecycleState != LifecycleState.Active)
                        throw new InvalidOperationException(
                            "The MAU2 account generation is not active.");
                    lifecycleState = LifecycleState.Stopping;
                    drain = activeOperations == 0
                        ? Task.CompletedTask
                        : (operationsDrained ??= new TaskCompletionSource(
                            TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                    break;
                }
            }

            try
            {
                await pendingRefresh.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Refresh failure restores Active before completing the transition.
            }
        }
        await drain.ConfigureAwait(false);
        await bindGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var current = bound;
            if (current is not null && current.Provisioned.LocalSessionId != account)
            {
                throw new InvalidOperationException(
                    "The stopped account differs from the bound MAU2 account.");
            }
            await provisioningSource.ReleaseAsync(account, CancellationToken.None)
                .ConfigureAwait(false);
            Interlocked.Exchange(ref bound, null)?.Transport.Dispose();
            lock (operationGate)
            {
                if (lifecycleState == LifecycleState.Stopping)
                {
                    lifecycleState = LifecycleState.Stopped;
                }
            }
        }
        catch
        {
            lock (operationGate)
            {
                if (lifecycleState == LifecycleState.Stopping)
                    lifecycleState = LifecycleState.Active;
            }
            throw;
        }
        finally
        {
            bindGate.Release();
        }
    }

    public void Resume(SessionId account)
    {
        lock (operationGate)
        {
            ThrowIfDisposed();
            if (lifecycleState == LifecycleState.Active)
                return;
            if (lifecycleState != LifecycleState.Stopped)
                throw new InvalidOperationException(
                    "The MAU2 account generation can resume only after a completed stop.");
            if (activeOperations != 0)
                throw new InvalidOperationException(
                    "A MAU2 account generation cannot resume before operations drain.");
            operationsDrained = null;
            lifecycleState = LifecycleState.Active;
        }
    }

    private async Task<BoundRuntime?> RefreshAfterRejectedRetrieveAsync(
        BoundRuntime failedRuntime,
        ClientMailboxTransportException terminalFailure,
        SessionId account,
        byte[] holderPublicKey,
        CancellationToken cancellationToken)
    {
        Task<BoundRuntime?> transition;
        TaskCompletionSource<BoundRuntime?>? completion = null;
        Task drain = Task.CompletedTask;
        lock (operationGate)
        {
            ThrowIfDisposed();
            var current = Volatile.Read(ref bound);
            if (!ReferenceEquals(current, failedRuntime))
            {
                if (current is null)
                    throw new OperationCanceledException(
                        "The rejected MAU2 runtime was released before refresh.");
                RequireSession(current, account);
                if (current.Provisioned.Authority.MinimumGeneration <=
                    failedRuntime.Provisioned.Authority.MinimumGeneration)
                    throw new InvalidOperationException(
                        "The replacement MAU2 runtime did not advance authority generation.");
                return current;
            }

            if (lifecycleState == LifecycleState.Active)
            {
                lifecycleState = LifecycleState.Refreshing;
                drain = activeOperations == 0
                    ? Task.CompletedTask
                    : (operationsDrained ??= new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                completion = new TaskCompletionSource<BoundRuntime?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                refreshTransition = completion.Task;
            }
            else if (lifecycleState != LifecycleState.Refreshing ||
                     refreshTransition is null)
            {
                throw new OperationCanceledException(
                    "The MAU2 account generation no longer permits reactive refresh.");
            }
            transition = refreshTransition;
        }

        if (completion is not null)
        {
            var refreshHolderPublicKey = holderPublicKey.ToArray();
            _ = RunRefreshTransitionAsync(
                completion,
                drain,
                failedRuntime,
                terminalFailure,
                account,
                refreshHolderPublicKey);
        }

        return await transition.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunRefreshTransitionAsync(
        TaskCompletionSource<BoundRuntime?> completion,
        Task drain,
        BoundRuntime failedRuntime,
        ClientMailboxTransportException terminalFailure,
        SessionId account,
        byte[] holderPublicKey)
    {
        try
        {
            await drain.ConfigureAwait(false);
            var replacement = await ReplaceRejectedRuntimeAsync(
                    failedRuntime,
                    terminalFailure,
                    account,
                    holderPublicKey,
                    CancellationToken.None)
                .ConfigureAwait(false);
            CompleteRefreshTransition(completion, replacement, exception: null);
        }
        catch (OperationCanceledException exception)
        {
            CompleteRefreshTransition(completion, replacement: null, exception);
        }
        catch (Exception exception)
        {
            CompleteRefreshTransition(completion, replacement: null, exception);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(
                holderPublicKey);
        }
    }

    private async Task<BoundRuntime?> ReplaceRejectedRuntimeAsync(
        BoundRuntime failedRuntime,
        ClientMailboxTransportException terminalFailure,
        SessionId account,
        byte[] holderPublicKey,
        CancellationToken cancellationToken)
    {
        await bindGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (operationGate)
            {
                ThrowIfDisposed();
                if (lifecycleState != LifecycleState.Refreshing ||
                    !ReferenceEquals(Volatile.Read(ref bound), failedRuntime))
                    throw new OperationCanceledException(
                        "The rejected MAU2 runtime changed before refresh acquisition.");
            }

            await failedRuntime.ReactiveReads.RetireTerminallyRejectedRetrieveAsync(
                    account, terminalFailure, CancellationToken.None)
                .ConfigureAwait(false);

            var provisioned = await provisioningSource.RefreshAfterRejectedRetrieveAsync(
                    store,
                    new MailboxHolderIdentity(account, holderPublicKey),
                    ownership,
                    failedRuntime.Provisioned.Authority.MinimumGeneration,
                    cancellationToken)
                .ConfigureAwait(false);
            if (provisioned is null) return null;

            NativeMau2MailboxTransport? replacementTransport = null;
            var published = false;
            try
            {
                if (provisioned.LocalSessionId != account ||
                    provisioned.Authority.MinimumGeneration <=
                    failedRuntime.Provisioned.Authority.MinimumGeneration)
                    throw new InvalidDataException(
                        "Reactive MAU2 refresh did not produce a strict authority successor.");
                replacementTransport = CreateNativeTransport(provisioned);
                var replacement = new BoundRuntime(
                    provisioned,
                    replacementTransport,
                    reactiveReadRuntimeFactory(replacementTransport));
                lock (operationGate)
                {
                    ThrowIfDisposed();
                    if (lifecycleState != LifecycleState.Refreshing ||
                        !ReferenceEquals(Volatile.Read(ref bound), failedRuntime))
                        throw new OperationCanceledException(
                            "The rejected MAU2 runtime changed before atomic publication.");
                    Volatile.Write(ref bound, replacement);
                    published = true;
                }
                failedRuntime.Transport.Dispose();
                return replacement;
            }
            finally
            {
                if (!published)
                {
                    if (replacementTransport is not null)
                        replacementTransport.Dispose();
                    else
                        (provisioned.Ingress as IDisposable)?.Dispose();
                }
            }
        }
        finally
        {
            bindGate.Release();
        }
    }

    private void CompleteRefreshTransition(
        TaskCompletionSource<BoundRuntime?> completion,
        BoundRuntime? replacement,
        Exception? exception)
    {
        lock (operationGate)
        {
            if (lifecycleState == LifecycleState.Refreshing)
                lifecycleState = LifecycleState.Active;
            if (ReferenceEquals(refreshTransition, completion.Task))
                refreshTransition = null;
        }

        if (exception is OperationCanceledException canceled)
            completion.TrySetCanceled(canceled.CancellationToken);
        else if (exception is not null)
            completion.TrySetException(exception);
        else
            completion.TrySetResult(replacement);
    }

    private async Task<BoundRuntime> EnsureBoundAsync(
        SessionId sessionId,
        byte[]? holderPublicKey,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var current = Volatile.Read(ref bound);
        if (current is not null)
        {
            RequireSession(current, sessionId);
            return current;
        }

        await bindGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            current = bound;
            if (current is not null)
            {
                RequireSession(current, sessionId);
                return current;
            }

            ProvisionedMailboxRuntime provisioned;
            if (holderPublicKey is not null)
            {
                provisioned = await provisioningSource.ProvisionAsync(
                    store,
                    new MailboxHolderIdentity(sessionId, holderPublicKey),
                    ownership,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var phrase = await secureStore.GetAsync<string>(
                    SessionAccountService.ActiveRecoveryPhraseKey,
                    cancellationToken).ConfigureAwait(false);
                if (!SessionAccountService.IsCanonicalRecoveryPhrase(phrase))
                {
                    throw new InvalidOperationException(
                        "Authenticated MAU2 cannot bind before a canonical account exists.");
                }
                using var identity = new SessionIdentityProvider(phrase!);
                if (identity.SessionId != sessionId)
                    throw new InvalidOperationException(
                        "Active recovery identity differs from the MAU2 sender.");
                var publicKey = identity.GetEd25519PublicKey();
                try
                {
                    provisioned = await provisioningSource.ProvisionAsync(
                        store,
                        new MailboxHolderIdentity(sessionId, publicKey),
                        ownership,
                        cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(publicKey);
                }
            }

            var transport = CreateNativeTransport(provisioned);
            try
            {
                current = new BoundRuntime(
                    provisioned,
                    transport,
                    reactiveReadRuntimeFactory(transport));
            }
            catch
            {
                transport.Dispose();
                throw;
            }
            Volatile.Write(ref bound, current);
            return current;
        }
        finally
        {
            bindGate.Release();
        }
    }

    private NativeMau2MailboxTransport CreateNativeTransport(
        ProvisionedMailboxRuntime provisioned) => new(
        featureFlags,
        provisioned.Activation,
        provisioned.Ingress,
        store,
        new PinnedClientMailboxReceiptVerifier(
            new SodiumClientMailboxReceiptCrypto()),
        provisioned.DecodePolicies,
        provisioned.Authority,
        account => account == provisioned.LocalSessionId
            ? provisioned.SelfSelector
            : throw new InvalidOperationException(
                "MAU2 self selector was requested for another account."),
        ownsIngress: true,
        timeProvider: provisioned.TimeProvider,
        routeUsageObserver: routeUsageObserver);

    private BoundRuntime RequireBound()
    {
        ThrowIfDisposed();
        return Volatile.Read(ref bound) ?? throw new InvalidOperationException(
            "Native MAU2 transport has not bound an account identity.");
    }

    private static void RequireSession(BoundRuntime runtime, SessionId sessionId)
    {
        if (runtime.Provisioned.LocalSessionId != sessionId)
            throw new InvalidOperationException(
                "One app-private MAU2 runtime cannot cross account identities.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(
        Volatile.Read(ref disposed) != 0, this);

    private OperationLease EnterOperation()
    {
        lock (operationGate)
        {
            ThrowIfDisposed();
            if (lifecycleState != LifecycleState.Active)
                throw new OperationCanceledException(
                    "The MAU2 account generation no longer accepts operations.");
            activeOperations++;
            return new OperationLease(this);
        }
    }

    private void ExitOperation()
    {
        TaskCompletionSource? drained = null;
        lock (operationGate)
        {
            activeOperations--;
            if (activeOperations == 0)
            {
                drained = operationsDrained;
                operationsDrained = null;
            }
        }
        drained?.TrySetResult();
    }

    private sealed record BoundRuntime(
        ProvisionedMailboxRuntime Provisioned,
        NativeMau2MailboxTransport Transport,
        IReactiveMau2ReadRuntime ReactiveReads);

    private sealed class OperationLease(StoreBoundNativeMau2Transport owner) : IDisposable
    {
        private StoreBoundNativeMau2Transport? activeOwner = owner;

        public void Dispose() => Interlocked.Exchange(ref activeOwner, null)?.ExitOperation();
    }

    private enum LifecycleState
    {
        Active = 0,
        Refreshing = 1,
        Stopping = 2,
        Stopped = 3,
        Disposed = 4
    }
}
