using System.Buffers.Binary;
using System.Security.Cryptography;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Domain.AccountDirectoryV1;
using Deep.Client.Shared.Domain.GroupV1;
using Deep.Client.Shared.Domain.DeviceV1;
using Deep.Client.Shared.Domain.MessagingV1;
using Deep.Client.Shared.Domain.ContactV1;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Persistence.AccountDirectoryV1;
using Deep.Client.Shared.Persistence.ContactV1;
using Deep.Client.Shared.Persistence.GroupV1;
using Deep.Client.Shared.Persistence.DeviceV1;
using Deep.Client.Shared.Persistence.PreKeyV1;
using Deep.Client.Shared.Persistence.XPointNetworkV1;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.Services.ContactV1;
using Deep.Client.Shared.Services.GroupV1;
using Deep.Client.Shared.Services.MessagingV1;
using Deep.Client.Shared.Services.XPointNetworkV1;
using Deep.Protocol.DeepExtension.PrivacyRouting;
using Deep.Protocol.DeepExtension.MailboxCapabilities;
using Deep.Protocol.ApplicationCore;
using Deep.Protocol.AccountDirectoryV1;
using Deep.Protocol.ContactV1;
using Deep.Protocol.DeepNative;
using Deep.Protocol.GroupV1;
using Deep.Protocol.Identity;
using Deep.Protocol.MessagingCrypto;
using Deep.Protocol.MessagingWire;
using Deep.Protocol.XPointNetworkV1;
using Deep.Client.Maui.Services.GroupV1;

namespace Deep.Client.Maui.Services;

/// <summary>
/// Owns the complete offline account graph. Construction opens only app-private
/// files and platform key protection; it has no transport/bootstrap dependency.
/// </summary>
internal sealed class DeepAccountRuntimeOwner : IAsyncDisposable
{
    private const string RelativeStatePath = "deep-store-v1/account.dsv1";
    private const string RelativeContactStatePath = "deep-store-v1/contacts.dcv1";
    private const string RelativeContactAddressPublicationPath =
        "deep-store-v1/contact-publication.dcp1";
    private const string RelativeDeviceStatePath = "deep-store-v1/devices.dvs1";
    private const string RelativeXpk1ClaimJournalPath = "deep-store-v1/xpk1-claims.xcj1";
    private const string RelativeAccountDirectoryStatePath =
        "deep-store-v1/account-directory.ads1";
    private const string RelativeXPointNetworkStatePath = "deep-store-v1/xpoint-network.xlk1";
    private const string RelativeEntryGuardStatePath = "deep-store-v1/entry-guards.xgs1";
    private const string RelativeGroupStatePath = "deep-store-v1/groups.dgv1";
    private const string RelativeMailboxStatePath = "deep-store-v1/mailbox.dmb1";
    private const string RelativeGroupInvitationActivationPath =
        "deep-store-v1/group-invitation-activation.gia1";
    private const string RelativeContactResolveEntropyStatePath =
        "deep-store-v1/contact-resolve-entropy.cre1";
    private const string ContactKeySuffix = ".contact-state-key";
    private const string ContactAddressPublicationKeySuffix =
        ".contact-publication-key";
    private const string DeviceStateKeySuffix = ".device-state-key";
    private const string Xpk1ClaimJournalKeySuffix = ".xpk1-claim-journal-key";
    private const string AccountDirectoryKeySuffix = ".account-directory-state-key";
    private const string XPointNetworkKeySuffix = ".xpoint-network-state-key";
    private const string EntryGuardKeySuffix = ".xpoint-entry-guard-key";
    private const string GroupStateKeySuffix = ".group-v1-state-key";
    private const string MailboxStateKeySuffix = ".mailbox-v1-state-key";
    private const string MessagingDao1SeedSuffix = ".msg01-dao1-seed";
    private const string ContactUpdateRendezvousSuffix =
        ".contact-xur1-genesis";
    private const string GroupInvitationActivationKeySuffix =
        ".group-v1-invitation-activation-key";
    private const string ContactResolveEntropyAnchor0Suffix =
        ".contact-resolve-entropy-anchor-0";
    private const string ContactResolveEntropyAnchor1Suffix =
        ".contact-resolve-entropy-anchor-1";
    private readonly string appDataDirectory;
    private readonly JournaledDeepSecureStorage secureStorage;
    private readonly IDeepSecretProtector privacyStateProtector;
    private readonly SemaphoreSlim contactGate = new(1, 1);
    private readonly SemaphoreSlim contactAddressPublicationGate = new(1, 1);
    private readonly SemaphoreSlim deviceGate = new(1, 1);
    private readonly SemaphoreSlim xpk1ClaimJournalGate = new(1, 1);
    private readonly SemaphoreSlim accountDirectoryGate = new(1, 1);
    private readonly SemaphoreSlim xPointGate = new(1, 1);
    private readonly SemaphoreSlim groupGate = new(1, 1);
    private readonly SemaphoreSlim directMessagingGate = new(1, 1);
    private readonly SemaphoreSlim mailboxGate = new(1, 1);
    private readonly SemaphoreSlim messagingTransportGate = new(1, 1);
    private readonly SemaphoreSlim contactUpdateRendezvousGate = new(1, 1);
    private readonly SemaphoreSlim contactResolvePrivacyGate = new(1, 1);
    private readonly SemaphoreSlim activationGate = new(1, 1);
    private SqliteDeepAccountStore? store;
    private SqliteContactStateStore? contactStore;
    private SqliteContactAddressPublicationStore? contactAddressPublicationStore;
    private SqliteDeviceStateStore? deviceStateStore;
    private SqliteXpk1ClaimJournal? xpk1ClaimJournal;
    private ContactAddressImportService? contactImporter;
    private SqliteAccountDirectoryStateStore? accountDirectoryStore;
    private SqliteXPointNetworkStateStore? xPointNetworkStore;
    private SqliteProtectedEntryGuardStore? entryGuardStore;
    private SqliteGroupStateStore? groupStateStore;
    private SqliteGroupInvitationActivationStore? groupInvitationActivationStore;
    private DeepGroupV1RuntimeBinding? groupRuntimeBinding;
    private DeepDirectMessagingStorageFacade? directMessagingStorage;
    private SqliteDeepMailboxStore? mailboxStore;
#if DEEP_TEST_INTERNALS
    private DeepDirectMessagingStorageOwner? directMessagingStorageForTests;
#endif
    private ProtectedContactResolveEntropyLedger? contactResolveEntropyLedger;
    private DeepContactResolvePrivacyHostBinding? contactResolvePrivacyBinding;
    private DeepGenesisDeviceActivation? genesisActivation;
    private LocalDeviceX25519AgreementAuthority? localAgreementAuthority;

    private DeepAccountRuntimeOwner(
        string appDataDirectory,
        JournaledDeepSecureStorage secureStorage,
        IDeepSecretProtector privacyStateProtector,
        SqliteDeepAccountStore store,
        DeepAccountService accounts)
    {
        this.appDataDirectory = appDataDirectory;
        this.secureStorage = secureStorage;
        this.privacyStateProtector = privacyStateProtector
            ?? throw new ArgumentNullException(nameof(privacyStateProtector));
        this.store = store;
        Accounts = accounts;
    }

    internal DeepAccountService Accounts { get; }

    internal async Task<DeepGenesisDeviceActivation?> EnsureGenesisDeviceActivatedAsync(
        CancellationToken cancellationToken = default)
    {
        await activationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(store is null, this);
            var identity = await Accounts.GetLocalIdentityAsync(cancellationToken)
                .ConfigureAwait(false);
            if (identity is null) return null;
            var activated = genesisActivation ??
                await Accounts.EnsureGenesisDeviceActivatedAsync(cancellationToken)
                    .ConfigureAwait(false);
            var operationId = DeviceOperationId32.FromBytes(
                activated.CurrentDirectory.Head.Record.RecordHash.Span);
            var commit = await CommitCurrentDmd1Async(
                    operationId,
                    activated.CurrentDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
            if (commit.Disposition is not (
                ProtectedCurrentDmd1CommitDisposition.Applied or
                ProtectedCurrentDmd1CommitDisposition.ExactReplay))
                throw new InvalidOperationException(
                    $"Genesis DMD1 could not become current: {commit.Disposition}.");
            localAgreementAuthority ??=
                await Accounts.OpenCurrentDeviceAgreementAuthorityAsync(
                        identity,
                        activated.VerifiedDevice,
                        cancellationToken)
                    .ConfigureAwait(false);
            genesisActivation = activated;
            return activated;
        }
        finally
        {
            activationGate.Release();
        }
    }

    internal async Task<IGroupDeviceCustodySigner?> TryGetGroupDeviceCustodySignerAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(store is null, this);
        var identity = await Accounts.GetLocalIdentityAsync(cancellationToken)
            .ConfigureAwait(false);
        return identity is null
            ? null
            : new AccountOwnedDeviceCustodySigner(Accounts, secureStorage, identity);
    }

    internal async Task<IContactDeviceCustodySigner?> TryGetContactDeviceCustodySignerAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(store is null, this);
        var identity = await Accounts.GetLocalIdentityAsync(cancellationToken)
            .ConfigureAwait(false);
        return identity is null
            ? null
            : new AccountOwnedDeviceCustodySigner(Accounts, secureStorage, identity);
    }

    internal async ValueTask<DeepDirectMessagingMetadataSealingPublicKey>
        PrepareContactMetadataSealingKeyAsync(
            VerifiedContactRouteProposalAuthority proposal,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var messaging = await TryGetDirectMessagingStorageAsync(cancellationToken)
            .ConfigureAwait(false) ?? throw new CryptographicException(
                "The current device is not active for XRA1 key authoring.");
        return await messaging.PrepareMetadataSealingKeyAsync(
            proposal, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<VerifiedContactUpdateRendezvous>
        EnsureGenesisContactUpdateRendezvousAsync(
            VerifiedContactRouteClosure route,
            DeepDirectMessagingMetadataSealingPublicKey metadataKey,
            ulong issuedAtUnixSeconds,
            ulong expiresAtUnixSeconds,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(metadataKey);
        var activation = await EnsureGenesisDeviceActivatedAsync(cancellationToken)
            .ConfigureAwait(false) ?? throw new CryptographicException(
                "XUR1 authoring requires an activated current local device.");
        var signer = await TryGetContactDeviceCustodySignerAsync(cancellationToken)
            .ConfigureAwait(false) ?? throw new CryptographicException(
                "XUR1 authoring requires the current device custody signer.");
        var identity = await Accounts.GetLocalIdentityAsync(cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException(
                "No local Deep account exists.");
        var slot = ScopedKeySlot(
            identity.SecureSlots.MessageStoreInstanceId,
            ContactUpdateRendezvousSuffix,
            "ContactV1 update rendezvous");
        var parsedRoute = ContactRouteClosureCodec.Decode(
            ContactRouteClosureCodec.Encode(route));

        await contactUpdateRendezvousGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            using var existing = await secureStorage.ReadOwnedAsync(
                    slot, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                byte[]? encoded = null;
                try
                {
                    existing.Use(value => encoded = value.ToArray());
                    var restored = DecodeContactUpdateRendezvous(encoded!);
                    try
                    {
                        return ContactUpdateRendezvousAuthor.RecoverCurrent(
                            restored.ExactXur1,
                            activation.AddressBinding,
                            activation.CurrentDirectory,
                            parsedRoute,
                            restored.KeyId,
                            restored.PublicKey,
                            issuedAtUnixSeconds);
                    }
                    finally
                    {
                        ZeroContactUpdateRendezvous(restored);
                    }
                }
                finally
                {
                    if (encoded is not null)
                        CryptographicOperations.ZeroMemory(encoded);
                }
            }

            var authored = await ContactUpdateRendezvousAuthor.AuthorGenesisAsync(
                    activation.AddressBinding,
                    activation.CurrentDirectory,
                    route,
                    metadataKey.KeyId,
                    metadataKey.X25519PublicKey,
                    issuedAtUnixSeconds,
                    expiresAtUnixSeconds,
                    signer,
                    cancellationToken)
                .ConfigureAwait(false);
            var bundle = EncodeContactUpdateRendezvous(
                metadataKey.KeyId.Span,
                metadataKey.X25519PublicKey.Span,
                authored.ExactXur1.Span);
            try
            {
                await secureStorage.WriteBatchAsync(
                        [new DeepSecureStorageWrite(slot, bundle)],
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bundle);
            }
            return authored;
        }
        finally
        {
            contactUpdateRendezvousGate.Release();
        }
    }

    internal async ValueTask<VerifiedContactUpdateRendezvous?>
        TryOpenCurrentContactUpdateRendezvousAsync(
            ulong trustedUnixSeconds,
            CancellationToken cancellationToken = default)
    {
        var activation = await EnsureGenesisDeviceActivatedAsync(cancellationToken)
            .ConfigureAwait(false);
        if (activation is null)
            return null;
        var identity = await Accounts.GetLocalIdentityAsync(cancellationToken)
            .ConfigureAwait(false);
        if (identity is null)
            return null;
        var publicationStore = await GetContactAddressPublicationStoreAsync(
                cancellationToken)
            .ConfigureAwait(false);
        var publication = await publicationStore.ReadLatestConfirmedAsync(
                cancellationToken)
            .ConfigureAwait(false);
        if (publication is null)
            return null;
        var request = Xpu1Codec.Decode(publication.ExactXpu1.Span);
        var route = ContactRouteClosureCodec.Decode(request.ExactRouteClosure.Span);
        var slot = ScopedKeySlot(
            identity.SecureSlots.MessageStoreInstanceId,
            ContactUpdateRendezvousSuffix,
            "ContactV1 update rendezvous");

        await contactUpdateRendezvousGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            using var stored = await secureStorage.ReadOwnedAsync(
                    slot, cancellationToken)
                .ConfigureAwait(false);
            if (stored is null)
                return null;
            byte[]? encoded = null;
            try
            {
                stored.Use(value => encoded = value.ToArray());
                var restored = DecodeContactUpdateRendezvous(encoded!);
                try
                {
                    return ContactUpdateRendezvousAuthor.RecoverCurrent(
                        restored.ExactXur1,
                        activation.AddressBinding,
                        activation.CurrentDirectory,
                        route,
                        restored.KeyId,
                        restored.PublicKey,
                        trustedUnixSeconds);
                }
                finally
                {
                    ZeroContactUpdateRendezvous(restored);
                }
            }
            finally
            {
                if (encoded is not null)
                    CryptographicOperations.ZeroMemory(encoded);
            }
        }
        finally
        {
            contactUpdateRendezvousGate.Release();
        }
    }

    internal async ValueTask<DeepDirectMessagingInventoryPublication>
        EnsureGenesisDirectMessagingInventoryAsync(
            VerifiedContactPreKeyService preKeyService,
            VerifiedContactServicePlacement placement,
            ulong notBeforeUnixSeconds,
            ulong issuedAtUnixSeconds,
            ulong expiresAtUnixSeconds,
            ReadOnlyMemory<byte> publicationOperationId,
            ushort oneTimePreKeyCount,
            ushort lastResortReuseLimit,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preKeyService);
        ArgumentNullException.ThrowIfNull(placement);
        var activation = await EnsureGenesisDeviceActivatedAsync(cancellationToken)
            .ConfigureAwait(false) ?? throw new CryptographicException(
                "DPK2 inventory requires an activated current local device.");
        var messaging = await TryGetDirectMessagingStorageAsync(cancellationToken)
            .ConfigureAwait(false) ?? throw new CryptographicException(
                "DPK2 inventory requires an active direct-message store.");
        var request = new DeepDirectMessagingInventoryRequest(
            activation.CurrentDirectory,
            preKeyService,
            placement,
            inventoryEpoch: 1,
            notBeforeUnixSeconds,
            issuedAtUnixSeconds,
            expiresAtUnixSeconds,
            predecessorXpi1Hash: new byte[32],
            publicationOperationId.Span,
            oneTimePreKeyCount,
            lastResortReuseLimit);
        return await messaging.EnsureInventoryAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<DeepDirectMessagingStorageFacade?>
        TryGetDirectMessagingStorageAsync(
            CancellationToken cancellationToken = default)
    {
        var activation = await EnsureGenesisDeviceActivatedAsync(cancellationToken)
            .ConfigureAwait(false);
        if (activation is null) return null;
        var agreement = localAgreementAuthority ??
            throw new InvalidOperationException("Local agreement authority was not activated.");

        await directMessagingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(store is null, this);
            if (directMessagingStorage is not null)
            {
                if (!directMessagingStorage.IsBoundTo(
                        agreement,
                        activation.VerifiedDevice))
                {
                    throw new CryptographicException(
                        "The direct-message owner is already bound to another verified local authority.");
                }
                return directMessagingStorage;
            }

            directMessagingStorage = await DeepDirectMessagingStorageFacade.OpenAsync(
                    appDataDirectory,
                    Accounts,
                    agreement,
                    activation.VerifiedDevice,
                    cancellationToken)
                .ConfigureAwait(false);
            return directMessagingStorage;
        }
        finally
        {
            directMessagingGate.Release();
        }
    }

#if !DEEP_TEST_INTERNALS
    internal async ValueTask<DeepDirectMessagingOpenedDeposit?>
        TryOpenDirectInboundMailboxEntryAsync(
            VerifiedContactRouteClosure currentLocalRoute,
            ScopedMailboxResolvedRoute currentMailboxRoute,
            ulong cursor,
            ReadOnlyMemory<byte> exactMeo1,
            ReadOnlyMemory<byte> externalEnvelopeDigest,
            MailboxClientDecodePolicy decodePolicy,
            CancellationToken cancellationToken = default)
    {
        var messaging = await TryGetDirectMessagingStorageAsync(cancellationToken)
            .ConfigureAwait(false);
        return messaging is null ? null :
            await messaging.OpenInboundMailboxEntryAsync(
                    currentLocalRoute, currentMailboxRoute, cursor, exactMeo1,
                    externalEnvelopeDigest, decodePolicy, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Previews the exact received DPH2 using the account-owned local prekey,
    /// then verifies its encrypted claim against live directory authority.
    /// No session, inbox, contact or mailbox ACK is committed here.
    /// </summary>
    internal async ValueTask<VerifiedDph2InitialClaim?>
        TryVerifyResponderInitialClaimAsync(
            Dph2Record initiation,
            VerifiedDpk2Offering localOffering,
            Dmd1LineageState initiatorDirectory,
            VerifiedAccountDirectoryFreshness initiatorFreshness,
            VerifiedContactServicePlacement claimPlacement,
            VerifiedContactNetworkAuthority recipientAuthority,
            VerifiedContactBundleClosure recipientBundle,
            OnionTrustedTimeAuthority trustedTimeAuthority,
            int maximumMessagesWithoutPqInjection,
            CancellationToken cancellationToken = default)
    {
        var messaging = await TryGetDirectMessagingStorageAsync(cancellationToken)
            .ConfigureAwait(false);
        return messaging is null
            ? null
            : await messaging.TryVerifyResponderInitialClaimAsync(
                    initiation, localOffering, initiatorDirectory,
                    initiatorFreshness, claimPlacement, recipientAuthority,
                    recipientBundle, trustedTimeAuthority,
                    maximumMessagesWithoutPqInjection, cancellationToken)
                .ConfigureAwait(false);
    }

    internal async ValueTask<VerifiedDph2InitialClaim?>
        TryVerifyResponderInitialClaimAsync(
            Dph2Record initiation,
            Dmd1LineageState initiatorDirectory,
            VerifiedAccountDirectoryFreshness initiatorFreshness,
            VerifiedContactServicePlacement claimPlacement,
            VerifiedContactNetworkAuthority recipientAuthority,
            VerifiedContactBundleClosure recipientBundle,
            OnionTrustedTimeAuthority trustedTimeAuthority,
            int maximumMessagesWithoutPqInjection,
            CancellationToken cancellationToken = default)
    {
        var messaging = await TryGetDirectMessagingStorageAsync(cancellationToken)
            .ConfigureAwait(false);
        return messaging is null
            ? null
            : await messaging.TryVerifyResponderInitialClaimAsync(
                    initiation, initiatorDirectory, initiatorFreshness,
                    claimPlacement, recipientAuthority, recipientBundle,
                    trustedTimeAuthority, maximumMessagesWithoutPqInjection,
                    cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Stages an unsolicited DPH2 only after its verified ContactHello endpoint
    /// is checked. Contact state, semantic inbox and mailbox ACK remain pending.
    /// </summary>
    internal async ValueTask<DeepDirectMessagingUnsolicitedCommitResult?>
        TryCommitUnsolicitedResponderSessionAsync(
            VerifiedDph2InitialClaim? verifiedInitial,
            int maximumMessagesWithoutPqInjection,
            CancellationToken cancellationToken = default)
    {
        var messaging = await TryGetDirectMessagingStorageAsync(cancellationToken)
            .ConfigureAwait(false);
        return messaging is null ? null :
            await messaging.TryCommitUnsolicitedResponderSessionAsync(
                    verifiedInitial, maximumMessagesWithoutPqInjection,
                    cancellationToken)
                .ConfigureAwait(false);
    }
#endif

    /// <summary>
    /// Opens the clean account-wide mailbox and semantic inbox with a distinct
    /// protected SQLCipher key. A pre-existing file without its key is never
    /// silently replaced, because it may contain unacknowledged deposits.
    /// </summary>
    internal async Task<SqliteDeepMailboxStore?> TryGetMailboxStoreAsync(
        CancellationToken cancellationToken = default)
    {
        await mailboxGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(store is null, this);
            if (mailboxStore is not null) return mailboxStore;
            var identity = await Accounts.GetLocalIdentityAsync(cancellationToken)
                .ConfigureAwait(false);
            if (identity is null) return null;

            var keySlot = ScopedKeySlot(identity.SecureSlots.MessageStoreInstanceId,
                MailboxStateKeySuffix, "clean mailbox");
            var path = Path.Combine(appDataDirectory, RelativeMailboxStatePath);
            var existed = SqliteFamilyExists(path);
            var key = await ReadScopedKeyAsync(keySlot, "clean mailbox",
                    cancellationToken).ConfigureAwait(false);
            if (key is null && existed)
                throw InvalidScopedKey(
                    "The clean mailbox database exists without its protected SQLCipher key.");
            if (key is not null && !existed)
            {
                CryptographicOperations.ZeroMemory(key);
                throw InvalidScopedKey(
                    "The protected clean mailbox key exists without its database generation.");
            }
            var createdKey = key is null;
            key ??= CreateNonzeroKey();
            try
            {
                if (createdKey)
                    await secureStorage.WriteBatchAsync(
                            [new DeepSecureStorageWrite(keySlot, key)],
                            cancellationToken).ConfigureAwait(false);
                using var options = new SqliteDeepMailboxStoreOptions(path, key);
                var opened = new SqliteDeepMailboxStore(options);
                mailboxStore = opened;
                return opened;
            }
            catch
            {
                if (createdKey && !existed)
                {
                    DeleteSqliteFamily(path);
                    await secureStorage.DeleteBatchAsync([keySlot], CancellationToken.None)
                        .ConfigureAwait(false);
                }
                throw;
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        finally { mailboxGate.Release(); }
    }

    internal async Task<IReadOnlyList<DirectMessageCreateSnapshot>>
        ListDirectMessageCreatesAsync(
            ContactConversationId32 conversationId,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        var identity = await Accounts.GetLocalIdentityAsync(cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException(
                "No local Deep account exists.");
        var inbox = await TryGetMailboxStoreAsync(cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException(
                "The account-wide message inbox is unavailable.");
        return await inbox.ListDirectMessageCreatesAsync(
                identity.Account.AccountIdentity.AccountId.Bytes,
                identity.Account.AccountIdentity.AccountGeneration,
                conversationId.ToArray(),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    internal async ValueTask<ReachabilityMailboxHolderAuthority.ReachabilityMailboxHolderSigner?>
        OpenReachabilityMailboxHolderAsync(
            VerifiedContactRouteClosure route,
            ReadOnlyMemory<byte> locatorHash,
            MailboxCapabilityDomain domain,
            CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(store is null, this);
        var identity = await Accounts.GetLocalIdentityAsync(cancellationToken)
            .ConfigureAwait(false);
        if (identity is null)
            return null;
        return await new ReachabilityMailboxHolderAuthority(secureStorage)
            .OpenOrCreateAsync(
                identity,
                route,
                locatorHash,
                domain,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<PrivacyRoutedInitialSessionDispatcher?>
        CreateInitialSessionDispatcherAsync(
            VerifiedCurrentMailboxGrant grant,
            ReachabilityMailboxHolderAuthority.ReachabilityMailboxHolderSigner holder,
            PrivacyMailboxRoute primaryRoute,
            PrivacyMailboxRoute fallbackRoute,
            PrivacyRoutingCodec codec,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(holder);
        var mailbox = await TryGetMailboxStoreAsync(cancellationToken)
            .ConfigureAwait(false);
        if (mailbox is null)
            return null;

        await messagingTransportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(store is null, this);
            var identity = await Accounts.GetLocalIdentityAsync(cancellationToken)
                .ConfigureAwait(false);
            if (identity is null)
                return null;
            var seedSlot = ScopedKeySlot(
                identity.SecureSlots.MessageStoreInstanceId,
                MessagingDao1SeedSuffix,
                "MSG-01 DAO1 sealing seed");
            var seed = await ReadScopedKeyAsync(
                    seedSlot,
                    "MSG-01 DAO1 sealing seed",
                    cancellationToken)
                .ConfigureAwait(false);
            var created = seed is null;
            seed ??= CreateNonzeroKey();
            try
            {
                if (created)
                {
                    await secureStorage.WriteBatchAsync(
                            [new DeepSecureStorageWrite(seedSlot, seed)],
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                return PrivacyRoutedInitialSessionDispatcher.Create(
                    identity,
                    mailbox,
                    grant,
                    holder,
                    primaryRoute,
                    fallbackRoute,
                    codec,
                    seed);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(seed);
            }
        }
        finally
        {
            messagingTransportGate.Release();
        }
    }

    internal async Task<PrivacyRoutedMessagingReceiver?>
        CreateMessagingReceiverAsync(
            VerifiedCurrentMailboxGrant grant,
            ReachabilityMailboxHolderAuthority.ReachabilityMailboxHolderSigner holder,
            ParsedContactRouteClosure currentLocalRoute,
            PrivacyMailboxRoute primaryRoute,
            PrivacyMailboxRoute fallbackRoute,
            PrivacyRoutingCodec codec,
            ProductionContactResolvePathAuthoritySource pathAuthority,
            VerifiedLocalMessagingRecipient localRecipient,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(currentLocalRoute);
        ArgumentNullException.ThrowIfNull(pathAuthority);
        ArgumentNullException.ThrowIfNull(localRecipient);
        var mailbox = await TryGetMailboxStoreAsync(cancellationToken)
            .ConfigureAwait(false);
        var messaging = await TryGetDirectMessagingStorageAsync(cancellationToken)
            .ConfigureAwait(false);
        if (mailbox is null || messaging is null)
            return null;

        await messagingTransportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(store is null, this);
            var identity = await Accounts.GetLocalIdentityAsync(cancellationToken)
                .ConfigureAwait(false);
            if (identity is null)
                return null;
            var seedSlot = ScopedKeySlot(
                identity.SecureSlots.MessageStoreInstanceId,
                MessagingDao1SeedSuffix,
                "MSG-01 DAO1 sealing seed");
            var seed = await ReadScopedKeyAsync(
                    seedSlot,
                    "MSG-01 DAO1 sealing seed",
                    cancellationToken)
                .ConfigureAwait(false);
            var created = seed is null;
            seed ??= CreateNonzeroKey();
            try
            {
                if (created)
                    await secureStorage.WriteBatchAsync(
                            [new DeepSecureStorageWrite(seedSlot, seed)],
                            cancellationToken)
                        .ConfigureAwait(false);
                return PrivacyRoutedMessagingReceiver.Create(
                    identity,
                    mailbox,
                    messaging,
                    currentLocalRoute,
                    grant,
                    holder,
                    primaryRoute,
                    fallbackRoute,
                    codec,
                    seed,
                    pathAuthority,
                    localRecipient);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(seed);
            }
        }
        finally
        {
            messagingTransportGate.Release();
        }
    }

    internal async ValueTask<DeepDirectMessagingInitiatorClaimStart?>
        TryBeginDirectMessagingInitiatorClaimAsync(
            ContactResolverReverifiedPeerAuthority? verifiedPeer,
            CancellationToken cancellationToken = default)
    {
        if (verifiedPeer is null)
        {
            return null;
        }
        var activation = await EnsureGenesisDeviceActivatedAsync(cancellationToken)
            .ConfigureAwait(false);
        if (activation is null)
        {
            return null;
        }
        var agreement = localAgreementAuthority ??
            throw new InvalidOperationException(
                "Local agreement authority was not activated.");
        var messaging = await TryGetDirectMessagingStorageAsync(cancellationToken)
            .ConfigureAwait(false);
        return messaging is null
            ? null
            : await messaging.TryBeginInitiatorClaimAsync(
                    verifiedPeer,
                    agreement,
                    activation.CurrentDirectory,
                    activation.AddressBinding,
                    cancellationToken)
                .ConfigureAwait(false);
    }

    internal async ValueTask<DeepDirectMessagingInitiatorClaimPreparation?>
        TryCompleteDirectMessagingInitiatorClaimAsync(
            DeepDirectMessagingInitiatorClaimStart? startedClaim,
            VerifiedXpc1PreKeyClaimReceipt? verifiedClaim,
            int maximumMessagesWithoutPqInjection,
            CancellationToken cancellationToken = default)
    {
        if (startedClaim is null || verifiedClaim is null)
        {
            startedClaim?.Dispose();
            return null;
        }

        var delegated = false;
        LocalDeviceX25519AgreementLease? lease = null;
        try
        {
            var operation = startedClaim.ClaimOperationId.ToArray();
            var claimedOperation = verifiedClaim.OperationId.ToArray();
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(operation, claimedOperation))
                {
                    throw new CryptographicException(
                        "The verified XPC1 receipt belongs to another initiator claim.");
                }
                var activation = await EnsureGenesisDeviceActivatedAsync(cancellationToken)
                    .ConfigureAwait(false) ?? throw new CryptographicException(
                        "The current local device is not activated for direct messaging.");
                var agreement = localAgreementAuthority ?? throw new CryptographicException(
                    "The current local agreement authority is unavailable.");
                var authorized = await AuthorizeAndRedeemDeviceAgreementAsync(
                        DeviceOperationId32.FromBytes(operation),
                        activation.CurrentDirectory,
                        agreement,
                        LocalDeviceX25519AgreementPurpose.Dph2InitiatorDh1,
                        operation,
                        verifiedClaim.Offering.InitiatorAgreementPeerPublicKey,
                        cancellationToken)
                    .ConfigureAwait(false);
                lease = authorized.Lease;
                if (authorized.Disposition != ProtectedDeviceAgreementDisposition.Granted ||
                    lease is null)
                {
                    throw new CryptographicException(
                        $"The protected DPH2 agreement operation was not granted: {authorized.Disposition}.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(operation);
                CryptographicOperations.ZeroMemory(claimedOperation);
            }

            var messaging = await TryGetDirectMessagingStorageAsync(cancellationToken)
                .ConfigureAwait(false);
            if (messaging is null)
            {
                return null;
            }
            delegated = true;
            var prepared = await messaging.TryCompleteInitiatorClaimAsync(
                    startedClaim,
                    verifiedClaim.Offering,
                    lease,
                    maximumMessagesWithoutPqInjection,
                    cancellationToken)
                .ConfigureAwait(false);
            lease = null;
            return prepared;
        }
        finally
        {
            if (!delegated)
            {
                startedClaim.Dispose();
                lease?.Dispose();
            }
        }
    }

    internal async ValueTask<DeepDirectMessagingInitiatorCommitResult?>
        TryCommitDirectMessagingInitiatorSessionAsync(
            DeepDirectMessagingInitiatorClaimPreparation? preparedClaim,
            VerifiedXpc1PreKeyClaimReceipt? verifiedClaim,
            CancellationToken cancellationToken = default)
    {
        var delegated = false;
        try
        {
            var messaging = await TryGetDirectMessagingStorageAsync(cancellationToken)
                .ConfigureAwait(false);
            if (messaging is null)
            {
                return null;
            }
            delegated = true;
            return await messaging.TryCommitInitiatorSessionAsync(
                    preparedClaim,
                    verifiedClaim,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (!delegated)
            {
                preparedClaim?.Dispose();
            }
        }
    }

    internal async ValueTask<DeepDirectMessagingInitiatorCommitResult?>
        TryCommitDirectMessagingInitiatorSessionAsync(
            DeepDirectMessagingInitiatorClaimPreparation? preparedClaim,
            VerifiedXpc1PreKeyClaimReceipt? verifiedClaim,
            AuthoredVerifiedContactHello? contactHello,
            CancellationToken cancellationToken = default)
    {
        var delegated = false;
        try
        {
            var messaging = await TryGetDirectMessagingStorageAsync(cancellationToken)
                .ConfigureAwait(false);
            if (messaging is null)
                return null;
            delegated = true;
            return await messaging.TryCommitInitiatorSessionAsync(
                    preparedClaim,
                    verifiedClaim,
                    contactHello,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (!delegated)
                preparedClaim?.Dispose();
        }
    }

#if DEEP_TEST_INTERNALS
    internal async Task<DeepDirectMessagingStorageOwner?>
        TryGetDirectMessagingStorageAsync(
            DeepDirectMessagingLocalAuthorityBinding? verifiedLocalAuthority,
            CancellationToken cancellationToken = default)
    {
        if (verifiedLocalAuthority is null)
        {
            return null;
        }

        await directMessagingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(store is null, this);
            if (directMessagingStorageForTests is not null)
            {
                if (!directMessagingStorageForTests.MatchesAuthority(verifiedLocalAuthority))
                {
                    throw new CryptographicException(
                        "The direct-message owner is already bound to another verified local authority.");
                }
                return directMessagingStorageForTests;
            }

            var identity = await Accounts.GetLocalIdentityAsync(cancellationToken)
                .ConfigureAwait(false);
            if (identity is null)
            {
                return null;
            }
            directMessagingStorageForTests = await DeepDirectMessagingStorageOwner.OpenAsync(
                    appDataDirectory,
                    secureStorage,
                    Accounts,
                    identity,
                    verifiedLocalAuthority,
                    cancellationToken)
                .ConfigureAwait(false);
            return directMessagingStorageForTests;
        }
        finally
        {
            directMessagingGate.Release();
        }
    }

    internal async ValueTask<DeepDirectMessagingInventoryPublication?>
        EnsureDirectMessagingInventoryAsync(
            DeepDirectMessagingLocalAuthorityBinding? verifiedLocalAuthority,
            PreKeyV1InventoryAuthoringContext? authoringContext,
            CancellationToken cancellationToken = default)
    {
        var messaging = await TryGetDirectMessagingStorageAsync(
                verifiedLocalAuthority,
                cancellationToken)
            .ConfigureAwait(false);
        return messaging is null
            ? null
            : await messaging.EnsureInventoryAsync(authoringContext, cancellationToken)
                .ConfigureAwait(false);
    }

    internal async ValueTask<DeepDirectMessagingInitiatorClaimStart?>
        TryBeginDirectMessagingInitiatorClaimAsync(
            DeepDirectMessagingLocalAuthorityBinding? verifiedLocalAuthority,
            ContactResolverReverifiedPeerAuthority? verifiedPeer,
            CancellationToken cancellationToken = default)
    {
        var messaging = await TryGetDirectMessagingStorageAsync(
                verifiedLocalAuthority,
                cancellationToken)
            .ConfigureAwait(false);
        if (messaging is null)
        {
            return null;
        }
        var activation = await EnsureGenesisDeviceActivatedAsync(cancellationToken)
            .ConfigureAwait(false);
        return await messaging.TryBeginInitiatorClaimAsync(
                verifiedPeer,
                localAgreementAuthority,
                activation?.CurrentDirectory,
                activation?.AddressBinding,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async ValueTask<DeepDirectMessagingInitiatorClaimPreparation?>
        TryCompleteDirectMessagingInitiatorClaimAsync(
            DeepDirectMessagingLocalAuthorityBinding? verifiedLocalAuthority,
            DeepDirectMessagingInitiatorClaimStart? startedClaim,
            VerifiedDpk2Offering? verifiedOffering,
            LocalDeviceX25519AgreementLease? deviceAgreementLease,
            int maximumMessagesWithoutPqInjection,
            CancellationToken cancellationToken = default)
    {
        var delegated = false;
        try
        {
            var messaging = await TryGetDirectMessagingStorageAsync(
                    verifiedLocalAuthority,
                    cancellationToken)
                .ConfigureAwait(false);
            if (messaging is null)
            {
                return null;
            }
            delegated = true;
            return await messaging.TryCompleteInitiatorClaimAsync(
                    startedClaim,
                    verifiedOffering,
                    deviceAgreementLease,
                    maximumMessagesWithoutPqInjection,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (!delegated)
            {
                startedClaim?.Dispose();
                deviceAgreementLease?.Dispose();
            }
        }
    }

    internal async ValueTask<DeepDirectMessagingInitiatorCommitResult?>
        TryCommitDirectMessagingInitiatorSessionAsync(
            DeepDirectMessagingLocalAuthorityBinding? verifiedLocalAuthority,
            DeepDirectMessagingInitiatorClaimPreparation? preparedClaim,
            VerifiedXpc1PreKeyClaimReceipt? verifiedClaim,
            ReadOnlyMemory<byte> exactSessionInitDmc2,
            ReadOnlyMemory<byte> exactFirstApplicationDmc2 = default,
            CancellationToken cancellationToken = default)
    {
        var delegated = false;
        try
        {
            var messaging = await TryGetDirectMessagingStorageAsync(
                    verifiedLocalAuthority,
                    cancellationToken)
                .ConfigureAwait(false);
            if (messaging is null)
            {
                return null;
            }
            delegated = true;
            return await messaging.TryCommitInitiatorSessionAsync(
                    preparedClaim,
                    verifiedClaim,
                    exactSessionInitDmc2,
                    exactFirstApplicationDmc2,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (!delegated)
            {
                preparedClaim?.Dispose();
            }
        }
    }

    internal async ValueTask<PreKeyV1InitialSessionSagaResult?>
        TryCommitDirectMessagingResponderSessionAsync(
            DeepDirectMessagingLocalAuthorityBinding? verifiedLocalAuthority,
            DeepDirectMessagingVerifiedSessionBinding? verifiedSession,
            VerifiedContactBundleEvidence? relationship,
            VerifiedXpc1PreKeyClaimReceipt? verifiedClaim,
            VerifiedDph2Initiation? verifiedInitiation,
            int maximumMessagesWithoutPqInjection,
            CancellationToken cancellationToken = default)
    {
        var messaging = await TryGetDirectMessagingStorageAsync(
                verifiedLocalAuthority,
                cancellationToken)
            .ConfigureAwait(false);
        if (messaging is null) return null;
        var result = await messaging.TryCommitResponderSessionAsync(
                    verifiedSession,
                    relationship,
                    verifiedClaim,
                    verifiedInitiation,
                    maximumMessagesWithoutPqInjection,
                    cancellationToken)
                .ConfigureAwait(false);
        if (result?.Disposition is PreKeyV1InitialSessionSagaDisposition.Initialized or
            PreKeyV1InitialSessionSagaDisposition.ExactReplay)
        {
            var inbox = await TryGetMailboxStoreAsync(cancellationToken)
                .ConfigureAwait(false) ?? throw new CryptographicException(
                    "The account inbox is unavailable after the initial session commit.");
            var materialized = await messaging.TryMaterializeInitialReceiveAsync(
                    verifiedSession, relationship, verifiedInitiation, inbox, cancellationToken)
                .ConfigureAwait(false);
            if (materialized is not (DirectDmc2InboxDisposition.Materialized or
                    DirectDmc2InboxDisposition.ExactReplay))
                throw new CryptographicException(
                    "The initial authenticated DMC2 batch was not durably materialized.");
        }
        return result;
    }
#endif

    internal async Task<DeepGroupV1RuntimeBinding?> TryGetGroupV1RuntimeBindingAsync(
        CancellationToken cancellationToken = default)
    {
        await groupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(store is null, this);
            if (groupRuntimeBinding is not null)
            {
                return groupRuntimeBinding;
            }

            var identity = await Accounts.GetLocalIdentityAsync(cancellationToken)
                .ConfigureAwait(false);
            if (identity is null)
            {
                return null;
            }
            if (!identity.Account.AccountIdentity.NetworkId.Matches(identity.NetworkId.Span))
            {
                throw new LocalStateResetRequiredException(
                    LocalStateResetRequiredReason.InvalidCurrentSchema,
                    "The GroupV1 runtime identity belongs to another network.");
            }

            var instanceId = await ReadMessageStoreInstanceIdAsync(
                    identity.SecureSlots.MessageStoreInstanceId,
                    cancellationToken)
                .ConfigureAwait(false);
            byte[]? key = null;
            try
            {
                var keySlot = ScopedKeySlot(
                    identity.SecureSlots.MessageStoreInstanceId,
                    GroupStateKeySuffix,
                    "GroupV1");
                var activationKeySlot = ScopedKeySlot(
                    identity.SecureSlots.MessageStoreInstanceId,
                    GroupInvitationActivationKeySuffix,
                    "GroupV1 invitation activation");
                key = await ReadScopedKeyAsync(keySlot, "GroupV1", cancellationToken)
                    .ConfigureAwait(false);
                byte[]? activationKey = null;
                var groupStatePath = Path.Combine(appDataDirectory, RelativeGroupStatePath);
                var activationStatePath = Path.Combine(
                    appDataDirectory,
                    RelativeGroupInvitationActivationPath);
                var databaseExisted = SqliteFamilyExists(groupStatePath);
                var activationDatabaseExisted = SqliteFamilyExists(activationStatePath);
                if (key is null && databaseExisted)
                {
                    throw new LocalStateResetRequiredException(
                        LocalStateResetRequiredReason.InvalidCurrentSchema,
                        "The GroupV1 database exists without its protected SQLCipher key.");
                }
                activationKey = await ReadScopedKeyAsync(
                        activationKeySlot,
                        "GroupV1 invitation activation",
                        cancellationToken)
                    .ConfigureAwait(false);
                if (activationKey is null && activationDatabaseExisted)
                {
                    throw new LocalStateResetRequiredException(
                        LocalStateResetRequiredReason.InvalidCurrentSchema,
                        "The GroupV1 invitation activation database exists without its protected SQLCipher key.");
                }
                var createdKey = key is null;
                var createdActivationKey = activationKey is null;
                key ??= CreateNonzeroKey();
                activationKey ??= CreateNonzeroKey();
                SqliteGroupStateStore? openedStore = null;
                SqliteGroupInvitationActivationStore? openedActivationStore = null;
                try
                {
                    if (CryptographicOperations.FixedTimeEquals(key, activationKey))
                    {
                        throw new LocalStateResetRequiredException(
                            LocalStateResetRequiredReason.InvalidCurrentSchema,
                            "The GroupV1 state and invitation activation SQLCipher keys are not distinct.");
                    }
                    var writes = new List<DeepSecureStorageWrite>(2);
                    if (createdKey)
                    {
                        writes.Add(new DeepSecureStorageWrite(keySlot, key));
                    }
                    if (createdActivationKey)
                    {
                        writes.Add(new DeepSecureStorageWrite(
                            activationKeySlot,
                            activationKey));
                    }
                    if (writes.Count != 0)
                    {
                        await secureStorage.WriteBatchAsync(writes, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    var groupScope = GroupStoreScope.ForCurrentAccount(
                        identity.Account.AccountIdentity.AccountId,
                        identity.Account.AccountIdentity.AccountGeneration);
                    using var options = new SqliteGroupStateStoreOptions(
                        groupStatePath,
                        key,
                        groupScope);
                    openedStore = new SqliteGroupStateStore(options);
                    using var activationOptions =
                        new SqliteGroupInvitationActivationStoreOptions(
                            activationStatePath,
                            activationKey,
                            groupScope);
                    openedActivationStore =
                        new SqliteGroupInvitationActivationStore(activationOptions);
                    var heads = await openedStore.ReadHeadsAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (heads.Any(head => !head.NetworkId.Span.SequenceEqual(identity.NetworkId.Span)))
                    {
                        throw new LocalStateResetRequiredException(
                            LocalStateResetRequiredReason.InvalidCurrentSchema,
                            "The GroupV1 database contains state for another network.");
                    }

                    var messageScope = new MessageStoreScope(
                        MessagingAccountId32.FromBytes(
                            identity.Account.AccountIdentity.AccountId.Bytes.Span),
                        identity.Account.AccountIdentity.AccountGeneration,
                        MessageStoreInstanceId32.FromBytes(instanceId));
                    var binding = new DeepGroupV1RuntimeBinding(
                        openedStore,
                        openedActivationStore,
                        messageScope,
                        new DeepGroupV1Runtime(
                            identity.Account.AccountIdentity.AccountId,
                            identity.Device.DeviceId,
                            new AccountOwnedDeviceCustodySigner(
                                Accounts,
                                secureStorage,
                                identity),
                            openedStore));
                    groupStateStore = openedStore;
                    groupInvitationActivationStore = openedActivationStore;
                    groupRuntimeBinding = binding;
                    openedStore = null;
                    openedActivationStore = null;
                    return binding;
                }
                catch
                {
                    openedStore?.Dispose();
                    openedActivationStore?.Dispose();
                    var createdSlots = new List<string>(2);
                    if (createdKey && !databaseExisted)
                    {
                        createdSlots.Add(keySlot);
                        DeleteSqliteFamily(groupStatePath);
                    }
                    if (createdActivationKey && !activationDatabaseExisted)
                    {
                        createdSlots.Add(activationKeySlot);
                        DeleteSqliteFamily(activationStatePath);
                    }
                    if (createdSlots.Count != 0)
                    {
                        await secureStorage.DeleteBatchAsync(
                                createdSlots,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    throw;
                }
                finally
                {
                    if (activationKey is not null)
                    {
                        CryptographicOperations.ZeroMemory(activationKey);
                    }
                }
            }
            finally
            {
                if (key is not null)
                {
                    CryptographicOperations.ZeroMemory(key);
                }
                CryptographicOperations.ZeroMemory(instanceId);
            }
        }
        finally
        {
            groupGate.Release();
        }
    }

    internal async Task<IXPointNetworkStateStore> GetXPointNetworkStateStoreAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureXPointStoresOpenAsync(cancellationToken).ConfigureAwait(false);
        return xPointNetworkStore
            ?? throw new ObjectDisposedException(nameof(DeepAccountRuntimeOwner));
    }

    internal async Task<IProtectedEntryGuardStore> GetProtectedEntryGuardStoreAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureXPointStoresOpenAsync(cancellationToken).ConfigureAwait(false);
        return entryGuardStore
            ?? throw new ObjectDisposedException(nameof(DeepAccountRuntimeOwner));
    }

    internal async Task<ContactAddressImportService> GetContactImporterAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureContactStoreOpenAsync(cancellationToken).ConfigureAwait(false);
        return contactImporter
            ?? throw new ObjectDisposedException(nameof(DeepAccountRuntimeOwner));
    }

    internal async Task<SqliteXpk1ClaimJournal> GetXpk1ClaimJournalAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureXpk1ClaimJournalOpenAsync(cancellationToken).ConfigureAwait(false);
        return xpk1ClaimJournal
            ?? throw new ObjectDisposedException(nameof(DeepAccountRuntimeOwner));
    }

    internal async ValueTask<ProtectedCurrentDmd1CommitResult> CommitCurrentDmd1Async(
        DeviceOperationId32 operationId,
        Dmd1LineageState verifiedCurrent,
        CancellationToken cancellationToken = default)
    {
        var deviceStore = await GetDeviceStateStoreAsync(cancellationToken)
            .ConfigureAwait(false);
        return await deviceStore.CommitCurrentDmd1Async(
                operationId,
                verifiedCurrent,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async ValueTask<ProtectedDeviceAgreementLeaseResult>
        AuthorizeAndRedeemDeviceAgreementAsync(
            DeviceOperationId32 operationId,
            Dmd1LineageState verifiedCurrent,
            LocalDeviceX25519AgreementAuthority authority,
            LocalDeviceX25519AgreementPurpose purpose,
            ReadOnlyMemory<byte> operationBinding,
            ReadOnlyMemory<byte> peerPublicKey,
            CancellationToken cancellationToken = default)
    {
        var deviceStore = await GetDeviceStateStoreAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ProtectedDeviceAgreementLeaseIssuer.AuthorizeAndRedeemAsync(
                deviceStore,
                operationId,
                verifiedCurrent,
                authority,
                purpose,
                operationBinding,
                peerPublicKey,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<DeepContactResolvePersistenceBinding>
        GetContactResolvePersistenceBindingAsync(
            CancellationToken cancellationToken = default)
    {
        await EnsureContactStoreOpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureAccountDirectoryStoreOpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureXPointStoresOpenAsync(cancellationToken).ConfigureAwait(false);
        return new DeepContactResolvePersistenceBinding(
            contactStore ?? throw new ObjectDisposedException(nameof(DeepAccountRuntimeOwner)),
            accountDirectoryStore
                ?? throw new ObjectDisposedException(nameof(DeepAccountRuntimeOwner)),
            xPointNetworkStore
                ?? throw new ObjectDisposedException(nameof(DeepAccountRuntimeOwner)));
    }

    internal async Task<SqliteContactAddressPublicationStore>
        GetContactAddressPublicationStoreAsync(
            CancellationToken cancellationToken = default)
    {
        await EnsureContactAddressPublicationStoreOpenAsync(cancellationToken)
            .ConfigureAwait(false);
        return contactAddressPublicationStore ??
            throw new ObjectDisposedException(nameof(DeepAccountRuntimeOwner));
    }

    internal async Task<DeepContactResolvePrivacyHostBinding>
        GetContactResolvePrivacyHostBindingAsync(
            CancellationToken cancellationToken = default)
    {
        await EnsureXPointStoresOpenAsync(cancellationToken).ConfigureAwait(false);
        await contactResolvePrivacyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(store is null, this);
            if (contactResolvePrivacyBinding is not null)
            {
                return contactResolvePrivacyBinding;
            }

            var identity = await Accounts.GetLocalIdentityAsync(cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("No local Deep account exists.");
            var instanceId = await ReadMessageStoreInstanceIdAsync(
                    identity.SecureSlots.MessageStoreInstanceId,
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                var scope = new ContactResolvePrivacyHostScope(identity, instanceId);
                var anchorSlot0 = ScopedKeySlot(
                    identity.SecureSlots.MessageStoreInstanceId,
                    ContactResolveEntropyAnchor0Suffix,
                    "ContactResolve entropy anchor");
                var anchorSlot1 = ScopedKeySlot(
                    identity.SecureSlots.MessageStoreInstanceId,
                    ContactResolveEntropyAnchor1Suffix,
                    "ContactResolve entropy anchor");
                ProtectedContactResolveEntropyLedger? ledger =
                    await ProtectedContactResolveEntropyLedger.OpenAsync(
                        Path.Combine(
                            appDataDirectory,
                            RelativeContactResolveEntropyStatePath),
                        anchorSlot0,
                        anchorSlot1,
                        privacyStateProtector,
                        secureStorage,
                        scope,
                        cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    var binding = new DeepContactResolvePrivacyHostBinding(
                        identity.NetworkId,
                        identity.Account.AccountIdentity.AccountId.Bytes,
                        identity.Account.AccountIdentity.AccountGeneration,
                        identity.Device.DeviceId.Bytes,
                        identity.Device.DeviceGeneration,
                        entryGuardStore
                            ?? throw new ObjectDisposedException(nameof(DeepAccountRuntimeOwner)),
                        ledger,
                        new AccountBoundOnionKeyAgreementVault(secureStorage, scope));
                    contactResolveEntropyLedger = ledger;
                    contactResolvePrivacyBinding = binding;
                    ledger = null;
                    return binding;
                }
                finally
                {
                    ledger?.Dispose();
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(instanceId);
            }
        }
        finally
        {
            contactResolvePrivacyGate.Release();
        }
    }

    internal static async Task<DeepAccountRuntimeOwner> OpenAsync(
        string appDataDirectory,
        IClock clock,
        ReadOnlyMemory<byte> networkId,
        Func<string, JournaledDeepSecureStorage> secureStorageFactory,
        Func<IDeepSecretProtector> privacyStateProtectorFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(secureStorageFactory);
        ArgumentNullException.ThrowIfNull(privacyStateProtectorFactory);
        var root = Path.GetFullPath(appDataDirectory);
        var secureStorage = secureStorageFactory(root);
        try
        {
            var stateDirectory = Path.Combine(root, "deep-store-v1");
            Directory.CreateDirectory(stateDirectory);
            var store = await SqliteDeepAccountStoreBootstrap.OpenAsync(
                    Path.Combine(root, RelativeStatePath),
                    secureStorage,
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                var service = new DeepAccountService(
                    store,
                    secureStorage,
                    clock,
                    networkId.Span);
                var localIdentity = await service.GetLocalIdentityAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (localIdentity is null)
                {
                    DeleteSqliteFamily(Path.Combine(root, RelativeContactStatePath));
                    DeleteSqliteFamily(Path.Combine(
                        root,
                        RelativeContactAddressPublicationPath));
                    DeleteSqliteFamily(Path.Combine(root, RelativeDeviceStatePath));
                    DeleteSqliteFamily(Path.Combine(root, RelativeXpk1ClaimJournalPath));
                    DeleteSqliteFamily(Path.Combine(root, RelativeAccountDirectoryStatePath));
                    DeleteSqliteFamily(Path.Combine(root, RelativeXPointNetworkStatePath));
                    DeleteSqliteFamily(Path.Combine(root, RelativeEntryGuardStatePath));
                    DeleteSqliteFamily(Path.Combine(root, RelativeGroupStatePath));
                    DeleteSqliteFamily(Path.Combine(root, RelativeMailboxStatePath));
                    DeleteSqliteFamily(Path.Combine(
                        root,
                        RelativeGroupInvitationActivationPath));
                    DeepDirectMessagingStorageFacade.DeleteAccountState(root);
                }
                var owner = new DeepAccountRuntimeOwner(
                    root,
                    secureStorage,
                    privacyStateProtectorFactory(),
                    store,
                    service);
                if (localIdentity?.Account.ActivationState == DeepAccountActivationState.ActiveLocal)
                    _ = await owner.EnsureGenesisDeviceActivatedAsync(cancellationToken)
                        .ConfigureAwait(false);
                return owner;
            }
            catch
            {
                await store.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            secureStorage.Dispose();
            throw;
        }
    }

    internal static async Task DestroyAsync(
        string appDataDirectory,
        Func<string, JournaledDeepSecureStorage> secureStorageFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        ArgumentNullException.ThrowIfNull(secureStorageFactory);
        var root = Path.GetFullPath(appDataDirectory);
        using var secureStorage = secureStorageFactory(root);
        await SqliteDeepAccountStoreBootstrap.DestroyGenerationAsync(
                Path.Combine(root, RelativeStatePath),
                secureStorage,
                cancellationToken)
            .ConfigureAwait(false);
        DeleteSqliteFamily(Path.Combine(root, RelativeContactStatePath));
        DeleteSqliteFamily(Path.Combine(root, RelativeContactAddressPublicationPath));
        DeleteSqliteFamily(Path.Combine(root, RelativeDeviceStatePath));
        DeleteSqliteFamily(Path.Combine(root, RelativeXpk1ClaimJournalPath));
        DeleteSqliteFamily(Path.Combine(root, RelativeAccountDirectoryStatePath));
        DeleteSqliteFamily(Path.Combine(root, RelativeXPointNetworkStatePath));
        DeleteSqliteFamily(Path.Combine(root, RelativeEntryGuardStatePath));
        DeleteSqliteFamily(Path.Combine(root, RelativeGroupStatePath));
        DeleteSqliteFamily(Path.Combine(root, RelativeMailboxStatePath));
        DeleteSqliteFamily(Path.Combine(root, RelativeGroupInvitationActivationPath));
        DeepDirectMessagingStorageFacade.DeleteAccountState(root);
        DeleteProtectedStateFamily(
            Path.Combine(root, RelativeContactResolveEntropyStatePath));
    }

    public async ValueTask DisposeAsync()
    {
        var ownedStore = Interlocked.Exchange(ref store, null);
        if (ownedStore is null)
        {
            return;
        }

        await Task.WhenAll(
                contactGate.WaitAsync(),
                contactAddressPublicationGate.WaitAsync(),
                deviceGate.WaitAsync(),
                xpk1ClaimJournalGate.WaitAsync(),
                accountDirectoryGate.WaitAsync(),
                xPointGate.WaitAsync(),
                groupGate.WaitAsync(),
                directMessagingGate.WaitAsync(),
                mailboxGate.WaitAsync(),
                messagingTransportGate.WaitAsync(),
                contactUpdateRendezvousGate.WaitAsync(),
                contactResolvePrivacyGate.WaitAsync(),
                activationGate.WaitAsync())
            .ConfigureAwait(false);
        try
        {
            contactImporter = null;
            contactStore?.Dispose();
            contactStore = null;
            contactAddressPublicationStore?.Dispose();
            contactAddressPublicationStore = null;
            deviceStateStore?.Dispose();
            deviceStateStore = null;
            xpk1ClaimJournal?.Dispose();
            xpk1ClaimJournal = null;
            accountDirectoryStore?.Dispose();
            accountDirectoryStore = null;
            xPointNetworkStore?.Dispose();
            xPointNetworkStore = null;
            entryGuardStore?.Dispose();
            entryGuardStore = null;
            groupRuntimeBinding = null;
            groupInvitationActivationStore?.Dispose();
            groupInvitationActivationStore = null;
            groupStateStore?.Dispose();
            groupStateStore = null;
            mailboxStore?.Dispose();
            mailboxStore = null;
            if (directMessagingStorage is not null)
            {
                await directMessagingStorage.DisposeAsync().ConfigureAwait(false);
                directMessagingStorage = null;
            }
#if DEEP_TEST_INTERNALS
            if (directMessagingStorageForTests is not null)
            {
                await directMessagingStorageForTests.DisposeAsync().ConfigureAwait(false);
                directMessagingStorageForTests = null;
            }
#endif
            contactResolvePrivacyBinding = null;
            contactResolveEntropyLedger?.Dispose();
            contactResolveEntropyLedger = null;
            genesisActivation = null;
            localAgreementAuthority?.Dispose();
            localAgreementAuthority = null;
            await ownedStore.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            xPointGate.Release();
            accountDirectoryGate.Release();
            contactGate.Release();
            contactAddressPublicationGate.Release();
            deviceGate.Release();
            xpk1ClaimJournalGate.Release();
            groupGate.Release();
            directMessagingGate.Release();
            mailboxGate.Release();
            messagingTransportGate.Release();
            contactUpdateRendezvousGate.Release();
            contactResolvePrivacyGate.Release();
            activationGate.Release();
            secureStorage.Dispose();
        }
    }

    private async Task EnsureContactStoreOpenAsync(CancellationToken cancellationToken)
    {
        await contactGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(store is null, this);
            if (contactStore is not null && contactImporter is not null)
            {
                return;
            }

            var identity = await Accounts.GetLocalIdentityAsync(cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("No local Deep account exists.");
            var keySlot = ContactKeySlot(identity.SecureSlots.MessageStoreInstanceId);
            var key = await ReadOrCreateContactKeyAsync(keySlot, cancellationToken)
                .ConfigureAwait(false);
            SqliteContactStateStore? openedStore = null;
            try
            {
                using var options = new SqliteContactStateStoreOptions(
                    Path.Combine(appDataDirectory, RelativeContactStatePath),
                    key,
                    ContactStoreScope.ForCurrentAccount(
                        identity.Account.AccountIdentity.AccountId));
                openedStore = new SqliteContactStateStore(options);
                contactImporter = new ContactAddressImportService(
                    openedStore,
                    identity.NetworkId);
                contactStore = openedStore;
                openedStore = null;
            }
            finally
            {
                openedStore?.Dispose();
                CryptographicOperations.ZeroMemory(key);
            }
        }
        finally
        {
            contactGate.Release();
        }
    }

    private async Task EnsureContactAddressPublicationStoreOpenAsync(
        CancellationToken cancellationToken)
    {
        await contactAddressPublicationGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(store is null, this);
            if (contactAddressPublicationStore is not null) return;

            var identity = await Accounts.GetLocalIdentityAsync(cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("No local Deep account exists.");
            var keySlot = ScopedKeySlot(
                identity.SecureSlots.MessageStoreInstanceId,
                ContactAddressPublicationKeySuffix,
                "ContactV1 publication journal");
            var contactKeySlot = ContactKeySlot(
                identity.SecureSlots.MessageStoreInstanceId);
            var key = await ReadScopedKeyAsync(
                    keySlot,
                    "ContactV1 publication journal",
                    cancellationToken)
                .ConfigureAwait(false);
            var contactKey = await ReadScopedKeyAsync(
                    contactKeySlot,
                    "ContactV1",
                    cancellationToken)
                .ConfigureAwait(false);
            var statePath = Path.Combine(
                appDataDirectory,
                RelativeContactAddressPublicationPath);
            var databaseExisted = SqliteFamilyExists(statePath);
            if (key is null && databaseExisted)
                throw InvalidScopedKey(
                    "The ContactV1 publication journal exists without its protected SQLCipher key.");
            var createdKey = key is null;
            key ??= CreateNonzeroKey();
            SqliteContactAddressPublicationStore? opened = null;
            try
            {
                if (contactKey is not null &&
                    CryptographicOperations.FixedTimeEquals(key, contactKey))
                {
                    throw InvalidScopedKey(
                        "The ContactV1 state and publication journal SQLCipher keys are not distinct.");
                }
                if (createdKey)
                {
                    await secureStorage.WriteBatchAsync(
                            [new DeepSecureStorageWrite(keySlot, key)],
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                using var options = new SqliteContactAddressPublicationStoreOptions(
                    statePath,
                    key,
                    ContactStoreScope.ForCurrentAccount(
                        identity.Account.AccountIdentity.AccountId));
                opened = new SqliteContactAddressPublicationStore(options);
                contactAddressPublicationStore = opened;
                opened = null;
            }
            catch
            {
                opened?.Dispose();
                if (createdKey && !databaseExisted)
                {
                    await secureStorage.DeleteBatchAsync(
                            [keySlot],
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    DeleteSqliteFamily(statePath);
                }
                throw;
            }
            finally
            {
                if (contactKey is not null)
                    CryptographicOperations.ZeroMemory(contactKey);
                CryptographicOperations.ZeroMemory(key);
            }
        }
        finally
        {
            contactAddressPublicationGate.Release();
        }
    }

    private async Task EnsureXpk1ClaimJournalOpenAsync(CancellationToken cancellationToken)
    {
        await xpk1ClaimJournalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(store is null, this);
            if (xpk1ClaimJournal is not null) return;

            var identity = await Accounts.GetLocalIdentityAsync(cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("No local Deep account exists.");
            var keySlot = ScopedKeySlot(
                identity.SecureSlots.MessageStoreInstanceId,
                Xpk1ClaimJournalKeySuffix,
                "XPK1 claim journal");
            var contactKeySlot = ContactKeySlot(identity.SecureSlots.MessageStoreInstanceId);
            var key = await ReadScopedKeyAsync(
                    keySlot,
                    "XPK1 claim journal",
                    cancellationToken)
                .ConfigureAwait(false);
            var contactKey = await ReadScopedKeyAsync(
                    contactKeySlot,
                    "ContactV1",
                    cancellationToken)
                .ConfigureAwait(false);
            var statePath = Path.Combine(appDataDirectory, RelativeXpk1ClaimJournalPath);
            var databaseExisted = SqliteFamilyExists(statePath);
            if (key is null && databaseExisted)
                throw InvalidScopedKey(
                    "The XPK1 claim journal exists without its protected SQLCipher key.");
            var createdKey = key is null;
            key ??= CreateNonzeroKey();
            SqliteXpk1ClaimJournal? opened = null;
            try
            {
                if (contactKey is not null &&
                    CryptographicOperations.FixedTimeEquals(key, contactKey))
                    throw InvalidScopedKey(
                        "The ContactV1 and XPK1 claim journal SQLCipher keys are not distinct.");
                if (createdKey)
                {
                    await secureStorage.WriteBatchAsync(
                            [new DeepSecureStorageWrite(keySlot, key)],
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                using var options = new SqliteXpk1ClaimJournalOptions(
                    statePath,
                    key,
                    ContactStoreScope.ForCurrentAccount(
                        identity.Account.AccountIdentity.AccountId));
                opened = new SqliteXpk1ClaimJournal(options);
                xpk1ClaimJournal = opened;
                opened = null;
            }
            catch
            {
                opened?.Dispose();
                if (createdKey && !databaseExisted)
                {
                    await secureStorage.DeleteBatchAsync([keySlot], CancellationToken.None)
                        .ConfigureAwait(false);
                    DeleteSqliteFamily(statePath);
                }
                throw;
            }
            finally
            {
                if (contactKey is not null) CryptographicOperations.ZeroMemory(contactKey);
                CryptographicOperations.ZeroMemory(key);
            }
        }
        finally
        {
            xpk1ClaimJournalGate.Release();
        }
    }

    internal async Task<SqliteDeviceStateStore> GetDeviceStateStoreAsync(
        CancellationToken cancellationToken)
    {
        await deviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(store is null, this);
            if (deviceStateStore is not null) return deviceStateStore;

            var identity = await Accounts.GetLocalIdentityAsync(cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("No local Deep account exists.");
            var instanceId = await ReadMessageStoreInstanceIdAsync(
                    identity.SecureSlots.MessageStoreInstanceId,
                    cancellationToken)
                .ConfigureAwait(false);
            var keySlot = ScopedKeySlot(
                identity.SecureSlots.MessageStoreInstanceId,
                DeviceStateKeySuffix,
                "DeviceV1");
            var key = await ReadScopedKeyAsync(keySlot, "DeviceV1", cancellationToken)
                .ConfigureAwait(false);
            var statePath = Path.Combine(appDataDirectory, RelativeDeviceStatePath);
            var databaseExisted = SqliteFamilyExists(statePath);
            if (key is null && databaseExisted)
                throw InvalidScopedKey(
                    "The DeviceV1 database exists without its protected SQLCipher key.");
            var createdKey = key is null;
            key ??= CreateNonzeroKey();
            SqliteDeviceStateStore? opened = null;
            byte[]? deviceStoreId = null;
            try
            {
                if (createdKey)
                {
                    await secureStorage.WriteBatchAsync(
                            [new DeepSecureStorageWrite(keySlot, key)],
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                deviceStoreId = DeriveDeviceStoreId(instanceId);
                opened = new SqliteDeviceStateStore(
                    new SqliteDeviceStateStoreOptions(
                        statePath,
                        key,
                        DeviceAccountId32.FromBytes(
                            identity.Account.AccountIdentity.AccountId.Bytes.Span),
                        identity.Account.AccountIdentity.AccountGeneration,
                        databaseGeneration: 1,
                        storeInstanceId: DeviceOperationId32.FromBytes(deviceStoreId)));
                deviceStateStore = opened;
                opened = null;
                return deviceStateStore;
            }
            catch
            {
                opened?.Dispose();
                if (createdKey && !databaseExisted)
                {
                    await secureStorage.DeleteBatchAsync([keySlot], CancellationToken.None)
                        .ConfigureAwait(false);
                    DeleteSqliteFamily(statePath);
                }
                throw;
            }
            finally
            {
                if (deviceStoreId is not null) CryptographicOperations.ZeroMemory(deviceStoreId);
                CryptographicOperations.ZeroMemory(instanceId);
                CryptographicOperations.ZeroMemory(key);
            }
        }
        finally
        {
            deviceGate.Release();
        }
    }

    private async Task EnsureAccountDirectoryStoreOpenAsync(
        CancellationToken cancellationToken)
    {
        await accountDirectoryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(store is null, this);
            if (accountDirectoryStore is not null)
            {
                return;
            }

            var identity = await Accounts.GetLocalIdentityAsync(cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("No local Deep account exists.");
            var instanceId = await ReadMessageStoreInstanceIdAsync(
                    identity.SecureSlots.MessageStoreInstanceId,
                    cancellationToken)
                .ConfigureAwait(false);
            var keySlot = ScopedKeySlot(
                identity.SecureSlots.MessageStoreInstanceId,
                AccountDirectoryKeySuffix,
                "account-directory");
            var key = await ReadScopedKeyAsync(
                    keySlot,
                    "account-directory",
                    cancellationToken)
                .ConfigureAwait(false);
            var statePath = Path.Combine(appDataDirectory, RelativeAccountDirectoryStatePath);
            var databaseExisted = SqliteFamilyExists(statePath);
            var createdKey = key is null;
            key ??= CreateNonzeroKey();
            SqliteAccountDirectoryStateStore? openedStore = null;
            byte[]? directoryStoreId = null;
            try
            {
                if (createdKey)
                {
                    await secureStorage.WriteBatchAsync(
                            [new DeepSecureStorageWrite(keySlot, key)],
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                directoryStoreId = DeriveAccountDirectoryStoreId(instanceId);
                var options = new SqliteAccountDirectoryStateStoreOptions(
                    statePath,
                    key,
                    identity.NetworkId.Span,
                    identity.Account.AccountIdentity.AccountId.Bytes.Span,
                    identity.Account.AccountIdentity.AccountGeneration,
                    databaseGeneration: 1,
                    storeInstanceId: AccountDirectoryStoreId32.FromBytes(directoryStoreId));
                openedStore = new SqliteAccountDirectoryStateStore(options);
                accountDirectoryStore = openedStore;
                openedStore = null;
            }
            catch
            {
                openedStore?.Dispose();
                if (createdKey && !databaseExisted)
                {
                    await secureStorage.DeleteBatchAsync([keySlot], CancellationToken.None)
                        .ConfigureAwait(false);
                    DeleteSqliteFamily(statePath);
                }
                throw;
            }
            finally
            {
                if (directoryStoreId is not null)
                {
                    CryptographicOperations.ZeroMemory(directoryStoreId);
                }
                CryptographicOperations.ZeroMemory(instanceId);
                CryptographicOperations.ZeroMemory(key);
            }
        }
        finally
        {
            accountDirectoryGate.Release();
        }
    }

    private async Task EnsureXPointStoresOpenAsync(CancellationToken cancellationToken)
    {
        await xPointGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(store is null, this);
            if (xPointNetworkStore is not null && entryGuardStore is not null)
            {
                return;
            }

            var identity = await Accounts.GetLocalIdentityAsync(cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("No local Deep account exists.");
            var instanceId = await ReadMessageStoreInstanceIdAsync(
                    identity.SecureSlots.MessageStoreInstanceId,
                    cancellationToken)
                .ConfigureAwait(false);
            var networkKeySlot = ScopedKeySlot(
                identity.SecureSlots.MessageStoreInstanceId,
                XPointNetworkKeySuffix,
                "XPoint network");
            var guardKeySlot = ScopedKeySlot(
                identity.SecureSlots.MessageStoreInstanceId,
                EntryGuardKeySuffix,
                "entry-guard");
            var keys = await ReadOrCreateXPointKeysAsync(
                    networkKeySlot,
                    guardKeySlot,
                    cancellationToken)
                .ConfigureAwait(false);
            SqliteXPointNetworkStateStore? openedNetworkStore = null;
            SqliteProtectedEntryGuardStore? openedGuardStore = null;
            try
            {
                openedNetworkStore = new SqliteXPointNetworkStateStore(
                    new SqliteXPointNetworkStateStoreOptions(
                        Path.Combine(appDataDirectory, RelativeXPointNetworkStatePath),
                        keys.NetworkKey,
                        identity.NetworkId.Span,
                        identity.Account.AccountIdentity.AccountId.Bytes.Span,
                        identity.Account.AccountIdentity.AccountGeneration,
                        checked((ulong)DeepAccountStoreContract.CurrentStoreGeneration),
                        instanceId));
                openedGuardStore = new SqliteProtectedEntryGuardStore(
                    new SqliteProtectedEntryGuardStoreOptions(
                        Path.Combine(appDataDirectory, RelativeEntryGuardStatePath),
                        keys.GuardKey,
                        identity.NetworkId.Span,
                        identity.Account.AccountIdentity.AccountId.Bytes.Span,
                        identity.Account.AccountIdentity.AccountGeneration,
                        checked((ulong)DeepAccountStoreContract.CurrentStoreGeneration),
                        instanceId));
                xPointNetworkStore = openedNetworkStore;
                entryGuardStore = openedGuardStore;
                openedNetworkStore = null;
                openedGuardStore = null;
            }
            finally
            {
                openedGuardStore?.Dispose();
                openedNetworkStore?.Dispose();
                CryptographicOperations.ZeroMemory(keys.NetworkKey);
                CryptographicOperations.ZeroMemory(keys.GuardKey);
                CryptographicOperations.ZeroMemory(instanceId);
            }
        }
        finally
        {
            xPointGate.Release();
        }
    }

    private async Task<byte[]> ReadOrCreateContactKeyAsync(
        string slot,
        CancellationToken cancellationToken)
    {
        using (var existing = await secureStorage.ReadOwnedAsync(slot, cancellationToken)
                   .ConfigureAwait(false))
        {
            if (existing is not null)
            {
                if (existing.Length != 32)
                {
                    throw InvalidContactKey();
                }
                var restored = new byte[32];
                existing.CopyTo(restored);
                if (restored.AsSpan().IndexOfAnyExcept((byte)0) < 0)
                {
                    CryptographicOperations.ZeroMemory(restored);
                    throw InvalidContactKey();
                }
                return restored;
            }
        }

        byte[] created;
        do
        {
            created = RandomNumberGenerator.GetBytes(32);
        }
        while (created.AsSpan().IndexOfAnyExcept((byte)0) < 0);
        try
        {
            await secureStorage.WriteBatchAsync(
                    [new DeepSecureStorageWrite(slot, created)],
                    cancellationToken)
                .ConfigureAwait(false);
            return created.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(created);
        }
    }

    private async Task<(byte[] NetworkKey, byte[] GuardKey)> ReadOrCreateXPointKeysAsync(
        string networkKeySlot,
        string guardKeySlot,
        CancellationToken cancellationToken)
    {
        var networkKey = await ReadScopedKeyAsync(
                networkKeySlot,
                "XPoint network",
                cancellationToken)
            .ConfigureAwait(false);
        var guardKey = await ReadScopedKeyAsync(
                guardKeySlot,
                "entry-guard",
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var writes = new List<DeepSecureStorageWrite>(2);
            if (networkKey is null)
            {
                networkKey = CreateNonzeroKey();
                writes.Add(new DeepSecureStorageWrite(networkKeySlot, networkKey));
            }
            if (guardKey is null)
            {
                do
                {
                    if (guardKey is not null)
                    {
                        CryptographicOperations.ZeroMemory(guardKey);
                    }
                    guardKey = CreateNonzeroKey();
                }
                while (CryptographicOperations.FixedTimeEquals(networkKey, guardKey));
                writes.Add(new DeepSecureStorageWrite(guardKeySlot, guardKey));
            }
            if (CryptographicOperations.FixedTimeEquals(networkKey, guardKey))
            {
                throw InvalidScopedKey("XPoint network and entry-guard keys are not distinct.");
            }
            if (writes.Count != 0)
            {
                await secureStorage.WriteBatchAsync(writes, cancellationToken)
                    .ConfigureAwait(false);
            }
            return (networkKey, guardKey);
        }
        catch
        {
            if (networkKey is not null)
            {
                CryptographicOperations.ZeroMemory(networkKey);
            }
            if (guardKey is not null)
            {
                CryptographicOperations.ZeroMemory(guardKey);
            }
            throw;
        }
    }

    private async Task<byte[]> ReadMessageStoreInstanceIdAsync(
        string slot,
        CancellationToken cancellationToken)
    {
        using var stored = await secureStorage.ReadOwnedAsync(slot, cancellationToken)
            .ConfigureAwait(false)
            ?? throw InvalidScopedKey("The local store-instance ID is missing.");
        if (stored.Length != 32)
        {
            throw InvalidScopedKey("The local store-instance ID is invalid.");
        }
        var instanceId = new byte[32];
        stored.CopyTo(instanceId);
        if (instanceId.AsSpan().IndexOfAnyExcept((byte)0) < 0)
        {
            CryptographicOperations.ZeroMemory(instanceId);
            throw InvalidScopedKey("The local store-instance ID is invalid.");
        }
        return instanceId;
    }

    private async Task<byte[]?> ReadScopedKeyAsync(
        string slot,
        string purpose,
        CancellationToken cancellationToken)
    {
        using var stored = await secureStorage.ReadOwnedAsync(slot, cancellationToken)
            .ConfigureAwait(false);
        if (stored is null)
        {
            return null;
        }
        if (stored.Length != 32)
        {
            throw InvalidScopedKey($"The {purpose} SQLCipher key is invalid.");
        }
        var key = new byte[32];
        stored.CopyTo(key);
        if (key.AsSpan().IndexOfAnyExcept((byte)0) < 0)
        {
            CryptographicOperations.ZeroMemory(key);
            throw InvalidScopedKey($"The {purpose} SQLCipher key is invalid.");
        }
        return key;
    }

    private static byte[] CreateNonzeroKey()
    {
        while (true)
        {
            var key = RandomNumberGenerator.GetBytes(32);
            if (key.AsSpan().IndexOfAnyExcept((byte)0) >= 0)
            {
                return key;
            }
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] EncodeContactUpdateRendezvous(
        ReadOnlySpan<byte> keyId,
        ReadOnlySpan<byte> publicKey,
        ReadOnlySpan<byte> exactXur1)
    {
        if (keyId.Length != 32 || publicKey.Length != 32 || exactXur1.IsEmpty ||
            exactXur1.Length > 65_535)
            throw new CryptographicException(
                "The protected ContactV1 update rendezvous is malformed.");
        var encoded = new byte[checked(4 + 2 + 32 + 32 + 4 + exactXur1.Length)];
        "DXU1"u8.CopyTo(encoded);
        BinaryPrimitives.WriteUInt16BigEndian(encoded.AsSpan(4), 1);
        keyId.CopyTo(encoded.AsSpan(6, 32));
        publicKey.CopyTo(encoded.AsSpan(38, 32));
        BinaryPrimitives.WriteUInt32BigEndian(
            encoded.AsSpan(70), checked((uint)exactXur1.Length));
        exactXur1.CopyTo(encoded.AsSpan(74));
        return encoded;
    }

    private static ProtectedContactUpdateRendezvous DecodeContactUpdateRendezvous(
        ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length < 75 || !encoded[..4].SequenceEqual("DXU1"u8) ||
            BinaryPrimitives.ReadUInt16BigEndian(encoded.Slice(4, 2)) != 1)
            throw InvalidScopedKey(
                "The protected ContactV1 update rendezvous is invalid.");
        var length = BinaryPrimitives.ReadUInt32BigEndian(encoded.Slice(70, 4));
        if (length == 0 || length > 65_535 ||
            encoded.Length != checked(74 + (int)length))
            throw InvalidScopedKey(
                "The protected ContactV1 update rendezvous is invalid.");
        var keyId = encoded.Slice(6, 32).ToArray();
        var publicKey = encoded.Slice(38, 32).ToArray();
        var exact = encoded[74..].ToArray();
        if (keyId.AsSpan().IndexOfAnyExcept((byte)0) < 0 ||
            publicKey.AsSpan().IndexOfAnyExcept((byte)0) < 0)
        {
            CryptographicOperations.ZeroMemory(keyId);
            CryptographicOperations.ZeroMemory(publicKey);
            CryptographicOperations.ZeroMemory(exact);
            throw InvalidScopedKey(
                "The protected ContactV1 update rendezvous is invalid.");
        }
        return new ProtectedContactUpdateRendezvous(keyId, publicKey, exact);
    }

    private sealed record ProtectedContactUpdateRendezvous(
        byte[] KeyId,
        byte[] PublicKey,
        byte[] ExactXur1);

    private static void ZeroContactUpdateRendezvous(
        ProtectedContactUpdateRendezvous value)
    {
        CryptographicOperations.ZeroMemory(value.KeyId);
        CryptographicOperations.ZeroMemory(value.PublicKey);
        CryptographicOperations.ZeroMemory(value.ExactXur1);
    }

    private static string ContactKeySlot(string messageStoreInstanceSlot)
        => ScopedKeySlot(messageStoreInstanceSlot, ContactKeySuffix, "ContactV1");

    private static byte[] DeriveAccountDirectoryStoreId(ReadOnlySpan<byte> instanceId)
    {
        var domain = "Deep/Client/AccountDirectory/store-id/v1"u8;
        var input = new byte[domain.Length + instanceId.Length];
        domain.CopyTo(input);
        instanceId.CopyTo(input.AsSpan(domain.Length));
        try
        {
            return SHA256.HashData(input);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    private static byte[] DeriveDeviceStoreId(ReadOnlySpan<byte> instanceId)
    {
        var domain = "Deep/Client/DeviceV1/store-id/v1"u8;
        var input = new byte[domain.Length + instanceId.Length];
        domain.CopyTo(input);
        instanceId.CopyTo(input.AsSpan(domain.Length));
        try
        {
            return SHA256.HashData(input);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    private static string ScopedKeySlot(
        string messageStoreInstanceSlot,
        string targetSuffix,
        string purpose)
    {
        const string suffix = ".message-store-instance";
        if (!messageStoreInstanceSlot.StartsWith("deep.store.v1.", StringComparison.Ordinal)
            || !messageStoreInstanceSlot.EndsWith(suffix, StringComparison.Ordinal))
        {
            throw new LocalStateResetRequiredException(
                LocalStateResetRequiredReason.InvalidCurrentSchema,
                $"The {purpose} secure-storage scope is invalid.");
        }
        return messageStoreInstanceSlot[..^suffix.Length] + targetSuffix;
    }

    private static LocalStateResetRequiredException InvalidContactKey() =>
        new(
            LocalStateResetRequiredReason.InvalidCurrentSchema,
            "The ContactV1 SQLCipher key is invalid.");

    private static LocalStateResetRequiredException InvalidScopedKey(string message) =>
        new(LocalStateResetRequiredReason.InvalidCurrentSchema, message);

    private static void DeleteSqliteFamily(string statePath)
    {
        foreach (var path in new[]
                 {
                     statePath,
                     statePath + "-wal",
                     statePath + "-shm",
                     statePath + "-journal"
                 })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static bool SqliteFamilyExists(string statePath) =>
        File.Exists(statePath)
        || File.Exists(statePath + "-wal")
        || File.Exists(statePath + "-shm")
        || File.Exists(statePath + "-journal");

    private static void DeleteProtectedStateFamily(string statePath)
    {
        var directory = Path.GetDirectoryName(statePath);
        var name = Path.GetFileName(statePath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Protected state path is invalid.");
        }
        foreach (var path in Directory.Exists(directory)
                     ? Directory.GetFiles(directory, name + "*")
                     : [])
        {
            File.Delete(path);
        }
    }
}

internal sealed record DeepContactResolvePersistenceBinding(
    IContactStateStore ContactStore,
    IAccountDirectoryStateStore AccountDirectoryStore,
    IXPointNetworkStateStore XPointNetworkStore);

internal sealed class DeepContactResolvePrivacyHostBinding
{
    private readonly byte[] networkId;
    private readonly byte[] accountId;
    private readonly byte[] deviceId;

    internal DeepContactResolvePrivacyHostBinding(
        ReadOnlyMemory<byte> networkId,
        ReadOnlyMemory<byte> accountId,
        ulong accountGeneration,
        ReadOnlyMemory<byte> deviceId,
        ulong deviceGeneration,
        IProtectedEntryGuardStore entryGuardStore,
        IOnionEntropyUniquenessLedger entropyLedger,
        IOnionKeyAgreementVault keyAgreementVault)
    {
        this.networkId = networkId.ToArray();
        this.accountId = accountId.ToArray();
        this.deviceId = deviceId.ToArray();
        AccountGeneration = accountGeneration;
        DeviceGeneration = deviceGeneration;
        EntryGuardStore = entryGuardStore
            ?? throw new ArgumentNullException(nameof(entryGuardStore));
        EntropyLedger = entropyLedger
            ?? throw new ArgumentNullException(nameof(entropyLedger));
        KeyAgreementVault = keyAgreementVault
            ?? throw new ArgumentNullException(nameof(keyAgreementVault));
    }

    internal ReadOnlyMemory<byte> NetworkId => networkId.ToArray();
    internal ReadOnlyMemory<byte> AccountId => accountId.ToArray();
    internal ulong AccountGeneration { get; }
    internal ReadOnlyMemory<byte> DeviceId => deviceId.ToArray();
    internal ulong DeviceGeneration { get; }
    internal IProtectedEntryGuardStore EntryGuardStore { get; }
    internal IOnionEntropyUniquenessLedger EntropyLedger { get; }
    internal IOnionKeyAgreementVault KeyAgreementVault { get; }
}
