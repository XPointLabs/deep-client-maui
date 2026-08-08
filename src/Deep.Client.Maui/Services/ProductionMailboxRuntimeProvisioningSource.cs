using System.Security.Cryptography;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Services;

internal interface IProductionMailboxLocalOwnerAcquirer
{
    Task<ProductionMailboxLocalOwnerBundle> AcquireAsync(
        SessionIdentityProvider holder,
        ProductionMailboxOwnerIdentity owner,
        ProductionMailboxClientApprovalIdentity clientIdentity,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Release-only LocalOwner composition. It deliberately exposes only the self selector until the
/// signed route-advertisement protocol can authenticate arbitrary PeerDeposit routes.
/// </summary>
internal sealed class ProductionMailboxRuntimeProvisioningSource(
    SecureRecoverySessionStore secureStore,
    IProductionMailboxLocalOwnerAcquirer acquirer,
    ProductionMailboxTrustAnchor buildAnchor,
    IProductionMailboxTrustStateStore trustStateStore,
    ProductionMailboxClientApprovalIdentity clientIdentity,
    TimeProvider? timeProvider = null) : IMailboxRuntimeProvisioningSource
{
    private readonly SecureRecoverySessionStore secureStore = secureStore ??
        throw new ArgumentNullException(nameof(secureStore));
    private readonly IProductionMailboxLocalOwnerAcquirer acquirer = acquirer ??
        throw new ArgumentNullException(nameof(acquirer));
    private readonly ProductionMailboxTrustAnchor buildAnchor = buildAnchor ??
        throw new ArgumentNullException(nameof(buildAnchor));
    private readonly IProductionMailboxTrustStateStore trustStateStore = trustStateStore ??
        throw new ArgumentNullException(nameof(trustStateStore));
    private readonly ProductionMailboxClientApprovalIdentity clientIdentity = clientIdentity ??
        throw new ArgumentNullException(nameof(clientIdentity));
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ProvisionedMailboxRuntime> ProvisionAsync(
        SqliteSessionStore store,
        MailboxHolderIdentity holder,
        MailboxInfrastructureOwnership ownership,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(holder);
        if (ownership != MailboxInfrastructureOwnership.OfficialManaged)
            throw new InvalidOperationException(
                "Production Registry provisioning is official-managed only.");
        var phrase = await secureStore.GetAsync<string>(
            SessionAccountService.ActiveRecoveryPhraseKey,
            cancellationToken).ConfigureAwait(false);
        if (!SessionAccountService.IsCanonicalRecoveryPhrase(phrase))
            throw new InvalidOperationException(
                "Production LocalOwner provisioning requires the active account identity.");
        using var identity = new SessionIdentityProvider(phrase!);
        var publicKey = identity.GetEd25519PublicKey();
        try
        {
            if (identity.SessionId != holder.SessionId ||
                holder.Ed25519PublicKey.Length != publicKey.Length ||
                !CryptographicOperations.FixedTimeEquals(
                    holder.Ed25519PublicKey.Span, publicKey))
                throw new InvalidOperationException(
                    "Production enrollment holder differs from the active account.");
            using var owner = await ProductionMailboxOwnerIdentityStore.LoadForAccountAsync(
                holder.SessionId,
                cancellationToken).ConfigureAwait(false);
            var expectedOwner = owner.GetPublicKey();
            try
            {
                // Reconcile an interrupted protected-LKG/SQLite publication before making any
                // Registry request. The durable journal contains only signed public material and
                // is fully reverified, so this path works after an offline restart.
                var material = await ProductionMailboxCredentialBundleImporter
                    .TryRecoverPendingLocalOwnerAsync(
                        store,
                        holder,
                        buildAnchor,
                        trustStateStore,
                        clientIdentity,
                        expectedOwner,
                        ownership,
                        timeProvider,
                        cancellationToken).ConfigureAwait(false);
                var active = material is null
                    ? await ProductionMailboxCredentialBundleImporter.LoadActiveLocalOwnerAsync(
                        store,
                        holder,
                        buildAnchor,
                        trustStateStore,
                        clientIdentity,
                        expectedOwner,
                        ownership,
                        timeProvider,
                        cancellationToken).ConfigureAwait(false)
                    : null;
                if (active?.Status == ProductionMailboxActiveBundleStatus.Corrupt)
                    throw new InvalidDataException(
                        "Active production mailbox bundle is corrupt; Registry acquisition is forbidden.");
                if (active is not null && active.Status is not (
                        ProductionMailboxActiveBundleStatus.Absent or
                        ProductionMailboxActiveBundleStatus.Valid or
                        ProductionMailboxActiveBundleStatus.RefreshRecommended or
                        ProductionMailboxActiveBundleStatus.Expired))
                    throw new InvalidDataException(
                        "Active production mailbox bundle has an unsupported state.");
                material ??= active?.Status is ProductionMailboxActiveBundleStatus.Valid
                    ? active.Material
                    : null;
                if (material is null &&
                    active?.Status is ProductionMailboxActiveBundleStatus.RefreshRecommended)
                {
                    try
                    {
                        material = await AcquireAndImportAsync(
                            store,
                            holder,
                            identity,
                            owner,
                            expectedOwner,
                            ownership,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (
                        exception is HttpRequestException or IOException or TimeoutException)
                    {
                        // The active signed bundle was fully reverified above and remains within
                        // its validity window. A transient control-plane outage must not turn a
                        // proactive refresh into an offline startup failure.
                        material = active.Material;
                    }
                }
                if (material is null)
                {
                    material = await AcquireAndImportAsync(
                            store,
                            holder,
                            identity,
                            owner,
                            expectedOwner,
                            ownership,
                            cancellationToken).ConfigureAwait(false);
                }
                var ingress = new OrderedReplicaMailboxBinaryIngress(
                    material.CurrentIngress,
                    material.NextIngress,
                    material.DecodePolicies);
                return new ProvisionedMailboxRuntime(
                    material.Authority,
                    material.Activation,
                    material.DecodePolicies,
                    material.LocalSessionId,
                    recipient => recipient == material.LocalSessionId
                        ? material.SelfSelector
                        : null,
                    ingress,
                    timeProvider);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expectedOwner);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(publicKey);
        }
    }

    private async Task<ImportedProductionMailboxRuntimeMaterial> AcquireAndImportAsync(
        SqliteSessionStore store,
        MailboxHolderIdentity holder,
        SessionIdentityProvider identity,
        ProductionMailboxOwnerIdentity owner,
        ReadOnlyMemory<byte> expectedOwner,
        MailboxInfrastructureOwnership ownership,
        CancellationToken cancellationToken)
    {
        var bundle = await acquirer.AcquireAsync(
            identity, owner, clientIdentity, cancellationToken).ConfigureAwait(false);
        if (bundle.MailboxOwnerEd25519PublicKey.Length != expectedOwner.Length ||
            !CryptographicOperations.FixedTimeEquals(
                bundle.MailboxOwnerEd25519PublicKey.Span, expectedOwner.Span))
            throw new InvalidDataException(
                "Registry response changed the stable mailbox owner identity.");
        return await ProductionMailboxCredentialBundleImporter.ImportLocalOwnerAsync(
            store,
            holder,
            bundle,
            expectedOwner,
            buildAnchor,
            trustStateStore,
            clientIdentity,
            ownership,
            timeProvider,
            cancellationToken).ConfigureAwait(false);
    }
}
