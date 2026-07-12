using System.Net;
using System.Net.Sockets;
using Deep.Client.Maui;

namespace Deep.Client.WindowsCompileTests;

public sealed class WindowsRealityTransportTests
{
    [Fact]
    public void ListenerOwnerLookupReturnsTheProcessThatOpenedTheLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0)
        {
            ExclusiveAddressUse = true
        };
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var found = WindowsRealityTransport.TryGetListenerOwnerProcessId(port, out var processId);

            Assert.True(found);
            Assert.Equal(Environment.ProcessId, processId);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void RuntimeSeedsReceiveUniqueRandomizedDynamicPorts()
    {
        var configuredSeeds = Enumerable.Range(0, 3)
            .Select(index => CreateSeed(17000 + index))
            .ToArray();

        var runtimeSeeds = WindowsRealityTransport.CreateRuntimeSeeds(configuredSeeds);

        Assert.Equal(configuredSeeds.Length, runtimeSeeds.Length);
        Assert.Equal(runtimeSeeds.Length, runtimeSeeds.Select(seed => seed.LocalPort).Distinct().Count());
        Assert.All(runtimeSeeds, seed => Assert.InRange(seed.LocalPort, 49152, 65535));
        Assert.DoesNotContain(runtimeSeeds, seed => configuredSeeds.Any(
            configured => configured.LocalPort == seed.LocalPort));
    }

    private static RealitySeed CreateSeed(int localPort) => new(
        RouterId: new string('a', 64),
        OriginIp: "203.0.113.1",
        Port: 443,
        ClientId: "00000000-0000-4000-8000-000000000001",
        Flow: "xtls-rprx-vision",
        ServerName: "seed.example.com",
        PublicKey: "public-key",
        ShortId: "0123456789abcdef",
        Fingerprint: "chrome",
        SpiderX: "/",
        LocalPort: localPort);
}
