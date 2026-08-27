using System.Security.Cryptography.X509Certificates;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Protocol.DeepExtension.MailboxTopology;

namespace Deep.Client.Maui.Services;

/// <summary>
/// One account-generation production composition shared by transport delivery, invitation
/// publication and authenticated contact onboarding.
/// </summary>
internal sealed class ProductionMailboxRuntimeCoordinator :
    IMailboxRuntimeProvisioningSource,
    IContactMailboxOnboarding,
    IContactInvitationProvider,
    IGroupMailboxRouteExchange,
    IDisposable
{
    internal const string RoutesResource =
        "Deep.Client.Maui.production-mailbox-privacy-routes.v1.json";
    internal const string RoutesSignatureResource =
        "Deep.Client.Maui.production-mailbox-privacy-routes.v1.sig";
    internal const string RoutesPublicKeyResource =
        "Deep.Client.Maui.production-mailbox-privacy-routes.v1.pub";

    private readonly ProductionMailboxRegistryClient registry;
    private readonly HttpClient registryHttpClient;
    private readonly string protectedRoot;
    private readonly HttpServiceTransportFactory transportFactory;
    private readonly HttpServiceClientOptions clientOptions;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim provisionGate = new(1, 1);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object attachmentGate = new();
    private RuntimeAttachment? attachment;
    private BoundState? bound;
    private int disposed;

    public ProductionMailboxRuntimeCoordinator(
        Uri registryOrigin,
        string appDataDirectory,
        HttpServiceTransportFactory transportFactory,
        HttpServiceClientOptions clientOptions,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(registryOrigin);
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        this.transportFactory = transportFactory ??
            throw new ArgumentNullException(nameof(transportFactory));
        this.clientOptions = clientOptions ??
            throw new ArgumentNullException(nameof(clientOptions));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        protectedRoot = Path.Combine(
            Path.GetFullPath(appDataDirectory), "production-mailbox-runtime-v1");
        var handler = HttpServiceTransportFactory.CreateHttpHandler(clientOptions, null);
        handler.AutomaticDecompression = System.Net.DecompressionMethods.None;
        handler.SslOptions.CertificateRevocationCheckMode = X509RevocationMode.Online;
        registryHttpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        registry = new ProductionMailboxRegistryClient(registryHttpClient, registryOrigin);
    }

    public bool CanAccept(string contactInput)
    {
        if (string.IsNullOrWhiteSpace(contactInput)) return false;
        try
        {
            _ = ContactMailboxInvitationService.ParseAndVerify(
                contactInput.Trim(), timeProvider);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<SessionId> PrepareAsync(
        string contactInput,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var canonical = contactInput.Trim();
        var invitation = ContactMailboxInvitationService.ParseAndVerify(
            canonical, timeProvider);
        await EnsureBoundToActiveAccountAsync(cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = RequireBound();
            if (invitation.SessionId == state.Material.LocalSessionId)
                throw new InvalidOperationException(
                    "The active account invitation cannot be added as a contact.");
            var imported = await state.Acquirer.AcquirePeerDepositAsync(
                invitation, cancellationToken).ConfigureAwait(false);
            await state.ContactState.SavePeerInvitationAsync(
                state.Material.LocalSessionId,
                invitation.SessionId,
                canonical,
                cancellationToken).ConfigureAwait(false);
            await state.PeerSelectorState.SaveAsync(
                state.Material.LocalSessionId,
                invitation.SessionId,
                imported.PeerSelector,
                cancellationToken).ConfigureAwait(false);
            state.PeerSelectors[invitation.SessionId] = imported.PeerSelector;
            return invitation.SessionId;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<string?> GetInvitationAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsureBoundToActiveAccountAsync(cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = RequireBound();
            return await GetOrCreateLocalInvitationLockedAsync(state, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<GroupMailboxRouteBundle?> CaptureForPublishAsync(
        Group group,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(group);
        await EnsureBoundToActiveAccountAsync(cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = RequireBound();
            if (!group.Members.Any(member =>
                    member.SessionId == state.Material.LocalSessionId &&
                    !member.IsPendingRemoval))
                throw new InvalidOperationException(
                    "The active account is not an active member of the group.");

            var invitations = new List<GroupMemberMailboxInvitation>(group.Members.Count);
            foreach (var member in group.Members
                         .OrderBy(static value => value.SessionId.Value, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var encoded = member.SessionId == state.Material.LocalSessionId
                    ? await GetOrCreateLocalInvitationLockedAsync(state, cancellationToken)
                        .ConfigureAwait(false)
                    : await state.ContactState.LoadPeerInvitationAsync(
                        state.Material.LocalSessionId,
                        member.SessionId,
                        cancellationToken).ConfigureAwait(false);
                if (encoded is null)
                    throw new InvalidOperationException(
                        $"Authenticated mailbox invitation is unavailable for group member {member.SessionId}.");
                var verified = ContactMailboxInvitationService.ParseAndVerify(
                    encoded, timeProvider);
                if (verified.SessionId != member.SessionId)
                    throw new InvalidDataException(
                        "Persisted group member invitation has a different Session ID.");
                invitations.Add(new GroupMemberMailboxInvitation(
                    member.SessionId,
                    ContactMailboxInvitationService.DecodeCanonicalText(encoded)));
            }

            return new GroupMailboxRouteBundle(
                group.Id,
                group.Revision,
                E2eeContentCodec.ComputeGroupMembershipDigest(group),
                invitations);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ImportReceivedAsync(
        SessionId localAccount,
        Group group,
        GroupMailboxRouteBundle bundle,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(bundle);
        GroupMailboxRouteBundleCodec.ValidateForGroup(bundle, group);

        var verified = bundle.Invitations.Select(invitation =>
        {
            var value = ContactMailboxInvitationService.ParseAndVerify(
                invitation.CanonicalInvitation, timeProvider);
            if (value.SessionId != invitation.Member)
                throw new InvalidDataException(
                    "A group mailbox invitation is bound to a different Session ID.");
            return (invitation.Member, Value: value,
                Text: ContactMailboxInvitationService.EncodeCanonicalBinary(
                    invitation.CanonicalInvitation));
        }).ToArray();

        await EnsureBoundToActiveAccountAsync(cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = RequireBound();
            if (localAccount != state.Material.LocalSessionId)
                throw new InvalidOperationException(
                    "The group mailbox route bundle targets another active account.");

            foreach (var invitation in verified)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (invitation.Member == localAccount)
                {
                    if (!IsCurrentLocalInvitation(state, invitation.Value))
                        throw new InvalidDataException(
                            "The group mailbox route bundle contains a stale local route.");
                    continue;
                }

                var imported = await state.Acquirer.AcquirePeerDepositAsync(
                    invitation.Value, cancellationToken).ConfigureAwait(false);
                await state.ContactState.SavePeerInvitationAsync(
                    localAccount,
                    invitation.Member,
                    invitation.Text,
                    cancellationToken).ConfigureAwait(false);
                await state.PeerSelectorState.SaveAsync(
                    localAccount,
                    invitation.Member,
                    imported.PeerSelector,
                    cancellationToken).ConfigureAwait(false);
                state.PeerSelectors[invitation.Member] = imported.PeerSelector;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string> GetOrCreateLocalInvitationLockedAsync(
        BoundState state,
        CancellationToken cancellationToken)
    {
        var existing = await state.ContactState.LoadLocalInvitationAsync(
            state.Material.LocalSessionId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            try
            {
                var verified = ContactMailboxInvitationService.ParseAndVerify(
                    existing, timeProvider);
                if (IsCurrentLocalInvitation(state, verified)) return existing;
            }
            catch (ContactMailboxInvitationException)
            {
                // A stale cache is replaceable only from the currently verified bound route below.
            }
        }

        var advertisement = ProductionMailboxRouteAdvertisementCodec.DecodeAdvertisement(
            state.PublicRoute.CanonicalRouteAdvertisement.Span);
        var expires = Math.Min(
            state.PublicRoute.ExpiresAtUnixSeconds,
            Math.Min(advertisement.Certificate.ExpiresAtUnixSeconds,
                advertisement.ExpiresAtUnixSeconds));
        var invitation = ContactMailboxInvitationService.Create(
            state.SessionIdentity,
            state.PublicRoute.MailboxOwnerEd25519PublicKey.Span,
            state.PublicRoute.CanonicalRouteAdvertisement.Span,
            DateTimeOffset.FromUnixTimeSeconds(checked((long)expires)),
            timeProvider);
        await state.ContactState.SaveLocalInvitationAsync(
            state.Material.LocalSessionId, invitation, cancellationToken)
            .ConfigureAwait(false);
        return invitation;
    }

    private static bool IsCurrentLocalInvitation(
        BoundState state,
        VerifiedContactMailboxInvitation invitation) =>
        invitation.SessionId == state.Material.LocalSessionId &&
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            invitation.MailboxOwnerEd25519PublicKey.Span,
            state.PublicRoute.MailboxOwnerEd25519PublicKey.Span) &&
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            invitation.CanonicalRouteAdvertisement.Span,
            state.PublicRoute.CanonicalRouteAdvertisement.Span);

    public async Task<ProvisionedMailboxRuntime> ProvisionAsync(
        SqliteSessionStore store,
        MailboxHolderIdentity holder,
        MailboxInfrastructureOwnership ownership,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(holder);
        if (ownership != MailboxInfrastructureOwnership.OfficialManaged)
            throw new InvalidOperationException(
                "Production mailbox acquisition requires official-managed ownership.");

        await provisionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (bound is not null)
                {
                    if (bound.Material.LocalSessionId != holder.SessionId ||
                        !string.Equals(bound.StoreIdentity, store.CanonicalStateIdentity,
                            StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            "Production mailbox coordinator is already bound to another account generation.");
                    return CreateProvisioned(bound);
                }
            }
            finally
            {
                gate.Release();
            }

            if (!ProductionMailboxBuildTrustFloor.TryLoad(out var anchor) || anchor is null)
                throw new InvalidOperationException("production-credentials-unavailable");
            var routes = ProductionMailboxPrivacyRouteBootstrap.Load(
                typeof(ProductionMailboxRuntimeCoordinator).Assembly,
                RoutesResource,
                RoutesSignatureResource,
                RoutesPublicKeyResource,
                anchor,
                timeProvider);
            var clientIdentity = await ProductionMailboxClientIdentityAttestor.AttestAsync(
                cancellationToken).ConfigureAwait(false);
            var trust = ProtectedProductionMailboxTrustStateStore.OpenOrCreate(protectedRoot);
            var phrase = await ProductionMailboxOwnerIdentityStore
                .GetRecoveryPhraseForDurableAccountAsync(async () =>
                    (await store.GetAsync<SessionAccount>(
                        SessionAccountService.ActiveAccountKey,
                        cancellationToken).ConfigureAwait(false))?.SessionId,
                    cancellationToken).ConfigureAwait(false);
            if (!SessionAccountService.IsCanonicalRecoveryPhrase(phrase))
                throw new InvalidOperationException(
                    "The durable account recovery identity is unavailable.");

            var sessionIdentity = new SessionIdentityProvider(phrase!);
            ProductionMailboxOwnerIdentity? ownerIdentity = null;
            try
            {
                if (sessionIdentity.SessionId != holder.SessionId)
                    throw new InvalidOperationException(
                        "Production mailbox holder differs from the active account.");
                var sessionKey = sessionIdentity.GetEd25519PublicKey();
                try
                {
                    if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                            sessionKey, holder.Ed25519PublicKey.Span))
                        throw new InvalidOperationException(
                            "Production mailbox holder key differs from the active account.");
                }
                finally
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(sessionKey);
                }

                ownerIdentity = await ProductionMailboxOwnerIdentityStore.LoadForAccountAsync(
                    holder.SessionId, cancellationToken).ConfigureAwait(false);
                var ownerKey = ownerIdentity.GetPublicKey();
                try
                {
                    var active = await ProductionMailboxCredentialBundleImporter
                        .LoadActiveLocalOwnerAsync(
                            store,
                            holder,
                            anchor,
                            trust,
                            clientIdentity,
                            ownerKey,
                            ownership,
                            timeProvider,
                            cancellationToken)
                        .ConfigureAwait(false);
                    var acquirer = new ProductionMailboxCredentialAcquirer(
                        registry,
                        store,
                        sessionIdentity,
                        ownerIdentity,
                        anchor,
                        trust,
                        clientIdentity,
                        timeProvider);
                    var routeState = new ProductionMailboxLocalOwnerRouteStateStore(store);
                    ProductionMailboxLocalOwnerPublicRoute? predecessorRoute =
                        active.Status switch
                        {
                            ProductionMailboxActiveBundleStatus.RefreshRecommended =>
                                active.PublicRoute,
                            ProductionMailboxActiveBundleStatus.Expired =>
                                await routeState.LoadAsync(
                                        holder.SessionId, cancellationToken)
                                    .ConfigureAwait(false),
                            _ => null
                        };
                    if (active.Status is
                            ProductionMailboxActiveBundleStatus.RefreshRecommended or
                            ProductionMailboxActiveBundleStatus.Expired &&
                        predecessorRoute is null)
                        throw new InvalidDataException(
                            "Production mailbox route continuity is unavailable for authenticated refresh.");
                    var activation = await ProductionMailboxLocalOwnerLifecycle.ResolveAsync(
                            active,
                            ownerKey,
                            token => acquirer.AcquireLocalOwnerAsync(
                                predecessorRoute, token),
                            cancellationToken)
                        .ConfigureAwait(false);
                    await routeState.SaveAsync(
                            holder.SessionId,
                            activation.PublicRoute,
                            cancellationToken)
                        .ConfigureAwait(false);

                    var state = new BoundState(
                        store.CanonicalStateIdentity,
                        activation.Material,
                        activation.PublicRoute,
                        sessionIdentity,
                        ownerIdentity,
                        acquirer,
                        new ProductionMailboxContactStateStore(store),
                        new PersistedMailboxPeerSelectorStore(store),
                        routes);
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        ThrowIfDisposed();
                        bound = state;
                        sessionIdentity = null!;
                        ownerIdentity = null;
                        return CreateProvisioned(state);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
                finally
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(ownerKey);
                }
            }
            finally
            {
                ownerIdentity?.Dispose();
                sessionIdentity?.Dispose();
            }
        }
        finally
        {
            provisionGate.Release();
        }
    }

    private ProvisionedMailboxRuntime CreateProvisioned(BoundState state) => new(
        state.Material.Authority,
        state.Material.Activation,
        state.Material.DecodePolicies,
        state.Material.LocalSessionId,
        state.Material.SelfSelector,
        ResolveRecipientAsync,
        transportFactory.CreatePrivacyRoutedMailboxIngress(
            state.Routes.Primary,
            state.Routes.Fallback,
            state.Material.DecodePolicies,
            clientOptions),
        timeProvider);

    private async Task<MailboxCredentialSelector?> ResolveRecipientAsync(
        SessionId recipient,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = RequireBound();
            if (recipient == state.Material.LocalSessionId)
                return state.Material.SelfSelector;
            if (state.PeerSelectors.TryGetValue(recipient, out var selector))
                return selector;
            var persistedSelector = await state.PeerSelectorState.LoadAsync(
                state.Material.LocalSessionId, recipient, cancellationToken)
                .ConfigureAwait(false);
            if (persistedSelector is not null)
            {
                state.PeerSelectors[recipient] = persistedSelector;
                return persistedSelector;
            }
            var encoded = await state.ContactState.LoadPeerInvitationAsync(
                state.Material.LocalSessionId, recipient, cancellationToken)
                .ConfigureAwait(false);
            if (encoded is null) return null;
            var invitation = ContactMailboxInvitationService.ParseAndVerify(
                encoded, timeProvider);
            if (invitation.SessionId != recipient)
                throw new InvalidDataException(
                    "Persisted contact invitation differs from its requested Session ID.");
            var imported = await state.Acquirer.AcquirePeerDepositAsync(
                invitation, cancellationToken).ConfigureAwait(false);
            await state.PeerSelectorState.SaveAsync(
                state.Material.LocalSessionId,
                recipient,
                imported.PeerSelector,
                cancellationToken).ConfigureAwait(false);
            state.PeerSelectors[recipient] = imported.PeerSelector;
            return imported.PeerSelector;
        }
        finally
        {
            gate.Release();
        }
    }

    private BoundState RequireBound() => bound ?? throw new InvalidOperationException(
        "Production mailbox runtime is not bound to an active account.");

    public void AttachRuntimeState(
        SqliteSessionStore store,
        SecureRecoverySessionStore secureStore)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(secureStore);
        lock (attachmentGate)
        {
            ThrowIfDisposed();
            if (attachment is not null &&
                (!ReferenceEquals(attachment.Store, store) ||
                 !ReferenceEquals(attachment.SecureStore, secureStore)))
                throw new InvalidOperationException(
                    "Production mailbox coordinator is already attached to another runtime store.");
            attachment ??= new RuntimeAttachment(store, secureStore);
        }
    }

    public async Task ReleaseAsync(
        SessionId account,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await provisionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (bound is null) return;
                if (bound.Material.LocalSessionId != account)
                    throw new InvalidOperationException(
                        "The released account differs from the bound production mailbox account.");
                DisposeBoundState(bound);
                bound = null;
            }
            finally
            {
                gate.Release();
            }
        }
        finally
        {
            provisionGate.Release();
        }
    }

    private async Task EnsureBoundToActiveAccountAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (bound is not null) return;
        }
        finally
        {
            gate.Release();
        }

        RuntimeAttachment context;
        lock (attachmentGate)
        {
            context = attachment ?? throw new InvalidOperationException(
                "Production mailbox runtime state is not attached.");
        }
        var phrase = await context.SecureStore.GetAsync<string>(
            SessionAccountService.ActiveRecoveryPhraseKey,
            cancellationToken).ConfigureAwait(false);
        if (!SessionAccountService.IsCanonicalRecoveryPhrase(phrase))
            throw new InvalidOperationException(
                "Authenticated MAU2 cannot bind before a canonical account exists.");
        using var identity = new SessionIdentityProvider(phrase!);
        var publicKey = identity.GetEd25519PublicKey();
        try
        {
            var provisioned = await ProvisionAsync(
                context.Store,
                new MailboxHolderIdentity(identity.SessionId, publicKey),
                MailboxInfrastructureOwnership.OfficialManaged,
                cancellationToken).ConfigureAwait(false);
            (provisioned.Ingress as IDisposable)?.Dispose();
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(publicKey);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(
        Volatile.Read(ref disposed) != 0, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        provisionGate.Wait();
        try
        {
            gate.Wait();
            try
            {
                if (bound is not null) DisposeBoundState(bound);
                bound = null;
                registryHttpClient.Dispose();
            }
            finally
            {
                gate.Release();
                gate.Dispose();
            }
        }
        finally
        {
            provisionGate.Release();
            provisionGate.Dispose();
        }
    }

    private static void DisposeBoundState(BoundState state)
    {
        state.OwnerIdentity.Dispose();
        state.SessionIdentity.Dispose();
        state.PeerSelectors.Clear();
    }

    private sealed record RuntimeAttachment(
        SqliteSessionStore Store,
        SecureRecoverySessionStore SecureStore);

    private sealed class BoundState(
        string storeIdentity,
        ImportedProductionMailboxRuntimeMaterial material,
        ProductionMailboxLocalOwnerPublicRoute publicRoute,
        SessionIdentityProvider sessionIdentity,
        ProductionMailboxOwnerIdentity ownerIdentity,
        ProductionMailboxCredentialAcquirer acquirer,
        ProductionMailboxContactStateStore contactState,
        PersistedMailboxPeerSelectorStore peerSelectorState,
        MailboxPrivacyRouteSet routes)
    {
        public string StoreIdentity { get; } = storeIdentity;
        public ImportedProductionMailboxRuntimeMaterial Material { get; } = material;
        public ProductionMailboxLocalOwnerPublicRoute PublicRoute { get; } = publicRoute;
        public SessionIdentityProvider SessionIdentity { get; } = sessionIdentity;
        public ProductionMailboxOwnerIdentity OwnerIdentity { get; } = ownerIdentity;
        public ProductionMailboxCredentialAcquirer Acquirer { get; } = acquirer;
        public ProductionMailboxContactStateStore ContactState { get; } = contactState;
        public PersistedMailboxPeerSelectorStore PeerSelectorState { get; } =
            peerSelectorState;
        public MailboxPrivacyRouteSet Routes { get; } = routes;
        public Dictionary<SessionId, MailboxCredentialSelector> PeerSelectors { get; } = [];
    }
}
