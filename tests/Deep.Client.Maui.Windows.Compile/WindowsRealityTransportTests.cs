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

    [Fact]
    public void PortCollisionRejectsCandidateAndSelectsAConfirmedReplacement()
    {
        var created = new List<int>();
        var rejected = new List<int>();

        var selected = WindowsRealityTransport.SelectBoundedCandidate(
            attemptLimit: 3,
            createCandidate: attempt =>
            {
                var candidate = 52000 + attempt;
                created.Add(candidate);
                return candidate;
            },
            tryActivate: candidate => candidate == 52001,
            rejectCandidate: rejected.Add,
            failureMessage: "selection failed");

        Assert.Equal(52001, selected);
        Assert.Equal(new[] { 52000, 52001 }, created);
        Assert.Equal(new[] { 52000 }, rejected);
    }

    [Fact]
    public void PublishedCatalogRecoveryRetriesTheSamePortsAndFailsClosed()
    {
        var publishedCandidate = new[] { 52001, 52002, 52003 };
        var attempted = new List<int[]>();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            WindowsRealityTransport.SelectBoundedCandidate(
                attemptLimit: 3,
                createCandidate: _ => publishedCandidate.ToArray(),
                tryActivate: candidate =>
                {
                    attempted.Add(candidate);
                    return false;
                },
                rejectCandidate: static _ => { },
                failureMessage: "published catalog recovery failed closed"));

        Assert.Equal(3, attempted.Count);
        Assert.All(attempted, candidate => Assert.Equal(publishedCandidate, candidate));
        Assert.Contains("failed closed", exception.Message, StringComparison.Ordinal);
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
