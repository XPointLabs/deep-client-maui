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

internal interface IMailboxRuntimeProvisioningSource
{
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
        var canonical = new byte[CanonicalBytes];
        try
        {
            if (localBytes.Length != SessionIdBytes || peerBytes.Length != SessionIdBytes ||
                selector.AccountScope.Value.Length != OpaqueBytes ||
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
            selector.AccountScope.Value.CopyTo(canonical.AsSpan(offset));
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
internal sealed class DevelopmentMailboxRuntimeProvisioningSource(
    Func<MailboxRuntimeProvisioning> provisioningFactory,
    Action<MailboxHolderIdentity> holderAvailable,
    HttpServiceTransportFactory transportFactory,
    HttpServiceClientOptions clientOptions,
    IPrivacyMailboxRouteSelectionObserver? routeSelectionObserver) :
    IMailboxRuntimeProvisioningSource
{
    private readonly Func<MailboxRuntimeProvisioning> provisioningFactory =
        provisioningFactory ?? throw new ArgumentNullException(nameof(provisioningFactory));
    private readonly Action<MailboxHolderIdentity> holderAvailable =
        holderAvailable ?? throw new ArgumentNullException(nameof(holderAvailable));
    private readonly HttpServiceTransportFactory transportFactory =
        transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
    private readonly HttpServiceClientOptions clientOptions =
        clientOptions ?? throw new ArgumentNullException(nameof(clientOptions));
    private readonly IPrivacyMailboxRouteSelectionObserver? routeSelectionObserver =
        routeSelectionObserver;

    public async Task<ProvisionedMailboxRuntime> ProvisionAsync(
        SqliteSessionStore store,
        MailboxHolderIdentity holder,
        MailboxInfrastructureOwnership ownership,
        CancellationToken cancellationToken = default)
    {
        holderAvailable(holder);
        var provisioning = provisioningFactory() ?? throw new InvalidOperationException(
            "The DEV-local mailbox provisioning factory returned no runtime.");
        var options = provisioning.ImportOptions;
        var material = await MailboxCredentialBundleImporter.ImportAsync(
            store, holder, options, ownership, cancellationToken).ConfigureAwait(false);
#if DEBUG && DEEP_PHYSICAL_E2E
        return new ProvisionedMailboxRuntime(
            material.Authority,
            material.Activation,
            material.DecodePolicies,
            material.LocalSessionId,
            material.SelfSelector,
            (recipient, _) => Task.FromResult<MailboxCredentialSelector?>(
                recipient == material.LocalSessionId
                    ? material.SelfSelector
                    : recipient == material.PeerSessionId
                        ? material.PeerSelector
                        : null),
            transportFactory.CreatePrivacyRoutedMailboxIngress(
                provisioning.PrivacyRoutes.Primary,
                provisioning.PrivacyRoutes.Fallback,
                material.DecodePolicies,
                clientOptions,
                routeSelectionObserver: routeSelectionObserver),
            options.TimeProvider);
#else
        throw new InvalidOperationException(
            "DEV-local mailbox credentials are forbidden outside physical Debug builds.");
#endif
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
        IMailboxDispatchRouteUsageObserver? routeUsageObserver = null)
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

            var transport = new NativeMau2MailboxTransport(
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
            current = new BoundRuntime(provisioned, transport);
            Volatile.Write(ref bound, current);
            return current;
        }
        finally
        {
            bindGate.Release();
        }
    }

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
