using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Domain.ContactV1;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Persistence.ContactV1;
using Deep.Client.Shared.Persistence.DeviceV1;
using Deep.Client.Shared.Persistence.PreKeyV1;
using Deep.Client.Shared.Persistence.XPointNetworkV1;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.Services.ContactV1;
using Deep.Client.Shared.Services.GroupV1;
using Deep.Client.Shared.Services.MessagingV1;
using Deep.Client.Shared.Services.XPointNetworkV1;
using Deep.Protocol.ApplicationCore;
using Deep.Protocol.AccountDirectoryV1;
using Deep.Protocol.ContactV1;
using Deep.Protocol.DeepNative;
using Deep.Protocol.DeepExtension.PrivacyRouting;
using Deep.Protocol.DeepExtension.MailboxCapabilities;
using Deep.Protocol.GroupV1;
using Deep.Protocol.Identity;
using Deep.Protocol.MessagingCrypto;
using Deep.Protocol.MessagingWire;
using Deep.Protocol.XPointNetworkV1;

namespace Deep.Client.Maui.Services;

internal sealed class DeepAccountRuntimeAccessor :
    IDeepAccountRuntimeAccessor
{
    private readonly string appDataDirectory;
    private readonly IClock clock;
    private readonly Func<ReadOnlyMemory<byte>> networkIdFactory;
    private readonly Func<string, JournaledDeepSecureStorage> secureStorageFactory;
    private readonly Func<IDeepSecretProtector> privacyStateProtectorFactory;
    private readonly SemaphoreSlim gate = new(1, 1);
    private DeepAccountRuntimeOwner? owner;
    private bool disposed;

    internal DeepAccountRuntimeAccessor(
        string appDataDirectory,
        IClock clock,
        Func<ReadOnlyMemory<byte>> networkIdFactory,
        Func<string, JournaledDeepSecureStorage> secureStorageFactory,
        Func<IDeepSecretProtector>? privacyStateProtectorFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        this.appDataDirectory = Path.GetFullPath(appDataDirectory);
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.networkIdFactory = networkIdFactory
            ?? throw new ArgumentNullException(nameof(networkIdFactory));
        this.secureStorageFactory = secureStorageFactory
            ?? throw new ArgumentNullException(nameof(secureStorageFactory));
        this.privacyStateProtectorFactory = privacyStateProtectorFactory
            ?? (static () => new PlatformDeepSecretProtector());
    }

    public async Task<DeepAccountService> GetAccountsAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (owner is null)
            {
                owner = await DeepAccountRuntimeOwner.OpenAsync(
                        appDataDirectory,
                        clock,
                        networkIdFactory(),
                        secureStorageFactory,
                        privacyStateProtectorFactory,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            return owner.Accounts;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task EnsureLocalIdentityActivatedAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            _ = await owner.EnsureGenesisDeviceActivatedAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<DeepGenesisDeviceActivation?>
        EnsureGenesisDeviceActivatedAsync(
            CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            return await owner.EnsureGenesisDeviceActivatedAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<string> GetPermanentDeepIdAsync(
        CancellationToken cancellationToken = default)
    {
        var accounts = await GetAccountsAsync(cancellationToken).ConfigureAwait(false);
        var identity = await accounts.GetLocalIdentityAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No local Deep account exists.");
        return identity.Account.PermanentId.CanonicalText;
    }

    internal async Task<DeepGroupV1RuntimeBinding?> TryGetGroupV1RuntimeBindingAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            return await owner.TryGetGroupV1RuntimeBindingAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<IDeepGroupV1Runtime?> TryGetGroupV1RuntimeAsync(
        CancellationToken cancellationToken = default)
    {
        var binding = await TryGetGroupV1RuntimeBindingAsync(cancellationToken)
            .ConfigureAwait(false);
        return binding?.Runtime;
    }

    internal async Task<SqliteXpk1ClaimJournal> GetXpk1ClaimJournalAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            return await owner.GetXpk1ClaimJournalAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<SqliteDeviceStateStore> GetDeviceStateStoreAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            return await owner.GetDeviceStateStoreAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<IGroupDeviceCustodySigner?>
        TryGetGroupDeviceCustodySignerAsync(
            CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            return await owner.TryGetGroupDeviceCustodySignerAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<IContactDeviceCustodySigner?>
        TryGetContactDeviceCustodySignerAsync(
            CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            return await owner.TryGetContactDeviceCustodySignerAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async ValueTask<DeepDirectMessagingMetadataSealingPublicKey>
        PrepareContactMetadataSealingKeyAsync(
            VerifiedContactRouteProposalAuthority proposal,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            return await owner.PrepareContactMetadataSealingKeyAsync(
                proposal, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async ValueTask<VerifiedContactUpdateRendezvous>
        EnsureGenesisContactUpdateRendezvousAsync(
            VerifiedContactRouteClosure route,
            DeepDirectMessagingMetadataSealingPublicKey metadataKey,
            ulong issuedAtUnixSeconds,
            ulong expiresAtUnixSeconds,
            CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            return await owner.EnsureGenesisContactUpdateRendezvousAsync(
                    route,
                    metadataKey,
                    issuedAtUnixSeconds,
                    expiresAtUnixSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async ValueTask<VerifiedContactUpdateRendezvous?>
        TryOpenCurrentContactUpdateRendezvousAsync(
            ulong trustedUnixSeconds,
            CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            return await owner.TryOpenCurrentContactUpdateRendezvousAsync(
                    trustedUnixSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
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
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            return await owner.EnsureGenesisDirectMessagingInventoryAsync(
                    preKeyService,
                    placement,
                    notBeforeUnixSeconds,
                    issuedAtUnixSeconds,
                    expiresAtUnixSeconds,
                    publicationOperationId,
                    oneTimePreKeyCount,
                    lastResortReuseLimit,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<DeepDirectMessagingStorageFacade?>
        TryGetDirectMessagingStorageAsync(
            CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            return await owner.TryGetDirectMessagingStorageAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<SqliteDeepMailboxStore?> TryGetMailboxStoreAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken).ConfigureAwait(false);
            return await owner.TryGetMailboxStoreAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    internal async ValueTask<ReachabilityMailboxHolderAuthority.ReachabilityMailboxHolderSigner?>
        OpenReachabilityMailboxHolderAsync(
            VerifiedContactRouteClosure route,
            ReadOnlyMemory<byte> locatorHash,
            MailboxCapabilityDomain domain,
            CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            return await owner.OpenReachabilityMailboxHolderAsync(
                    route, locatorHash, domain, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
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
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            return await owner.CreateInitialSessionDispatcherAsync(
                    grant,
                    holder,
                    primaryRoute,
                    fallbackRoute,
                    codec,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
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
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            return await owner.CreateMessagingReceiverAsync(
                    grant,
                    holder,
                    currentLocalRoute,
                    primaryRoute,
                    fallbackRoute,
                    codec,
                    pathAuthority,
                    localRecipient,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async ValueTask<DeepDirectMessagingInitiatorClaimStart?>
        TryBeginDirectMessagingInitiatorClaimAsync(
            ContactResolverReverifiedPeerAuthority? verifiedPeer,
            CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            return await owner.TryBeginDirectMessagingInitiatorClaimAsync(
                    verifiedPeer,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async ValueTask<DeepDirectMessagingInitiatorClaimPreparation?>
        TryCompleteDirectMessagingInitiatorClaimAsync(
            DeepDirectMessagingInitiatorClaimStart? startedClaim,
            VerifiedXpc1PreKeyClaimReceipt? verifiedClaim,
            int maximumMessagesWithoutPqInjection,
            CancellationToken cancellationToken = default)
    {
        var gateHeld = false;
        var delegated = false;
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateHeld = true;
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            delegated = true;
            return await owner.TryCompleteDirectMessagingInitiatorClaimAsync(
                    startedClaim,
                    verifiedClaim,
                    maximumMessagesWithoutPqInjection,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (!delegated)
            {
                startedClaim?.Dispose();
            }
            if (gateHeld)
            {
                gate.Release();
            }
        }
    }

    internal async ValueTask<DeepDirectMessagingInitiatorCommitResult?>
        TryCommitDirectMessagingInitiatorSessionAsync(
            DeepDirectMessagingInitiatorClaimPreparation? preparedClaim,
            VerifiedXpc1PreKeyClaimReceipt? verifiedClaim,
            CancellationToken cancellationToken = default)
    {
        var gateHeld = false;
        var delegated = false;
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateHeld = true;
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            delegated = true;
            return await owner.TryCommitDirectMessagingInitiatorSessionAsync(
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
            if (gateHeld)
            {
                gate.Release();
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
        var gateHeld = false;
        var delegated = false;
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateHeld = true;
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            delegated = true;
            return await owner.TryCommitDirectMessagingInitiatorSessionAsync(
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
            if (gateHeld)
                gate.Release();
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
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory, clock, networkIdFactory(),
                    secureStorageFactory, privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            return await owner.TryOpenDirectInboundMailboxEntryAsync(
                    currentLocalRoute, currentMailboxRoute, cursor, exactMeo1,
                    externalEnvelopeDigest, decodePolicy, cancellationToken)
                .ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

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
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            return await owner.TryVerifyResponderInitialClaimAsync(
                    initiation, localOffering, initiatorDirectory,
                    initiatorFreshness, claimPlacement, recipientAuthority,
                    recipientBundle, trustedTimeAuthority,
                    maximumMessagesWithoutPqInjection, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
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
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            return await owner.TryVerifyResponderInitialClaimAsync(
                    initiation,
                    initiatorDirectory,
                    initiatorFreshness,
                    claimPlacement,
                    recipientAuthority,
                    recipientBundle,
                    trustedTimeAuthority,
                    maximumMessagesWithoutPqInjection,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async ValueTask<DeepDirectMessagingUnsolicitedCommitResult?>
        TryCommitUnsolicitedResponderSessionAsync(
            VerifiedDph2InitialClaim? verifiedInitial,
            int maximumMessagesWithoutPqInjection,
            CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            return await owner.TryCommitUnsolicitedResponderSessionAsync(
                    verifiedInitial, maximumMessagesWithoutPqInjection,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
#endif

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

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            return await owner.TryGetDirectMessagingStorageAsync(
                    verifiedLocalAuthority,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async ValueTask<DeepDirectMessagingInventoryPublication?>
        EnsureDirectMessagingInventoryAsync(
            DeepDirectMessagingLocalAuthorityBinding? verifiedLocalAuthority,
            PreKeyV1InventoryAuthoringContext? authoringContext,
            CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            return await owner.EnsureDirectMessagingInventoryAsync(
                    verifiedLocalAuthority,
                    authoringContext,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async ValueTask<DeepDirectMessagingInitiatorClaimStart?>
        TryBeginDirectMessagingInitiatorClaimAsync(
            DeepDirectMessagingLocalAuthorityBinding? verifiedLocalAuthority,
            ContactResolverReverifiedPeerAuthority? verifiedPeer,
            CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            return await owner.TryBeginDirectMessagingInitiatorClaimAsync(
                    verifiedLocalAuthority,
                    verifiedPeer,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
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
        var gateHeld = false;
        var delegated = false;
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateHeld = true;
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            delegated = true;
            return await owner.TryCompleteDirectMessagingInitiatorClaimAsync(
                    verifiedLocalAuthority,
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
            if (gateHeld)
            {
                gate.Release();
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
        var gateHeld = false;
        var delegated = false;
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateHeld = true;
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            delegated = true;
            return await owner.TryCommitDirectMessagingInitiatorSessionAsync(
                    verifiedLocalAuthority,
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
            if (gateHeld)
            {
                gate.Release();
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
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            return await owner.TryCommitDirectMessagingResponderSessionAsync(
                    verifiedLocalAuthority,
                    verifiedSession,
                    relationship,
                    verifiedClaim,
                    verifiedInitiation,
                    maximumMessagesWithoutPqInjection,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
#endif

    internal async Task<IXPointNetworkStateStore> GetXPointNetworkStateStoreAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            return await owner.GetXPointNetworkStateStoreAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<IProtectedEntryGuardStore> GetProtectedEntryGuardStoreAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            return await owner.GetProtectedEntryGuardStoreAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<Deep.Client.Shared.Services.ContactV1.ContactAddressImportService>
        GetContactImporterAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (owner is null)
            {
                owner = await DeepAccountRuntimeOwner.OpenAsync(
                        appDataDirectory,
                        clock,
                        networkIdFactory(),
                        secureStorageFactory,
                        privacyStateProtectorFactory,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            return await owner.GetContactImporterAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<DeepContactResolvePersistenceBinding>
        GetContactResolvePersistenceBindingAsync(
            CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            return await owner.GetContactResolvePersistenceBindingAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<SqliteContactAddressPublicationStore>
        GetContactAddressPublicationStoreAsync(
            CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            return await owner.GetContactAddressPublicationStoreAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<DeepContactResolvePrivacyHostBinding>
        GetContactResolvePrivacyHostBindingAsync(
            CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepAccountRuntimeOwner.OpenAsync(
                    appDataDirectory,
                    clock,
                    networkIdFactory(),
                    secureStorageFactory,
                    privacyStateProtectorFactory,
                    cancellationToken)
                .ConfigureAwait(false);
            return await owner.GetContactResolvePrivacyHostBindingAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ResetLocalStateAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (owner is not null)
            {
                await owner.DisposeAsync().ConfigureAwait(false);
                owner = null;
            }
            await DeepAccountRuntimeOwner
                .DestroyAsync(appDataDirectory, secureStorageFactory, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            if (owner is not null)
            {
                await owner.DisposeAsync().ConfigureAwait(false);
                owner = null;
            }
        }
        finally
        {
            gate.Release();
        }
    }
}
