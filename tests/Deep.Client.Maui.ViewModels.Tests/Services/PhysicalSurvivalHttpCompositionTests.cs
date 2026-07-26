using System.Collections.Concurrent;
using System.Net;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Domain;
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
    public async Task PhysicalLanShape_ExercisesEveryMauiComposedTransportThroughOwnedNetworkHooks()
    {
        var fileDestinations = new ConcurrentQueue<DnsEndPoint>();
        var serviceDestinations = new ConcurrentQueue<DnsEndPoint>();
        var clientOptions = new HttpServiceClientOptions(
            Timeout: TimeSpan.FromSeconds(2));
        var factories = ApplicationHttpTransportComposition.CreateBoundNetwork(
            new HttpServiceTransportFactory(
                HttpServiceEndpointPolicy.PhysicalE2eDevelopment),
            CreateProbeHooks(fileDestinations),
            CreateProbeHooks(serviceDestinations),
            "http://192.168.1.44:41821/",
            "http://192.168.1.44:41822/",
            "http://192.168.1.44:41823/",
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
        var calls = Assert.IsType<HttpCallSignalingTransport>(
            provider.GetRequiredService<ICallSignalingTransport>());
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
        await Assert.ThrowsAsync<HttpRequestException>(
            () => calls.ReceiveAsync(sessionId));

        var envelope = CreateEnvelope(sessionId, SessionId.CreateNew());
        using (var sessionProvider = BuildDirectSessionBranch(factories))
        {
            var session = sessionProvider.GetRequiredService<HttpSessionTransport>();
            await Assert.ThrowsAsync<HttpRequestException>(
                () => session.SendAsync(envelope));
        }
        using (var storageProvider = BuildDirectStorageBranch(factories))
        {
            var storage = storageProvider
                .GetRequiredService<SessionStorageMessageTransport>();
            await Assert.ThrowsAsync<HttpRequestException>(
                () => storage.SendAsync(envelope));
        }

        Assert.Equal(2, fileDestinations.Count);
        Assert.All(
            fileDestinations,
            destination =>
            {
                Assert.Equal("192.168.1.44", destination.Host);
                Assert.Equal(41821, destination.Port);
            });
        Assert.Equal(
            [41820, 41820, 41822, 41823],
            serviceDestinations.Select(item => item.Port).Order().ToArray());
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
        using var sessionProvider = BuildDirectSessionBranch(factories);
        using var storageProvider = BuildDirectStorageBranch(factories);
        Assert.Throws<ArgumentException>(
            sessionProvider.GetRequiredService<HttpSessionTransport>);
        Assert.Throws<ArgumentException>(
            storageProvider.GetRequiredService<SessionStorageMessageTransport>);
    }

    private static ServiceProvider BuildDirectSessionBranch(
        ApplicationHttpTransportFactories factories)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => factories.ServiceTransportFactory.CreateSession(
            new HttpSessionTransportOptions("http://192.168.1.44:41820/"),
            factories.ServiceClientOptions));
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private static ServiceProvider BuildDirectStorageBranch(
        ApplicationHttpTransportFactories factories)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => factories.ServiceTransportFactory.CreateStorage(
            new SessionStorageMessageTransportOptions(
                "http://192.168.1.44:41820/",
                MetadataMode: SessionStorageMetadataMode.LegacyCompatibility),
            clientOptions: factories.ServiceClientOptions));
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true });
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

    private static OutboundMessageEnvelope CreateEnvelope(
        SessionId sender,
        SessionId recipient) =>
        new(
            sender,
            recipient,
            "probe",
            [],
            DateTimeOffset.UtcNow,
            null);

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
