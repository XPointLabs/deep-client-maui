using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class PhysicalSurvivalHttpCompositionTests
{
    [Fact]
    public void PhysicalLanShape_EagerlyResolvesEveryHttpTransportFromDi()
    {
        var factories = ApplicationHttpTransportComposition.Create(
            new HttpServiceTransportFactory(
                HttpServiceEndpointPolicy.PhysicalE2eDevelopment),
            "http://192.168.1.44:41821/",
            "http://192.168.1.44:41822/",
            "http://192.168.1.44:41823/",
            static () => new HttpClient(),
            static () => new HttpClient());
        var services = new ServiceCollection();
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

        Assert.IsType<HttpAvatarProfileTransport>(
            provider.GetRequiredService<IAvatarProfileTransport>());
        Assert.IsType<HttpAttachmentFileTransport>(
            provider.GetRequiredService<IAttachmentFileTransport>());
        Assert.IsType<HttpPushSubscriptionTransport>(
            provider.GetRequiredService<IPushSubscriptionTransport>());
        Assert.IsType<HttpCallSignalingTransport>(
            provider.GetRequiredService<ICallSignalingTransport>());
    }

    [Fact]
    public void ProductionComposition_CannotResolveLanHttpTransports()
    {
        var factories = ApplicationHttpTransportComposition.Create(
            new HttpServiceTransportFactory(HttpServiceEndpointPolicy.Production),
            "http://192.168.1.44:41821/",
            "http://192.168.1.44:41822/",
            "http://192.168.1.44:41823/",
            static () => new HttpClient(),
            static () => new HttpClient());
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
}
