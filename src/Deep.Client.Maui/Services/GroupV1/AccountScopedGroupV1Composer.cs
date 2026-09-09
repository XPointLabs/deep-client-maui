using System.Security.Cryptography;
using System.Text;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Domain.ContactV1;
using Deep.Client.Shared.Domain.GroupV1;
using Deep.Client.Shared.Persistence.ContactV1;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.Services.ContactV1;
using Deep.Client.Shared.Services.GroupV1;
using Deep.Protocol.ApplicationCore;
using Deep.Protocol.ContactV1;
using Deep.Protocol.DeepExtension.PrivacyRouting;
using Deep.Protocol.GroupV1;

namespace Deep.Client.Maui.Services.GroupV1;

/// <summary>
/// Clean-break GroupV1 composer. Durable ContactV1 evidence is reverified for
/// every draft add and again before authoring; UI projections never carry
/// protocol authority.
/// </summary>
internal sealed class AccountScopedGroupV1Composer : IGroupV1Composer
{
    private const uint OrdinaryEventExpirySeconds = 7 * 24 * 60 * 60;

    private readonly DeepAccountRuntimeAccessor accounts;
    private readonly IContactResolveRuntimePrerequisitesSource prerequisites;
    private readonly IOnionMonotonicClock monotonicClock;
    private readonly IClock clock;

    internal AccountScopedGroupV1Composer(
        DeepAccountRuntimeAccessor accounts,
        IContactResolveRuntimePrerequisitesSource prerequisites,
        IOnionMonotonicClock monotonicClock,
        IClock clock)
    {
        this.accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        this.prerequisites = prerequisites
            ?? throw new ArgumentNullException(nameof(prerequisites));
        this.monotonicClock = monotonicClock
            ?? throw new ArgumentNullException(nameof(monotonicClock));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask<GroupV1ComposerReadiness> GetReadinessAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var identity = await (await accounts.GetAccountsAsync(cancellationToken)
                .ConfigureAwait(false))
            .GetLocalIdentityAsync(cancellationToken)
            .ConfigureAwait(false);
        if (identity is null)
        {
            return Readiness(
                GroupV1ComposerAvailability.NoLocalAccount,
                "Создайте локальный Deep account перед созданием группы.");
        }

        try
        {
            if (await accounts.TryGetGroupV1RuntimeAsync(cancellationToken).ConfigureAwait(false)
                is null)
            {
                return Readiness(
                    GroupV1ComposerAvailability.CustodyRuntimeUnavailable,
                    "GroupV1 недоступна: не открыт account-scoped runtime или ключ custody текущего устройства.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            CryptographicException or IOException or InvalidOperationException)
        {
            return Readiness(
                GroupV1ComposerAvailability.CustodyRuntimeUnavailable,
                "GroupV1 недоступна: защищённое account/custody состояние не прошло проверку.");
        }

        ContactResolverTrustedVerifier? verifier;
        try
        {
            verifier = await TryGetVerifierAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            CryptographicException or IOException or InvalidOperationException)
        {
            return Readiness(
                GroupV1ComposerAvailability.ContactVerificationUnavailable,
                "GroupV1 недоступна: bootstrap ContactV1 не прошёл проверку для этой XPoint Network.");
        }
        if (verifier is null)
        {
            return Readiness(
                GroupV1ComposerAvailability.ContactVerificationUnavailable,
                "GroupV1 недоступна: проверяющий ContactV1 runtime ещё не подключён к этой XPoint Network.");
        }

        var persistence = await accounts.GetContactResolvePersistenceBindingAsync(cancellationToken)
            .ConfigureAwait(false);
        var ownerAddress = ParseCanonicalAddress(identity.Account.PermanentId.CanonicalText);
        try
        {
            if (await TryReverifyAsync(
                    persistence.ContactStore,
                    verifier,
                    ownerAddress,
                    cancellationToken).ConfigureAwait(false) is not { } owner
                || !owner.Bundle.Directory.Record.DeepAccountId.Span.SequenceEqual(
                    identity.Account.AccountIdentity.AccountId.Bytes.Span))
            {
                return Readiness(
                    GroupV1ComposerAvailability.OwnerContactCapabilityUnavailable,
                    "GroupV1 недоступна: постоянный Deep ID владельца ещё не опубликован и не проверен через ContactV1.");
            }
        }
        catch (GroupV1ComposerException)
        {
            return Readiness(
                GroupV1ComposerAvailability.OwnerContactCapabilityUnavailable,
                "GroupV1 недоступна: сохранённая capability постоянного Deep ID владельца не прошла повторную проверку ContactV1.");
        }

        return Readiness(
            GroupV1ComposerAvailability.Ready,
            "GroupV1 готова: account custody и ContactV1 capabilities проверены.");
    }

    public async ValueTask<GroupV1DraftMember> VerifyMemberAsync(
        string canonicalAddress,
        CancellationToken cancellationToken = default)
    {
        var parsed = ParseCanonicalAddress(canonicalAddress);
        var context = await RequireContextAsync(cancellationToken).ConfigureAwait(false);
        var verified = await ReverifyRequiredAsync(
                context.Store,
                context.Verifier,
                parsed,
                "Контакт ещё не достиг состояния Active с сохранённым проверяемым ContactV1 package.",
                cancellationToken)
            .ConfigureAwait(false);
        if (verified.Bundle.Directory.Record.DeepAccountId.Span.SequenceEqual(
                context.Runtime.AccountId.Bytes.Span))
        {
            throw Failure(
                "group-v1-member-is-owner",
                "Нельзя добавить собственный Deep ID как участника группы.");
        }

        return new GroupV1DraftMember(
            parsed.CanonicalText,
            ShortAddress(parsed.CanonicalText));
    }

    public async ValueTask<GroupV1ComposerGroup> CreateAsync(
        string groupName,
        IReadOnlyList<GroupV1DraftMember> members,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count > 99)
        {
            throw Failure(
                "group-v1-member-limit",
                "В GroupV1 может быть не более 100 участников вместе с владельцем.");
        }

        var context = await RequireContextAsync(cancellationToken).ConfigureAwait(false);
        var ownerAddress = ParseCanonicalAddress(context.Owner.Account.PermanentId.CanonicalText);
        var owner = await ReverifyRequiredAsync(
                context.Store,
                context.Verifier,
                ownerAddress,
                "Постоянный Deep ID владельца ещё не имеет проверяемого ContactV1 package.",
                cancellationToken)
            .ConfigureAwait(false);
        if (!owner.Bundle.Directory.Record.DeepAccountId.Span.SequenceEqual(
                context.Runtime.AccountId.Bytes.Span))
        {
            throw Failure(
                "group-v1-owner-binding",
                "Проверенная ContactV1 capability владельца относится к другому account.");
        }

        var seenAccounts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            ArgumentNullException.ThrowIfNull(member);
            var parsed = ParseCanonicalAddress(member.CanonicalAddress);
            var verified = await ReverifyRequiredAsync(
                    context.Store,
                    context.Verifier,
                    parsed,
                    "Участник больше не имеет активной проверяемой ContactV1 capability.",
                    cancellationToken)
                .ConfigureAwait(false);
            var accountKey = Convert.ToHexString(
                verified.Bundle.Directory.Record.DeepAccountId.Span);
            if (verified.Bundle.Directory.Record.DeepAccountId.Span.SequenceEqual(
                    context.Runtime.AccountId.Bytes.Span))
            {
                throw Failure(
                    "group-v1-member-is-owner",
                    "Собственный Deep ID не может повторяться в списке участников.");
            }
            if (!seenAccounts.Add(accountKey))
            {
                throw Failure(
                    "group-v1-duplicate-account",
                    "Два адреса разрешились в один и тот же Deep account.");
            }
        }

        if (members.Count != 0)
        {
            throw Failure(
                "group-v1-invitation-transport-unavailable",
                "Создание группы с участниками недоступно до подключения проверяемой GCF1/GSW1/GSS1 доставки и подтверждения приглашений.");
        }

        var now = checked((ulong)clock.UtcNow.ToUnixTimeSeconds());
        var monotonic = await monotonicClock.ReadAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw Failure(
                "group-v1-monotonic-clock",
                "Защищённые монотонные часы GroupV1 недоступны.");
        var groupId = GroupId32.FromBytes(RandomNonzero32());
        var operationId = GroupOperationId32.FromBytes(RandomNonzero32());
        var genesis = await context.Runtime.CreateAsync(
                operationId,
                new GroupGenesisAuthoringRequest(
                    groupId.Bytes.Span,
                    owner.Bundle,
                    context.Runtime.DeviceId.Bytes.Span,
                    groupName,
                    explicitHistoryTransfer: false,
                    OrdinaryEventExpirySeconds,
                    now,
                    monotonic.BootId.Span,
                    monotonic.SampleSeconds),
                cancellationToken)
            .ConfigureAwait(false);

        return new GroupV1ComposerGroup(
            Convert.ToHexStringLower(groupId.Bytes.Span),
            groupName,
            genesis.Persistence.Head?.MemberCount ?? 1,
            members.Count,
            genesis.Persistence.Head?.ForkLatched ?? false);
    }

    public async ValueTask<IReadOnlyList<GroupV1ComposerGroup>> ReadAllAsync(
        CancellationToken cancellationToken = default)
    {
        var runtime = await accounts.TryGetGroupV1RuntimeAsync(cancellationToken)
            .ConfigureAwait(false);
        if (runtime is null)
        {
            return [];
        }

        var heads = await runtime.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        return heads.Select(static head =>
        {
            var commit = GroupCodec.Decode("DGC1", head.ExactCanonicalCommit.Span);
            return new GroupV1ComposerGroup(
                Convert.ToHexStringLower(head.GroupId.Bytes.Span),
                Encoding.UTF8.GetString(commit.Field(13).Span),
                head.MemberCount,
                PendingInvitationCount: 0,
                head.ForkLatched);
        }).ToArray();
    }

    private async ValueTask<ComposerContext> RequireContextAsync(
        CancellationToken cancellationToken)
    {
        var accountService = await accounts.GetAccountsAsync(cancellationToken)
            .ConfigureAwait(false);
        var owner = await accountService.GetLocalIdentityAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw Failure(
                "group-v1-no-account",
                "Создайте локальный Deep account перед созданием группы.");
        var runtime = await accounts.TryGetGroupV1RuntimeAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw Failure(
                "group-v1-custody-unavailable",
                "GroupV1 runtime/custody текущего устройства недоступен.");
        if (!runtime.AccountId.Equals(owner.Account.AccountIdentity.AccountId)
            || !runtime.DeviceId.Equals(owner.Device.DeviceId))
        {
            throw Failure(
                "group-v1-runtime-binding",
                "GroupV1 runtime относится к другому account или устройству.");
        }

        var verifier = await TryGetVerifierAsync(cancellationToken).ConfigureAwait(false)
            ?? throw Failure(
                "group-v1-contact-verifier-unavailable",
                "Проверяющий ContactV1 runtime ещё не подключён к этой XPoint Network.");
        var persistence = await accounts.GetContactResolvePersistenceBindingAsync(cancellationToken)
            .ConfigureAwait(false);
        return new ComposerContext(owner, runtime, persistence.ContactStore, verifier);
    }

    private async ValueTask<ContactResolverTrustedVerifier?> TryGetVerifierAsync(
        CancellationToken cancellationToken)
    {
        var current = await prerequisites.GetCurrentAsync(cancellationToken)
            .ConfigureAwait(false);
        return current.UnavailableReason is null
            ? current.TrustedAuthorityVerifierFactory?.Invoke()
            : null;
    }

    private static async ValueTask<ContactResolverReverifiedPeerAuthority>
        ReverifyRequiredAsync(
            IContactStateStore store,
            ContactResolverTrustedVerifier verifier,
            CanonicalContactAddress address,
            string unavailableMessage,
            CancellationToken cancellationToken)
    {
        return await TryReverifyAsync(store, verifier, address, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Failure("group-v1-contact-not-verified", unavailableMessage);
    }

    private static async ValueTask<ContactResolverReverifiedPeerAuthority?>
        TryReverifyAsync(
            IContactStateStore store,
            ContactResolverTrustedVerifier verifier,
            CanonicalContactAddress address,
            CancellationToken cancellationToken)
    {
        var matches = (await store.ReadRelationshipsAsync(cancellationToken)
                .ConfigureAwait(false))
            .Where(relationship => relationship.State == ContactRelationshipState.Active
                && relationship.AddressKind == address.Kind
                && relationship.ExactCanonicalAddress.Span.SequenceEqual(
                    address.CanonicalBytes.Span))
            .ToArray();
        if (matches.Length != 1)
        {
            return null;
        }

        var package = await store.ReadVerifiedPeerPackageAsync(
                matches[0].RelationshipId,
                cancellationToken)
            .ConfigureAwait(false);
        if (package is null)
        {
            return null;
        }

        ContactResolverReverifiedPeerAuthority verified;
        try
        {
            verified = await verifier.ReverifyAsync(package, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CryptographicException exception)
        {
            throw Failure(
                "group-v1-contact-reverification",
                "Сохранённая ContactV1 capability устарела или не прошла повторную проверку.",
                exception);
        }
        if (verified.Evidence.Address.Kind != address.Kind
            || !verified.Evidence.Address.CanonicalBytes.Span.SequenceEqual(
                address.CanonicalBytes.Span)
            || !verified.Evidence.Scope.Equals(store.Scope))
        {
            throw Failure(
                "group-v1-contact-binding",
                "Повторно проверенная ContactV1 capability изменила адрес или account scope.");
        }
        return verified;
    }

    private static CanonicalContactAddress ParseCanonicalAddress(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || !string.Equals(input, input.Trim(), StringComparison.Ordinal))
        {
            throw Failure(
                "group-v1-address-canonical",
                "Введите canonical deep1… или deepinvite:DIA1 без пробелов.");
        }

        try
        {
            if (input.StartsWith(DeepInvitationTextCodec.Prefix, StringComparison.Ordinal))
            {
                var invitation = DeepInvitationTextCodec.DecodeCanonical(input);
                return new CanonicalContactAddress(
                    ContactAddressKind.OneTimeInvitation,
                    input,
                    invitation.CanonicalBytes);
            }

            var did = ApplicationCoreCodec.DecodeDeepIdText(input);
            if (!string.Equals(did.Text, input, StringComparison.Ordinal))
            {
                throw new FormatException("Permanent Deep ID is not canonical.");
            }
            return new CanonicalContactAddress(
                ContactAddressKind.PermanentDeepId,
                did.Text,
                did.CanonicalBytes);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw Failure(
                "group-v1-address-canonical",
                "Принимается только canonical permanent deep1… или canonical deepinvite:DIA1.",
                exception);
        }
    }

    private static string ShortAddress(string canonical) => canonical.Length <= 22
        ? canonical
        : string.Concat(canonical.AsSpan(0, 12), "…", canonical.AsSpan(canonical.Length - 6));

    private static byte[] RandomNonzero32()
    {
        while (true)
        {
            var value = RandomNumberGenerator.GetBytes(32);
            if (value.AsSpan().IndexOfAnyExcept((byte)0) >= 0)
            {
                return value;
            }
            CryptographicOperations.ZeroMemory(value);
        }
    }

    private static GroupV1ComposerReadiness Readiness(
        GroupV1ComposerAvailability availability,
        string message) => new(availability, message);

    private static GroupV1ComposerException Failure(
        string code,
        string message,
        Exception? inner = null) => new(code, message, inner);

    private sealed record CanonicalContactAddress(
        ContactAddressKind Kind,
        string CanonicalText,
        ReadOnlyMemory<byte> CanonicalBytes);

    private sealed record ComposerContext(
        DeepLocalIdentitySnapshot Owner,
        IDeepGroupV1Runtime Runtime,
        IContactStateStore Store,
        ContactResolverTrustedVerifier Verifier);
}
