using System.Collections.Concurrent;
using System.Net;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Features;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Platform;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;
using Microsoft.Extensions.DependencyInjection;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class PhysicalSurvivalHttpCompositionTests
{
    [Fact]
    public async Task PhysicalLanHttpsShape_ExercisesEnabledTransportsAndRejectsLegacyCallSignaling()
    {
        var fileDestinations = new ConcurrentQueue<DnsEndPoint>();
        var serviceDestinations = new ConcurrentQueue<DnsEndPoint>();
        var clientOptions = new HttpServiceClientOptions(
            Timeout: TimeSpan.FromSeconds(2));
        var factories = ApplicationHttpTransportComposition.CreateBoundNetwork(
            new HttpServiceTransportFactory(
                HttpServiceEndpointPolicy.Production),
            CreateProbeHooks(fileDestinations),
            CreateProbeHooks(serviceDestinations),
            "https://192.168.1.44:41821/",
            "https://192.168.1.44:41822/",
            "https://192.168.1.44:41823/",
            clientOptions,
            clientOptions);
        var runtime = new ClientRuntime(
            new InMemorySessionStore(),
            ClientFeatureFlags.Defaults,
            new SystemClock(),
            new StubSessionBackend());
        var account = await runtime.Accounts.RegisterAsync("Physical transport probe");
        var services = new ServiceCollection();
        services.AddSingleton(runtime);
        services.AddSingleton(factories.Avatar);
        services.AddSingleton(factories.Attachment);
        services.AddSingleton(factories.Push);
        services.AddSingleton(factories.Calls);
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });

        var avatar = Assert.IsType<HttpAvatarProfileTransport>(
            provider.GetRequiredService<IAvatarProfileTransport>());
        var attachment = Assert.IsType<HttpAttachmentFileTransport>(
            provider.GetRequiredService<IAttachmentFileTransport>());
        var push = Assert.IsType<HttpPushSubscriptionTransport>(
            provider.GetRequiredService<IPushSubscriptionTransport>());
        var calls = provider.GetRequiredService<ICallSignalingTransport>();
        var sessionId = account.SessionId;

        await Assert.ThrowsAsync<HttpRequestException>(
            () => avatar.TryGetInfoAsync(sessionId));
        await using (var content = new MemoryStream([1, 2, 3]))
        {
            await Assert.ThrowsAsync<HttpRequestException>(
                () => attachment.UploadAsync(
                    new AttachmentFileUpload(
                        "probe.bin",
                        "application/octet-stream",
                        content)));
        }
        await Assert.ThrowsAsync<HttpRequestException>(
            () => push.SubscribeAsync(CreatePushRequest()));
        await Assert.ThrowsAsync<NotSupportedException>(
            () => calls.ReceiveAsync(sessionId));

        Assert.Equal(2, fileDestinations.Count);
        Assert.All(
            fileDestinations,
            destination =>
            {
                Assert.Equal("192.168.1.44", destination.Host);
                Assert.Equal(41821, destination.Port);
            });
        Assert.Equal([41822], serviceDestinations.Select(item => item.Port).ToArray());
        Assert.All(
            serviceDestinations,
            destination => Assert.Equal("192.168.1.44", destination.Host));
    }

    [Fact]
    public void ProductionComposition_CannotResolveTheSameLanHttpBranches()
    {
        var transportFactory = new HttpServiceTransportFactory(
            HttpServiceEndpointPolicy.Production);
        var hooks = CreateProbeHooks(new ConcurrentQueue<DnsEndPoint>());
        var factories = ApplicationHttpTransportComposition.CreateBoundNetwork(
            transportFactory,
            hooks,
            hooks,
            "http://192.168.1.44:41821/",
            "http://192.168.1.44:41822/",
            "http://192.168.1.44:41823/");
        var services = new ServiceCollection();
        services.AddSingleton(factories.Avatar);
        services.AddSingleton(factories.Attachment);
        services.AddSingleton(factories.Push);
        services.AddSingleton(factories.Calls);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<ArgumentException>(
            provider.GetRequiredService<IAvatarProfileTransport>);
        Assert.Throws<ArgumentException>(
            provider.GetRequiredService<IAttachmentFileTransport>);
        Assert.Throws<ArgumentException>(
            provider.GetRequiredService<IPushSubscriptionTransport>);
        Assert.Throws<ArgumentException>(
            provider.GetRequiredService<ICallSignalingTransport>);
    }

    private static HttpServiceNetworkHooks CreateProbeHooks(
        ConcurrentQueue<DnsEndPoint> destinations) =>
        new(
            (context, _) =>
            {
                destinations.Enqueue(context.DnsEndPoint);
                return ValueTask.FromException<Stream>(
                    new HttpRequestException("Physical composition network probe."));
            });

    private static PushSubscriptionRequest CreatePushRequest() =>
        new(
            "05abc",
            "ed25519pub",
            [0],
            true,
            "firebase",
            123,
            "signature",
            new PushSubscriptionServiceInfo("token"),
            "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff");
}
