using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.ViewModels.Tests.Services;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.ViewModels.Tests.ViewModels;

public sealed class StrictLiveEndpointPolicyTests
{
    private const string RouterOne = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string RouterTwo = "2222222222222222222222222222222222222222222222222222222222222222";
    private const string RouterThree = "3333333333333333333333333333333333333333333333333333333333333333";

    [Fact]
    public void Resolve_AlwaysUsesProductionPolicy()
    {
        Assert.Same(RoutedRuntimeEndpointPolicy.Production, StrictLiveEndpointPolicy.Resolve());
        Assert.Same(HttpServiceEndpointPolicy.Production, StrictLiveEndpointPolicy.ResolveHttpServicePolicy());
    }

    [Fact]
    public void LanHttpCannotBeEnabledAcrossLiveHarnessEndpoints()
    {
        var routers = string.Join(';',
            $"{RouterOne}|http://192.168.1.44:41801/",
            $"{RouterTwo}|http://192.168.1.44:41802/",
            $"{RouterThree}|http://192.168.1.44:41803/");
        const string serviceUrl = "http://192.168.1.44:41810/api";

        var defaultPolicy = StrictLiveEndpointPolicy.Resolve();
        Assert.Throws<InvalidOperationException>(() =>
            RoutedRuntimeConfiguration.ParseAtLeastThree(routers, defaultPolicy));
        Assert.Throws<InvalidOperationException>(() =>
            RoutedRuntimeConfiguration.RequireLiveServiceUrl("DEEP_FILE_URL", serviceUrl, defaultPolicy));

        Assert.Throws<InvalidOperationException>(() =>
            RoutedRuntimeConfiguration.ParseAtLeastThree(routers, StrictLiveEndpointPolicy.Resolve()));
    }

    [Fact]
    public void HttpServicePolicy_RejectsLanHttpAndAcceptsLanHttps()
    {
        const string lanService = "http://192.168.1.44:41821/";
        const string tlsService = "https://192.168.1.44:41821/";
        var servicePolicy = StrictLiveEndpointPolicy.ResolveHttpServicePolicy();

        using var attachment = new HttpAttachmentFileTransport(
            new HttpClient(),
            new HttpAttachmentFileTransportOptions(tlsService),
            servicePolicy);
        using var push = new HttpPushSubscriptionTransport(
            new HttpClient(),
            new HttpPushSubscriptionTransportOptions(tlsService),
            servicePolicy);
        using var calls = new HttpCallSignalingTransport(
            new HttpClient(),
            new HttpCallSignalingTransportOptions(tlsService),
            endpointPolicy: servicePolicy);

        Assert.Throws<ArgumentException>(() => new HttpAttachmentFileTransport(
            new HttpClient(),
            new HttpAttachmentFileTransportOptions(lanService),
            servicePolicy));
        Assert.Throws<ArgumentException>(() => new HttpPushSubscriptionTransport(
            new HttpClient(),
            new HttpPushSubscriptionTransportOptions(lanService),
            servicePolicy));
        Assert.Throws<ArgumentException>(() => new HttpCallSignalingTransport(
            new HttpClient(),
            new HttpCallSignalingTransportOptions(lanService),
            endpointPolicy: servicePolicy));
    }
}
