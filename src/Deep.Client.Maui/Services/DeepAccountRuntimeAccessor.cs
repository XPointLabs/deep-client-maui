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
using Deep.Protocol.ApplicationCore;
using Deep.Protocol.ContactV1;
using Deep.Protocol.DeepNative;
using Deep.Protocol.GroupV1;
using Deep.Protocol.Identity;
using Deep.Protocol.MessagingWire;

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
                    localAgreementAuthority,
                    verifiedDevice,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
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

    internal async ValueTask<DeepDirectMessagingInitiatorClaimPreparation?>
        TryPrepareDirectMessagingInitiatorClaimAsync(
            DeepDirectMessagingLocalAuthorityBinding? verifiedLocalAuthority,
            ContactResolverReverifiedPeerAuthority? verifiedPeer,
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
            return await owner.TryPrepareDirectMessagingInitiatorClaimAsync(
                    verifiedLocalAuthority,
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
