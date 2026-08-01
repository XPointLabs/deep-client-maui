using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Features;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Services;

/// <summary>
/// App-private, store-bound DEV-local MAU2 composition. Binding is deferred until an account
/// exists, then the Mr. X-approved pair is imported once and no raw transport fallback exists.
/// </summary>
internal sealed class StoreBoundNativeMau2Transport :
    IAuthenticatedOpaqueMailboxTransport,
    IAuthenticatedInboxTransport,
    IMetadataPrivateSessionMessageTransport,
    IMailboxDeliveryPolicy,
    IAccountGenerationLifecycle,
    IDisposable
{
    private readonly SqliteSessionStore store;
    private readonly SecureRecoverySessionStore secureStore;
    private readonly Func<MailboxCredentialBundleImportOptions> importOptionsFactory;
    private readonly Action<MailboxHolderIdentity> holderAvailable;
    private readonly MailboxInfrastructureOwnership ownership;
    private readonly ClientFeatureFlags featureFlags;
    private readonly SemaphoreSlim bindGate = new(1, 1);
    private readonly object operationGate = new();
    private BoundRuntime? bound;
    private TaskCompletionSource? operationsDrained;
    private int activeOperations;
    private LifecycleState lifecycleState = LifecycleState.Active;
    private int disposed;

    public StoreBoundNativeMau2Transport(
        SqliteSessionStore store,
        SecureRecoverySessionStore secureStore,
        Func<MailboxCredentialBundleImportOptions> importOptionsFactory,
        Action<MailboxHolderIdentity> holderAvailable,
        MailboxInfrastructureOwnership ownership,
        ClientFeatureFlags featureFlags)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.secureStore = secureStore ?? throw new ArgumentNullException(nameof(secureStore));
        this.importOptionsFactory = importOptionsFactory ??
            throw new ArgumentNullException(nameof(importOptionsFactory));
        this.holderAvailable = holderAvailable ??
            throw new ArgumentNullException(nameof(holderAvailable));
        if (ownership is not (MailboxInfrastructureOwnership.UserManaged or
            MailboxInfrastructureOwnership.OfficialManaged))
            throw new ArgumentOutOfRangeException(nameof(ownership));
        this.ownership = ownership;
        this.featureFlags = featureFlags ?? throw new ArgumentNullException(nameof(featureFlags));
        if (!featureFlags.ClientMailboxAdapterEnabled ||
            !featureFlags.MetadataPrivateTransportRequired)
        {
            throw new InvalidOperationException(
                "Authenticated MAU2 requires the native adapter and metadata-private release gates.");
        }
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
        if (request.Kind != MailboxDeliveryKind.Direct)
        {
            throw new NotSupportedException(
                "DEV-local mailbox schema v1 provisions only the Android/Windows direct pair.");
        }

        var runtime = await EnsureBoundAsync(
            request.Envelope.Sender, holderPublicKey: null, cancellationToken)
            .ConfigureAwait(false);
        var selector = request.Envelope.Recipient switch
        {
            var recipient when recipient == runtime.Material.LocalSessionId =>
                runtime.Material.SelfSelector,
            var recipient when recipient == runtime.Material.PeerSessionId =>
                runtime.Material.PeerSelector,
            _ => throw new NotSupportedException(
                "DEV-local mailbox schema v1 has no third-contact credential.")
        };
        var decision = new MailboxDeliveryDecision(
            MailboxTransportProtocol.AuthenticatedMau2,
            ownership,
            runtime.Material.Authority,
            selector);
        decision.Validate();
        return decision;
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
        await runtime.Transport.SendPreparedMailboxAuthenticatedAsync(
            preparedSend, cancellationToken).ConfigureAwait(false);
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
        using var operation = EnterOperation();
        var publicKey = identity.GetEd25519PublicKey();
        try
        {
            var runtime = await EnsureBoundAsync(
                identity.SessionId, publicKey, cancellationToken).ConfigureAwait(false);
            return await runtime.Transport.ReceiveAuthenticatedAsync(identity, cancellationToken)
                .ConfigureAwait(false);
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
        using var operation = EnterOperation();
        var publicKey = identity.GetEd25519PublicKey();
        try
        {
            var runtime = await EnsureBoundAsync(
                identity.SessionId, publicKey, cancellationToken).ConfigureAwait(false);
            return await runtime.Transport.RetrieveAuthenticatedAsync(
                identity, cursor, limit, cancellationToken).ConfigureAwait(false);
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
        using var operation = EnterOperation();
        var publicKey = signer.GetEd25519PublicKey();
        try
        {
            var runtime = await EnsureBoundAsync(
                signer.SessionId, publicKey, cancellationToken).ConfigureAwait(false);
            return await runtime.Transport.RetrieveOpaqueMailboxInboxAsync(
                signer, continuation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(publicKey);
        }
    }

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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Task drain;
        lock (operationGate)
        {
            lifecycleState = LifecycleState.Disposed;
            drain = activeOperations == 0
                ? Task.CompletedTask
                : (operationsDrained ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
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
        lock (operationGate)
        {
            ThrowIfDisposed();
            if (lifecycleState != LifecycleState.Active)
                throw new InvalidOperationException(
                    "The MAU2 account generation is not active.");
            lifecycleState = LifecycleState.Stopping;
            drain = activeOperations == 0
                ? Task.CompletedTask
                : (operationsDrained ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        await drain.ConfigureAwait(false);
        await bindGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var current = bound;
            if (current is not null && current.Material.LocalSessionId != account)
            {
                throw new InvalidOperationException(
                    "The stopped account differs from the bound MAU2 account.");
            }
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

            ImportedMailboxRuntimeMaterial material;
            MailboxCredentialBundleImportOptions importOptions;
            if (holderPublicKey is not null)
            {
                holderAvailable(new MailboxHolderIdentity(sessionId, holderPublicKey));
                importOptions = importOptionsFactory() ?? throw new InvalidOperationException(
                    "The DEV-local mailbox provisioning factory returned no options.");
                material = await MailboxCredentialBundleImporter.ImportAsync(
                    store,
                    new MailboxHolderIdentity(sessionId, holderPublicKey),
                    importOptions,
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
                    var holder = new MailboxHolderIdentity(sessionId, publicKey);
                    holderAvailable(holder);
                    importOptions = importOptionsFactory() ?? throw new InvalidOperationException(
                        "The DEV-local mailbox provisioning factory returned no options.");
                    material = await MailboxCredentialBundleImporter.ImportAsync(
                        store, holder, importOptions, ownership, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(publicKey);
                }
            }

            var ingress = CreateIngress(material);
            var transport = new NativeMau2MailboxTransport(
                featureFlags,
                material.Activation,
                ingress,
                store,
                new PinnedClientMailboxReceiptVerifier(
                    new SodiumClientMailboxReceiptCrypto()),
                material.DecodePolicies,
                material.Authority,
                account => account == material.LocalSessionId
                    ? material.SelfSelector
                    : throw new InvalidOperationException(
                        "MAU2 self selector was requested for another account."),
                ownsIngress: true,
                importOptions.TimeProvider);
            current = new BoundRuntime(material, transport);
            Volatile.Write(ref bound, current);
            return current;
        }
        finally
        {
            bindGate.Release();
        }
    }

    private static IClientMailboxBinaryIngress CreateIngress(
        ImportedMailboxRuntimeMaterial material)
    {
#if DEBUG && DEEP_PHYSICAL_E2E
        return HttpClientMailboxBinaryIngress.CreatePhysicalDevelopment(
            material.Coordinator,
            material.DecodePolicies);
#else
        throw new InvalidOperationException(
            "DEV-local mailbox credentials and cleartext ingress are forbidden outside physical Debug builds.");
#endif
    }

    private BoundRuntime RequireBound()
    {
        ThrowIfDisposed();
        return Volatile.Read(ref bound) ?? throw new InvalidOperationException(
            "Native MAU2 transport has not bound an account identity.");
    }

    private static void RequireSession(BoundRuntime runtime, SessionId sessionId)
    {
        if (runtime.Material.LocalSessionId != sessionId)
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
        ImportedMailboxRuntimeMaterial Material,
        NativeMau2MailboxTransport Transport);

    private sealed class OperationLease(StoreBoundNativeMau2Transport owner) : IDisposable
    {
        private StoreBoundNativeMau2Transport? activeOwner = owner;

        public void Dispose() => Interlocked.Exchange(ref activeOwner, null)?.ExitOperation();
    }

    private enum LifecycleState
    {
        Active = 0,
        Stopping = 1,
        Stopped = 2,
        Disposed = 3
    }
}
