using System.Net;
using System.Security.Cryptography;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Protocol.DeepExtension.SelfHostedProfiles;

namespace Deep.Client.Maui.Core.Services;

public sealed record DevLocalMembershipRouteConfiguration(
    Uri CatalogUrl,
    string ExpectedArtifactSha256)
{
    public static DevLocalMembershipRouteConfiguration? Resolve(
        string? catalogUrl,
        string? expectedArtifactSha256,
        bool explicitDevelopmentProfile,
        bool productionBuild)
    {
        var hasUrl = !string.IsNullOrWhiteSpace(catalogUrl);
        var hasPin = !string.IsNullOrWhiteSpace(expectedArtifactSha256);
        if (!hasUrl && !hasPin)
        {
            return null;
        }
        if (!hasUrl || !hasPin)
        {
            throw new InvalidOperationException(
                "Development membership routing requires both its catalog URL and SHA-256 pin.");
        }
        if (productionBuild)
        {
            throw new InvalidOperationException(
                "Development membership routing is forbidden in production builds.");
        }
        if (!explicitDevelopmentProfile)
        {
            throw new InvalidOperationException(
                "Development membership routing requires the explicit physical-E2E development profile.");
        }
        if (expectedArtifactSha256!.Length != SHA256.HashSizeInBytes * 2 ||
            expectedArtifactSha256.Any(static value =>
                value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new InvalidOperationException(
                "Development membership routing requires a lowercase SHA-256 pin.");
        }
        if (!Uri.TryCreate(catalogUrl, UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment) ||
            !string.Equals(
                parsed.AbsolutePath,
                HttpMembershipRouteArtifactSource.DefaultArtifactPath,
                StringComparison.Ordinal) ||
            !IPAddress.TryParse(parsed.Host, out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            !IsLocalIpv4(address))
        {
            throw new InvalidOperationException(
                "Development membership routing requires an exact local-IPv4 HTTPS catalog URL.");
        }

        return new DevLocalMembershipRouteConfiguration(
            parsed,
            expectedArtifactSha256);
    }

    private static bool IsLocalIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               bytes[0] == 127 ||
               bytes[0] == 169 && bytes[1] == 254 ||
               bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
               bytes[0] == 192 && bytes[1] == 168;
    }
}

public sealed class DeferredVerifiedMembershipRouteCatalogProvider :
    IMembershipRouteCatalogProvider,
    IDisposable
{
    private readonly object sync = new();
    private readonly DevLocalMembershipRouteConfiguration configuration;
    private readonly HttpClient httpClient;
    private readonly IMembershipRouteArtifactCache artifactCache;
    private readonly IClock clock;
    private readonly TimeProvider timeProvider;
    private IMembershipRouteCatalogProvider? provider;
    private bool disposed;

    public DeferredVerifiedMembershipRouteCatalogProvider(
        DevLocalMembershipRouteConfiguration configuration,
        HttpClient httpClient,
        IMembershipRouteArtifactCache artifactCache,
        IClock? clock = null,
        TimeProvider? timeProvider = null)
    {
        this.configuration = configuration ??
            throw new ArgumentNullException(nameof(configuration));
        this.httpClient = httpClient ??
            throw new ArgumentNullException(nameof(httpClient));
        this.artifactCache = artifactCache ??
            throw new ArgumentNullException(nameof(artifactCache));
        this.clock = clock ?? new SystemClock();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsBound
    {
        get
        {
            lock (sync)
            {
                return provider is not null;
            }
        }
    }

    public void Bind(IMembershipTrustRepository trustRepository)
    {
        ArgumentNullException.ThrowIfNull(trustRepository);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (provider is not null)
            {
                throw new InvalidOperationException(
                    "The verified membership provider is already bound.");
            }

            var endpointPolicy = MembershipRouteEndpointPolicy.DevLocalHttps;
            var source = HttpMembershipRouteArtifactSource.FromCatalogUrls(
                httpClient,
                [configuration.CatalogUrl],
                endpointPolicy);
            var trustService = new MembershipTrustService(
                trustRepository,
                new SodiumEd25519MembershipSignatureVerifier(),
                clock,
                MembershipTrustOptions.DormantDefaults with
                {
                    Enabled = true,
                    AllowedClockSkew = TimeSpan.FromSeconds(30),
                    ClockRollbackTolerance = TimeSpan.FromSeconds(30)
                });
            provider = new VerifiedMembershipRouteCatalogProvider(
                trustService,
                trustRepository,
                new DevLocalMembershipTrustBootstrapOptions(
                    configuration.ExpectedArtifactSha256),
                source,
                artifactCache,
                timeProvider,
                endpointPolicy);
        }
    }

    public Task<MembershipRouteCatalogSnapshot> GetCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        IMembershipRouteCatalogProvider current;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            current = provider ??
                throw new MembershipRouteCatalogException(
                    "Verified membership routing is not initialized.");
        }

        return current.GetCatalogAsync(cancellationToken);
    }

    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            provider = null;
        }
    }
}
