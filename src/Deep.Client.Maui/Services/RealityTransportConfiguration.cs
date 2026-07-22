using System.Net;
using System.Reflection;
using System.Text.Json;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui;

internal static class RealityTransportConfiguration
{
    internal const string BootstrapResource = "deep.bootstrap.json";
    private const int PhysicalE2EFirstLocalPort = 27891;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static RealityBootstrap LoadEmbedded(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        using var stream = assembly.GetManifestResourceStream(BootstrapResource)
            ?? throw new InvalidOperationException(
                $"Embedded Reality bootstrap '{BootstrapResource}' was not found.");
        return Load(stream);
    }

    public static RealityBootstrap Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var bootstrap = JsonSerializer.Deserialize<RealityBootstrap>(stream, JsonOptions)
            ?? throw new InvalidOperationException("Embedded Reality bootstrap is empty.");
        Validate(bootstrap);
        return bootstrap;
    }

    public static IReadOnlyList<PinnedRouterEndpoint> BuildRouterEndpoints(RealityBootstrap bootstrap)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        Validate(bootstrap);
        return bootstrap.Seeds
            .Select(seed => new PinnedRouterEndpoint(
                $"http://127.0.0.1:{seed.LocalPort}",
                seed.RouterId))
            .ToArray();
    }

    public static RealityBootstrap ApplyLocalPortProfile(
        RealityBootstrap bootstrap,
        RealityTransportPortProfile profile)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        Validate(bootstrap);
        if (profile == RealityTransportPortProfile.Default)
        {
            return bootstrap;
        }
        if (profile != RealityTransportPortProfile.PhysicalE2E)
        {
            throw new ArgumentOutOfRangeException(nameof(profile));
        }
        if (bootstrap.Seeds.Count > ushort.MaxValue - PhysicalE2EFirstLocalPort + 1)
        {
            throw new InvalidOperationException("Physical E2E local listener range exceeds the TCP port limit.");
        }

        var remapped = new RealityBootstrap(
            bootstrap.Version,
            bootstrap.Seeds
                .Select((seed, index) => seed with { LocalPort = PhysicalE2EFirstLocalPort + index })
                .ToArray());
        Validate(remapped);
        return remapped;
    }

    public static string BuildXrayConfig(IReadOnlyList<RealitySeed> seeds)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        Validate(new RealityBootstrap(1, seeds));

        var inbounds = seeds.Select((seed, index) => new
        {
            tag = $"xpoint-seed-{index + 1}-in",
            listen = IPAddress.Loopback.ToString(),
            port = seed.LocalPort,
            protocol = "dokodemo-door",
            settings = new { address = IPAddress.Loopback.ToString(), port = 8080, network = "tcp" }
        }).ToArray();

        var outbounds = seeds.Select((seed, index) => new
        {
            tag = $"xpoint-seed-{index + 1}-out",
            protocol = "vless",
            settings = new
            {
                vnext = new[]
                {
                    new
                    {
                        address = seed.OriginIp,
                        port = seed.Port,
                        users = new[] { new { id = seed.ClientId, encryption = "none", flow = seed.Flow } }
                    }
                }
            },
            streamSettings = new
            {
                network = "tcp",
                security = "reality",
                realitySettings = new
                {
                    serverName = seed.ServerName,
                    fingerprint = seed.Fingerprint,
                    password = seed.PublicKey,
                    shortId = seed.ShortId,
                    spiderX = seed.SpiderX
                }
            }
        }).ToArray();

        var rules = seeds.Select((_, index) => new
        {
            type = "field",
            inboundTag = new[] { $"xpoint-seed-{index + 1}-in" },
            outboundTag = $"xpoint-seed-{index + 1}-out"
        }).ToArray();

        return JsonSerializer.Serialize(new
        {
            log = new { loglevel = "warning" },
            inbounds,
            outbounds,
            routing = new { domainStrategy = "AsIs", rules }
        }, JsonOptions);
    }

    public static RealitySeed? FindSeedForRequest(
        IReadOnlyList<RealitySeed>? seeds,
        Uri? requestUri) =>
        seeds?.FirstOrDefault(candidate =>
            RealityStartupCoordinator.TargetsListener(requestUri, candidate.LocalPort));

    private static void Validate(RealityBootstrap bootstrap)
    {
        if (bootstrap.Version != 1 || bootstrap.Seeds is null || bootstrap.Seeds.Count < 3)
        {
            throw new InvalidOperationException(
                "Production Reality bootstrap must contain at least three version-1 seeds.");
        }

        if (bootstrap.Seeds.Select(seed => seed.RouterId).Distinct(StringComparer.Ordinal).Count() != bootstrap.Seeds.Count
            || bootstrap.Seeds.Select(seed => seed.LocalPort).Distinct().Count() != bootstrap.Seeds.Count
            || bootstrap.Seeds.Select(seed => (seed.OriginIp, seed.Port)).Distinct().Count() != bootstrap.Seeds.Count)
        {
            throw new InvalidOperationException(
                "Production Reality bootstrap contains duplicate node identities, origin endpoints, or local ports.");
        }

        foreach (var seed in bootstrap.Seeds)
        {
            if (!IsRouterId(seed.RouterId))
            {
                throw new InvalidOperationException("Production Reality bootstrap contains an invalid router identity.");
            }
            if (!IPAddress.TryParse(seed.OriginIp, out var originIp)
                || IPAddress.IsLoopback(originIp)
                || originIp.Equals(IPAddress.Any)
                || originIp.Equals(IPAddress.IPv6Any))
            {
                throw new InvalidOperationException("Production Reality bootstrap contains an invalid origin IP address.");
            }
            if (!IsPort(seed.Port) || !IsPort(seed.LocalPort))
            {
                throw new InvalidOperationException("Production Reality bootstrap contains an invalid port.");
            }
            if (!Guid.TryParseExact(seed.ClientId, "D", out _)
                || string.IsNullOrWhiteSpace(seed.Flow)
                || Uri.CheckHostName(seed.ServerName) == UriHostNameType.Unknown
                || string.IsNullOrWhiteSpace(seed.PublicKey)
                || string.IsNullOrWhiteSpace(seed.Fingerprint)
                || !IsShortId(seed.ShortId)
                || string.IsNullOrWhiteSpace(seed.SpiderX)
                || !seed.SpiderX.StartsWith("/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Production Reality bootstrap contains invalid Reality credentials.");
            }
        }
    }

    private static bool IsRouterId(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        try
        {
            return Convert.FromHexString(value).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsShortId(string? value)
    {
        if (value is null || value.Length is < 2 or > 16 || value.Length % 2 != 0)
        {
            return false;
        }

        try
        {
            _ = Convert.FromHexString(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsPort(int value) => value is > 0 and <= ushort.MaxValue;
}

internal enum RealityTransportPortProfile
{
    Default = 0,
    PhysicalE2E = 1
}

internal sealed record RealityBootstrap(int Version, IReadOnlyList<RealitySeed> Seeds);

internal sealed record RealitySeed(
    string RouterId,
    string OriginIp,
    int Port,
    string ClientId,
    string Flow,
    string ServerName,
    string PublicKey,
    string ShortId,
    string Fingerprint,
    string SpiderX,
    int LocalPort);
