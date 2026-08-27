using System.Security.Cryptography;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Protocol.DeepExtension.MailboxTopology;

namespace Deep.Client.Maui.Services;

internal sealed record ProductionMailboxLocalOwnerActivation(
    ImportedProductionMailboxRuntimeMaterial Material,
    ProductionMailboxLocalOwnerPublicRoute PublicRoute);

/// <summary>
/// Chooses whether a verified active LocalOwner can be reused or must be refreshed through the
/// authenticated Registry acquisition path. The acquirer owns durable, atomic publication and
/// recovery of ambiguous outcomes; this helper does not mutate or discard existing state first.
/// </summary>
internal static class ProductionMailboxLocalOwnerLifecycle
{
    public static async Task<ProductionMailboxLocalOwnerActivation> ResolveAsync(
        ProductionMailboxActiveBundleLoadResult active,
        ReadOnlyMemory<byte> expectedMailboxOwnerEd25519PublicKey,
        Func<CancellationToken, Task<AcquiredProductionMailboxLocalOwner>> acquire,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(acquire);
        if (expectedMailboxOwnerEd25519PublicKey.Length != 32)
            throw new InvalidDataException(
                "Production mailbox owner identity is invalid.");

        if (active.Status == ProductionMailboxActiveBundleStatus.Valid)
        {
            if (active.Material is null || active.PublicRoute is null)
                throw new InvalidDataException(
                    "Valid production mailbox state is incomplete.");
            ValidateOwner(
                active.PublicRoute.MailboxOwnerEd25519PublicKey.Span,
                expectedMailboxOwnerEd25519PublicKey.Span);
            return new ProductionMailboxLocalOwnerActivation(
                active.Material, active.PublicRoute);
        }

        if (active.Status == ProductionMailboxActiveBundleStatus.Corrupt)
            throw new InvalidDataException(
                "Production mailbox state is corrupt and cannot be refreshed safely.");
        if (active.Status is not (
                ProductionMailboxActiveBundleStatus.Absent or
                ProductionMailboxActiveBundleStatus.RefreshRecommended or
                ProductionMailboxActiveBundleStatus.Expired))
            throw new InvalidDataException(
                "Production mailbox state has an unsupported lifecycle status.");

        // No local state is removed before this call. ImportLocalOwnerAsync publishes the new
        // generation via its durable journal, so a transient failure or process restart leaves
        // either the exact prior generation or a recoverable/exact new generation.
        var acquired = await acquire(cancellationToken).ConfigureAwait(false);
        var advertisement = ProductionMailboxRouteAdvertisementCodec.DecodeAdvertisement(
            acquired.CanonicalRouteAdvertisement.Span);
        ValidateOwner(
            advertisement.Certificate.MailboxOwnerEd25519PublicKey.Span,
            expectedMailboxOwnerEd25519PublicKey.Span);
        if (advertisement.ExpiresAtUnixSeconds == 0)
            throw new InvalidDataException(
                "Authenticated production mailbox route has no expiry.");
        return new ProductionMailboxLocalOwnerActivation(
            acquired.Material,
            new ProductionMailboxLocalOwnerPublicRoute(
                expectedMailboxOwnerEd25519PublicKey.ToArray(),
                acquired.CanonicalRouteAdvertisement.ToArray(),
                advertisement.ExpiresAtUnixSeconds));
    }

    private static void ValidateOwner(
        ReadOnlySpan<byte> actual,
        ReadOnlySpan<byte> expected)
    {
        if (actual.Length != expected.Length ||
            !CryptographicOperations.FixedTimeEquals(actual, expected))
            throw new InvalidDataException(
                "Production mailbox route changed the durable owner identity.");
    }
}

/// <summary>
/// Durable continuity hint for authoring the next owner-signed PRA1 sequence after a long offline
/// period. It is not a trust anchor: Registry replay state and the shared atomic importer still
/// enforce exact replay, route-domain continuity and monotonic publication.
/// </summary>
internal sealed class ProductionMailboxLocalOwnerRouteStateStore(SqliteSessionStore store)
{
    private const string KeyPrefix = "deep.production-mailbox.local-owner-route.v1:";

    public async Task<ProductionMailboxLocalOwnerPublicRoute?> LoadAsync(
        SessionId account,
        CancellationToken cancellationToken = default)
    {
        var encoded = await store.GetAsync<string>(
            KeyPrefix + account.Value, cancellationToken).ConfigureAwait(false);
        if (encoded is null) return null;
        byte[] canonical;
        try
        {
            canonical = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "Persisted production mailbox predecessor route is corrupt.", exception);
        }
        if (canonical.Length !=
            ProductionMailboxRouteAdvertisementConstants.CanonicalAdvertisementLength)
            throw new InvalidDataException(
                "Persisted production mailbox predecessor route has an invalid length.");
        var advertisement = ProductionMailboxRouteAdvertisementCodec.DecodeAdvertisement(
            canonical);
        return new ProductionMailboxLocalOwnerPublicRoute(
            advertisement.Certificate.MailboxOwnerEd25519PublicKey.ToArray(),
            canonical,
            advertisement.ExpiresAtUnixSeconds);
    }

    public Task SaveAsync(
        SessionId account,
        ProductionMailboxLocalOwnerPublicRoute route,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        var canonical = route.CanonicalRouteAdvertisement.ToArray();
        if (canonical.Length !=
            ProductionMailboxRouteAdvertisementConstants.CanonicalAdvertisementLength)
            throw new InvalidDataException(
                "Production mailbox predecessor route has an invalid length.");
        var advertisement = ProductionMailboxRouteAdvertisementCodec.DecodeAdvertisement(
            canonical);
        if (route.MailboxOwnerEd25519PublicKey.Length != 32 ||
            !CryptographicOperations.FixedTimeEquals(
                route.MailboxOwnerEd25519PublicKey.Span,
                advertisement.Certificate.MailboxOwnerEd25519PublicKey.Span) ||
            route.ExpiresAtUnixSeconds != advertisement.ExpiresAtUnixSeconds)
            throw new InvalidDataException(
                "Production mailbox predecessor route metadata is inconsistent.");
        return store.SetAsync(
            KeyPrefix + account.Value,
            Convert.ToBase64String(canonical),
            cancellationToken);
    }
}
