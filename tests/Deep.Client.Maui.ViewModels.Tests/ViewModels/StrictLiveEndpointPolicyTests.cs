using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.ViewModels.Tests.Services;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.ViewModels.Tests.ViewModels;

public sealed class StrictLiveEndpointPolicyTests
{
    private const string RouterOne = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string RouterTwo = "2222222222222222222222222222222222222222222222222222222222222222";
    private const string RouterThree = "3333333333333333333333333333333333333333333333333333333333333333";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("1 ")]
    public void Resolve_DefaultAndNonExactOptInsKeepProductionPolicy(string? value)
    {
        Assert.Same(RoutedRuntimeEndpointPolicy.Production, StrictLiveEndpointPolicy.Resolve(value));
    }

    [Fact]
    public void Resolve_ExactPhysicalE2eOptInAllowsLanHttpAcrossLiveHarnessEndpoints()
    {
        var routers = string.Join(';',
            $"{RouterOne}|http://192.168.1.44:41801/",
            $"{RouterTwo}|http://192.168.1.44:41802/",
            $"{RouterThree}|http://192.168.1.44:41803/");
        const string serviceUrl = "http://192.168.1.44:41810/api";

        var defaultPolicy = StrictLiveEndpointPolicy.Resolve(null);
        Assert.Throws<InvalidOperationException>(() =>
            RoutedRuntimeConfiguration.ParseAtLeastThree(routers, defaultPolicy));
        Assert.Throws<InvalidOperationException>(() =>
            RoutedRuntimeConfiguration.RequireLiveServiceUrl("DEEP_FILE_URL", serviceUrl, defaultPolicy));

        var physicalPolicy = StrictLiveEndpointPolicy.Resolve("1");
        var endpoints = RoutedRuntimeConfiguration.ParseAtLeastThree(routers, physicalPolicy);
        Assert.Equal(3, endpoints.Count);
        Assert.True(RoutedRuntimeConfiguration.RequireLiveServiceUrl("DEEP_FILE_URL", serviceUrl, physicalPolicy).IsAbsoluteUri);
        Assert.True(RoutedRuntimeConfiguration.RequireLiveServiceUrl("DEEP_PUSH_URL", serviceUrl, physicalPolicy).IsAbsoluteUri);
        Assert.True(RoutedRuntimeConfiguration.RequireLiveServiceUrl("DEEP_CALL_SIGNALING_BASE_URL", serviceUrl, physicalPolicy).IsAbsoluteUri);

        using var composition = RoutedProductionCompositionFactory.Create(
            endpoints,
            directStorageUrl: null,
            new HttpClient(),
            new RoutedSessionStorageTransportOptions(
                MetadataMode: SessionStorageMetadataMode.OpaqueP03),
            OpaqueStorageTestDependencies.Create(),
            endpointPolicy: physicalPolicy);

        Assert.Equal(endpoints, composition.PinnedRouters);
    }

    [Fact]
    public void ResolveHttpServicePolicy_ControlsEveryLiveHttpTransportConstructor()
    {
        const string lanService = "http://192.168.1.44:41821/";
        var routedPolicy = StrictLiveEndpointPolicy.Resolve("1");
        var servicePolicy = StrictLiveEndpointPolicy.ResolveHttpServicePolicy(routedPolicy);

        using var attachment = new HttpAttachmentFileTransport(
            new HttpClient(),
            new HttpAttachmentFileTransportOptions(lanService),
            servicePolicy);
        using var push = new HttpPushSubscriptionTransport(
            new HttpClient(),
            new HttpPushSubscriptionTransportOptions(lanService),
            servicePolicy);
        using var calls = new HttpCallSignalingTransport(
            new HttpClient(),
            new HttpCallSignalingTransportOptions(lanService),
            endpointPolicy: servicePolicy);

        var productionServicePolicy = StrictLiveEndpointPolicy.ResolveHttpServicePolicy(
            StrictLiveEndpointPolicy.Resolve(null));
        Assert.Same(HttpServiceEndpointPolicy.Production, productionServicePolicy);
        Assert.Throws<ArgumentException>(() => new HttpAttachmentFileTransport(
            new HttpClient(),
            new HttpAttachmentFileTransportOptions(lanService),
            productionServicePolicy));
        Assert.Throws<ArgumentException>(() => new HttpPushSubscriptionTransport(
            new HttpClient(),
            new HttpPushSubscriptionTransportOptions(lanService),
            productionServicePolicy));
        Assert.Throws<ArgumentException>(() => new HttpCallSignalingTransport(
            new HttpClient(),
            new HttpCallSignalingTransportOptions(lanService),
            endpointPolicy: productionServicePolicy));
    }
}
