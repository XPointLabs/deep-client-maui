using System.Text;
using System.Text.Json;

namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class RealityTransportConfigurationTests
{
    [Fact]
    public void PhysicalE2EProfileRemapsRoutesAndXrayListenersTogether()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(CreateBootstrapJson()));
        var bootstrap = RealityTransportConfiguration.Load(stream);

        var remapped = RealityTransportConfiguration.ApplyLocalPortProfile(
            bootstrap,
            RealityTransportPortProfile.PhysicalE2E);
        var routes = RealityTransportConfiguration.BuildRouterEndpoints(remapped);
        using var config = JsonDocument.Parse(RealityTransportConfiguration.BuildXrayConfig(remapped.Seeds));

        Assert.Equal([27891, 27892, 27893], remapped.Seeds.Select(seed => seed.LocalPort));
        Assert.Equal(
            ["http://127.0.0.1:27891", "http://127.0.0.1:27892", "http://127.0.0.1:27893"],
            routes.Select(route => route.BaseUrl));
        Assert.Equal(
            [27891, 27892, 27893],
            config.RootElement.GetProperty("inbounds")
                .EnumerateArray()
                .Select(inbound => inbound.GetProperty("port").GetInt32()));
    }

    [Fact]
    public void DefaultProfilePreservesProductionBootstrapPorts()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(CreateBootstrapJson()));
        var bootstrap = RealityTransportConfiguration.Load(stream);

        var configured = RealityTransportConfiguration.ApplyLocalPortProfile(
            bootstrap,
            RealityTransportPortProfile.Default);

        Assert.Same(bootstrap, configured);
        Assert.Equal([17891, 17892, 17893], configured.Seeds.Select(seed => seed.LocalPort));
    }

    [Fact]
    public void ValidBootstrapBuildsThreePinnedLoopbackRoutesAndRealityOutbounds()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(CreateBootstrapJson()));

        var bootstrap = RealityTransportConfiguration.Load(stream);
        var routes = RealityTransportConfiguration.BuildRouterEndpoints(bootstrap);
        var config = RealityTransportConfiguration.BuildXrayConfig(bootstrap.Seeds);

        Assert.Equal(3, routes.Count);
        Assert.Collection(
            routes,
            route => Assert.Equal("http://127.0.0.1:17891", route.BaseUrl),
            route => Assert.Equal("http://127.0.0.1:17892", route.BaseUrl),
            route => Assert.Equal("http://127.0.0.1:17893", route.BaseUrl));

        using var document = JsonDocument.Parse(config);
        var root = document.RootElement;
        Assert.All(root.GetProperty("inbounds").EnumerateArray(), inbound =>
        {
            Assert.Equal("127.0.0.1", inbound.GetProperty("listen").GetString());
            Assert.Equal("dokodemo-door", inbound.GetProperty("protocol").GetString());
        });
        Assert.All(root.GetProperty("outbounds").EnumerateArray(), outbound =>
        {
            Assert.Equal("vless", outbound.GetProperty("protocol").GetString());
            Assert.Equal(
                "reality",
                outbound.GetProperty("streamSettings").GetProperty("security").GetString());
        });
        Assert.Equal(3, root.GetProperty("routing").GetProperty("rules").GetArrayLength());
    }

    [Fact]
    public void BootstrapRejectsDuplicateLocalListener()
    {
        using var document = JsonDocument.Parse(CreateBootstrapJson());
        var seeds = document.RootElement.GetProperty("seeds")
            .EnumerateArray()
            .Select((seed, index) => new
            {
                routerId = seed.GetProperty("routerId").GetString(),
                originIp = seed.GetProperty("originIp").GetString(),
                port = seed.GetProperty("port").GetInt32(),
                clientId = seed.GetProperty("clientId").GetString(),
                flow = seed.GetProperty("flow").GetString(),
                serverName = seed.GetProperty("serverName").GetString(),
                publicKey = seed.GetProperty("publicKey").GetString(),
                shortId = seed.GetProperty("shortId").GetString(),
                fingerprint = seed.GetProperty("fingerprint").GetString(),
                spiderX = seed.GetProperty("spiderX").GetString(),
                localPort = index == 1 ? 17891 : seed.GetProperty("localPort").GetInt32()
            })
            .ToArray();
        var json = JsonSerializer.Serialize(new { version = 1, seeds });
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            RealityTransportConfiguration.Load(stream));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateBootstrapJson() => JsonSerializer.Serialize(new
    {
        version = 1,
        seeds = Enumerable.Range(1, 3).Select(index => new
        {
            routerId = new string((char)('0' + index), 64),
            originIp = $"203.0.113.{index}",
            port = 443,
            clientId = $"00000000-0000-4000-8000-00000000000{index}",
            flow = "xtls-rprx-vision",
            serverName = $"seed{index}.example.com",
            publicKey = $"public-key-{index}",
            shortId = $"0123456789abcde{index}",
            fingerprint = "chrome",
            spiderX = "/",
            localPort = 17890 + index
        })
    });
}
