using System.Security.Cryptography;
using Deep.Client.Shared.Persistence.ContactV1;
using Deep.Client.Shared.Services;
using Deep.Protocol.ContactV1;

namespace Deep.Client.Maui.Services;

internal enum ProductionContactPublicationBootstrapState
{
    NotStarted = 0,
    Publishing = 1,
    Published = 2,
    Recovered = 3,
    Failed = 4,
}

internal sealed record ProductionContactPublicationBootstrapResult(
    ProductionContactPublicationBootstrapState State,
    ulong EffectiveExpiresAtUnixSeconds,
    AuthoredGenesisContactReleaseState? PublishedState = null);

/// <summary>
/// Starts network publication only after local account creation/authentication.
/// Account creation itself remains offline and one-click. The confirmed XPU1
/// journal is checked before authoring so a normal application restart never
/// creates a second generation-zero publication.
/// </summary>
public sealed class ProductionContactPublicationBootstrap
{
    private static ReadOnlySpan<byte> AntiSpamPolicyDomain =>
        "Deep/Client/ContactV1/anti-spam/bounded-unsolicited-v1"u8;

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly DeepAccountRuntimeAccessor accounts;
    private readonly AccountOwnedContactRouteAdvertisementAuthor author;
    private readonly IClock clock;
    private ProductionContactPublicationBootstrapResult? completed;
    private ProductionContactPublicationBootstrapState state;

    internal ProductionContactPublicationBootstrap(
        DeepAccountRuntimeAccessor accounts,
        AccountOwnedContactRouteAdvertisementAuthor author,
        IClock clock)
    {
        this.accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        this.author = author ?? throw new ArgumentNullException(nameof(author));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    internal ProductionContactPublicationBootstrapState State => state;

    internal async ValueTask<ProductionContactPublicationBootstrapResult>
        EnsurePublishedAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (completed is not null)
                return completed;

            state = ProductionContactPublicationBootstrapState.Publishing;
            var store = await accounts.GetContactAddressPublicationStoreAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            var existing = await store.ReadLatestConfirmedAsync(cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                var request = Xpu1Codec.Decode(existing.ExactXpu1.Span);
                var now = checked((ulong)clock.UtcNow.ToUnixTimeSeconds());
                if (request.EffectiveExpiresAtUnixSeconds <= now)
                {
                    state = ProductionContactPublicationBootstrapState.Failed;
                    throw new CryptographicException(
                        "The confirmed ContactV1 publication expired; generation renewal is required.");
                }
                if (await accounts.TryOpenCurrentContactUpdateRendezvousAsync(
                        now, cancellationToken).ConfigureAwait(false) is null)
                {
                    state = ProductionContactPublicationBootstrapState.Failed;
                    throw new CryptographicException(
                        "The confirmed ContactV1 publication predates the clean XUR1 authority; local account reset and generation-zero republication are required.");
                }
                completed = new ProductionContactPublicationBootstrapResult(
                    ProductionContactPublicationBootstrapState.Recovered,
                    request.EffectiveExpiresAtUnixSeconds);
                state = completed.State;
                return completed;
            }

            var antiSpamPolicyHash = SHA256.HashData(AntiSpamPolicyDomain);
            try
            {
                var published = await author.PublishGenesisAsync(
                        maximumAcceptedHellos: 1_024,
                        antiSpamPolicyHash,
                        oneTimePreKeyCount: 32,
                        lastResortReuseLimit: 8,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                completed = new ProductionContactPublicationBootstrapResult(
                    ProductionContactPublicationBootstrapState.Published,
                    published.Address.Request.EffectiveExpiresAtUnixSeconds,
                    published);
                state = completed.State;
                return completed;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(antiSpamPolicyHash);
            }
        }
        catch
        {
            if (completed is null)
                state = ProductionContactPublicationBootstrapState.Failed;
            throw;
        }
        finally
        {
            gate.Release();
        }
    }
}
