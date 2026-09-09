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
using Deep.Protocol.DeepExtension.PrivacyRouting;
using Deep.Protocol.ApplicationCore;
using Deep.Protocol.ContactV1;
using Deep.Protocol.DeepNative;
using Deep.Protocol.GroupV1;
using Deep.Protocol.Identity;
using Deep.Protocol.MessagingWire;
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
    private const string RelativeDeviceStatePath = "deep-store-v1/devices.dvs1";
    private const string RelativeXpk1ClaimJournalPath = "deep-store-v1/xpk1-claims.xcj1";
    private const string RelativeAccountDirectoryStatePath =
        "deep-store-v1/account-directory.ads1";
    private const string RelativeXPointNetworkStatePath = "deep-store-v1/xpoint-network.xlk1";
    private const string RelativeEntryGuardStatePath = "deep-store-v1/entry-guards.xgs1";
    private const string RelativeGroupStatePath = "deep-store-v1/groups.dgv1";
    private const string RelativeGroupInvitationActivationPath =
        "deep-store-v1/group-invitation-activation.gia1";
    private const string RelativeContactResolveEntropyStatePath =
        "deep-store-v1/contact-resolve-entropy.cre1";
    private const string ContactKeySuffix = ".contact-state-key";
    private const string DeviceStateKeySuffix = ".device-state-key";
    private const string Xpk1ClaimJournalKeySuffix = ".xpk1-claim-journal-key";
    private const string AccountDirectoryKeySuffix = ".account-directory-state-key";
    private const string XPointNetworkKeySuffix = ".xpoint-network-state-key";
    private const string EntryGuardKeySuffix = ".xpoint-entry-guard-key";
    private const string GroupStateKeySuffix = ".group-v1-state-key";
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
    private readonly SemaphoreSlim deviceGate = new(1, 1);
    private readonly SemaphoreSlim xpk1ClaimJournalGate = new(1, 1);
    private readonly SemaphoreSlim accountDirectoryGate = new(1, 1);
    private readonly SemaphoreSlim xPointGate = new(1, 1);
    private readonly SemaphoreSlim groupGate = new(1, 1);
    private readonly SemaphoreSlim directMessagingGate = new(1, 1);
    private readonly SemaphoreSlim contactResolvePrivacyGate = new(1, 1);
    private SqliteDeepAccountStore? store;
    private SqliteContactStateStore? contactStore;
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
#if DEEP_TEST_INTERNALS
    private DeepDirectMessagingStorageOwner? directMessagingStorageForTests;
#endif
    private ProtectedContactResolveEntropyLedger? contactResolveEntropyLedger;
    private DeepContactResolvePrivacyHostBinding? contactResolvePrivacyBinding;

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

    internal async Task<IGroupDeviceCustodySigner?> TryGetGroupDeviceCustodySignerAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(store is null, this);
        var identity = await Accounts.GetLocalIdentityAsync(cancellationToken)
            .ConfigureAwait(false);
        return identity is null
            ? null
            : new AccountOwnedGroupDeviceCustodySigner(Accounts, secureStorage, identity);
    }

    internal async Task<DeepDirectMessagingStorageFacade?>
        TryGetDirectMessagingStorageAsync(
            LocalDeviceX25519AgreementAuthority? localAgreementAuthority,
            VerifiedDeviceRelative? verifiedDevice,
            CancellationToken cancellationToken = default)
    {
        if (localAgreementAuthority is null || verifiedDevice is null)
        {
            return null;
        }

        await directMessagingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(store is null, this);
            if (directMessagingStorage is not null)
            {
                if (!directMessagingStorage.IsBoundTo(
                        localAgreementAuthority,
                        verifiedDevice))
                {
                    throw new CryptographicException(
                        "The direct-message owner is already bound to another verified local authority.");
                }
                return directMessagingStorage;
            }

            directMessagingStorage = await DeepDirectMessagingStorageFacade.OpenAsync(
                    appDataDirectory,
                    Accounts,
                    localAgreementAuthority,
                    verifiedDevice,
                    cancellationToken)
                .ConfigureAwait(false);
            return directMessagingStorage;
        }
        finally
        {
            directMessagingGate.Release();
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

    internal async ValueTask<DeepDirectMessagingInitiatorClaimPreparation?>
        TryPrepareDirectMessagingInitiatorClaimAsync(
            DeepDirectMessagingLocalAuthorityBinding? verifiedLocalAuthority,
            ContactResolverReverifiedPeerAuthority? verifiedPeer,
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
            return await messaging.TryPrepareInitiatorClaimAsync(
                    verifiedPeer,
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
        return messaging is null
            ? null
            : await messaging.TryCommitResponderSessionAsync(
                    verifiedSession,
                    relationship,
                    verifiedClaim,
                    verifiedInitiation,
                    maximumMessagesWithoutPqInjection,
                    cancellationToken)
                .ConfigureAwait(false);
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
                            new AccountOwnedGroupDeviceCustodySigner(
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
                if (await service.GetLocalIdentityAsync(cancellationToken).ConfigureAwait(false) is null)
                {
                    DeleteSqliteFamily(Path.Combine(root, RelativeContactStatePath));
                    DeleteSqliteFamily(Path.Combine(root, RelativeDeviceStatePath));
                    DeleteSqliteFamily(Path.Combine(root, RelativeXpk1ClaimJournalPath));
                    DeleteSqliteFamily(Path.Combine(root, RelativeAccountDirectoryStatePath));
                    DeleteSqliteFamily(Path.Combine(root, RelativeXPointNetworkStatePath));
                    DeleteSqliteFamily(Path.Combine(root, RelativeEntryGuardStatePath));
                    DeleteSqliteFamily(Path.Combine(root, RelativeGroupStatePath));
                    DeleteSqliteFamily(Path.Combine(
                        root,
                        RelativeGroupInvitationActivationPath));
                    DeepDirectMessagingStorageFacade.DeleteAccountState(root);
                }
                return new DeepAccountRuntimeOwner(
                    root,
                    secureStorage,
                    privacyStateProtectorFactory(),
                    store,
                    service);
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
        DeleteSqliteFamily(Path.Combine(root, RelativeDeviceStatePath));
        DeleteSqliteFamily(Path.Combine(root, RelativeXpk1ClaimJournalPath));
        DeleteSqliteFamily(Path.Combine(root, RelativeAccountDirectoryStatePath));
        DeleteSqliteFamily(Path.Combine(root, RelativeXPointNetworkStatePath));
        DeleteSqliteFamily(Path.Combine(root, RelativeEntryGuardStatePath));
        DeleteSqliteFamily(Path.Combine(root, RelativeGroupStatePath));
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
                deviceGate.WaitAsync(),
                xpk1ClaimJournalGate.WaitAsync(),
                accountDirectoryGate.WaitAsync(),
                xPointGate.WaitAsync(),
                groupGate.WaitAsync(),
                directMessagingGate.WaitAsync(),
                contactResolvePrivacyGate.WaitAsync())
            .ConfigureAwait(false);
        try
        {
            contactImporter = null;
            contactStore?.Dispose();
            contactStore = null;
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
            await ownedStore.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            xPointGate.Release();
            accountDirectoryGate.Release();
            contactGate.Release();
            deviceGate.Release();
            xpk1ClaimJournalGate.Release();
            groupGate.Release();
            directMessagingGate.Release();
            contactResolvePrivacyGate.Release();
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
