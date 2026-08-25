using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class RoutedRuntimeConfigurationTests
{
    private const string RouterOne = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string RouterTwo = "2222222222222222222222222222222222222222222222222222222222222222";
    private const string RouterThree = "3333333333333333333333333333333333333333333333333333333333333333";
    private const string RouterFour = "4444444444444444444444444444444444444444444444444444444444444444";
    private const string RouterAlpha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void ParseAtLeastThree_AcceptsRedundantUniqueLowercasePinsAndCanonicalizesUrls()
    {
        var endpoints = RoutedRuntimeConfiguration.ParseAtLeastThree(string.Join(';',
            $"{RouterOne}|http://127.0.0.1:29281",
            $"{RouterTwo}|http://[::1]:29282",
            $"{RouterThree}|https://router-three.example",
            $"{RouterFour}|https://router-four.example"));

        Assert.Equal(4, endpoints.Count);
        Assert.Equal([RouterOne, RouterTwo, RouterThree, RouterFour], endpoints.Select(static endpoint => endpoint.ExpectedRouterId));
        Assert.Equal("http://127.0.0.1:29281/", endpoints[0].BaseUrl);
        Assert.Equal("http://[::1]:29282/", endpoints[1].BaseUrl);
        Assert.Equal("https://router-three.example/", endpoints[2].BaseUrl);
    }

    [Theory]
    [MemberData(nameof(InvalidRouterSets))]
    public void ParseAtLeastThree_RejectsTooSmallDuplicateOrUntrustedRouterSets(string value)
    {
        Assert.Throws<InvalidOperationException>(() => RoutedRuntimeConfiguration.ParseAtLeastThree(value));
    }

    [Fact]
    public void ValidateAtLeastThree_RejectsCanonicalUrlAliases()
    {
        var endpoints = new[]
        {
            new RealityRouterEndpoint("https://router-one.example", RouterOne),
            new RealityRouterEndpoint("https://router-one.example/", RouterTwo),
            new RealityRouterEndpoint("https://router-three.example", RouterThree)
        };

        Assert.Throws<InvalidOperationException>(() =>
            RoutedRuntimeConfiguration.ValidateAtLeastThree(endpoints));
    }

    [Theory]
    [InlineData("https://service.example")]
    [InlineData("http://127.0.0.1:18102")]
    public void RequireLiveServiceUrl_AcceptsHttpsOrExplicitLoopbackHttp(string value)
    {
        Assert.True(RoutedRuntimeConfiguration.RequireLiveServiceUrl("TEST_URL", value).IsAbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("http://service.example")]
    [InlineData("http://localhost:18103")]
    [InlineData("ftp://127.0.0.1/service")]
    [InlineData("https://user@service.example")]
    [InlineData("https://service.example/path?query=value")]
    [InlineData("https://service.example/#fragment")]
    public void RequireLiveServiceUrl_RejectsMissingRelativeOrCleartextRemoteUrls(string value)
    {
        Assert.Throws<InvalidOperationException>(() =>
            RoutedRuntimeConfiguration.RequireLiveServiceUrl("TEST_URL", value));
    }

    [Fact]
    public void ProductionPolicy_RejectsLanHttpRoutersAndAcceptsLanHttps()
    {
        var cleartext = string.Join(';',
            $"{RouterOne}|http://192.168.50.10:29281/",
            $"{RouterTwo}|http://10.20.30.40:29282/",
            $"{RouterThree}|http://169.254.10.20:29283/");
        var tls = cleartext.Replace("http://", "https://", StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() =>
            RoutedRuntimeConfiguration.ParseAtLeastThree(cleartext));
        var endpoints = RoutedRuntimeConfiguration.ParseAtLeastThree(tls);

        Assert.Equal(3, endpoints.Count);
        Assert.All(endpoints, endpoint => Assert.StartsWith("https://", endpoint.BaseUrl));
    }

    [Theory]
    [InlineData("https://10.20.30.40:18100/api")]
    [InlineData("https://172.20.30.40:18100/api")]
    [InlineData("https://192.168.50.10:18100/api")]
    [InlineData("https://169.254.10.20:18100/api")]
    public void ProductionPolicy_AcceptsLanHttpsServiceUrls(string value)
    {
        var uri = RoutedRuntimeConfiguration.RequireLiveServiceUrl(
            "TEST_URL",
            value);

        Assert.Equal(value, uri.AbsoluteUri.TrimEnd('/'));
    }

    [Theory]
    [InlineData("http://router.local:18100/api")]
    [InlineData("http://8.8.8.8:18100/api")]
    [InlineData("http://0.0.0.0:18100/api")]
    [InlineData("http://224.0.0.1:18100/api")]
    [InlineData("http://192.168.050.010:18100/api")]
    [InlineData("http://user@192.168.50.10:18100/api")]
    [InlineData("http://192.168.50.10:18100/api?bypass=true")]
    public void ProductionPolicy_RejectsCleartextRemoteUrls(
        string value)
    {
        Assert.Throws<InvalidOperationException>(() =>
            RoutedRuntimeConfiguration.RequireLiveServiceUrl(
                "TEST_URL",
                value));
    }

    public static TheoryData<string> InvalidRouterSets => new()
    {
        string.Join(';',
            $"{RouterOne}|https://router-one.example",
            $"{RouterTwo}|https://router-two.example"),
        string.Join(';',
            $"{RouterOne}|https://router-one.example",
            $"{RouterOne}|https://router-two.example",
            $"{RouterThree}|https://router-three.example"),
        string.Join(';',
            $"{RouterOne}|https://router-one.example",
            $"{RouterTwo}|https://router-one.example",
            $"{RouterThree}|https://router-three.example"),
        string.Join(';',
            $"{RouterAlpha.ToUpperInvariant()}|https://router-one.example",
            $"{RouterTwo}|https://router-two.example",
            $"{RouterThree}|https://router-three.example"),
        string.Join(';',
            $"{RouterOne}|http://router-one.example",
            $"{RouterTwo}|https://router-two.example",
            $"{RouterThree}|https://router-three.example")
    };

    [Fact]
    public void ParseAtLeastThree_RejectsRouterSetsAboveTheConfiguredMaximum()
    {
        var routers = Enumerable.Range(1, RoutedRuntimeConfiguration.MaximumRouterCount + 1)
            .Select(index =>
                $"{index.ToString("x64")}|https://router-{index}.example/");

        Assert.Throws<InvalidOperationException>(() =>
            RoutedRuntimeConfiguration.ParseAtLeastThree(string.Join(';', routers)));
    }

    [Theory]
    [InlineData("https://router-one.example/base")]
    [InlineData("https://user@router-one.example/")]
    [InlineData("https://router-one.example/?query=value")]
    [InlineData("https://router-one.example/#fragment")]
    [InlineData("http://localhost:29281/")]
    public void ParseAtLeastThree_RejectsNonCanonicalRouterBaseUrl(string firstUrl)
    {
        var value = string.Join(';',
            $"{RouterOne}|{firstUrl}",
            $"{RouterTwo}|https://router-two.example/",
            $"{RouterThree}|https://router-three.example/");

        Assert.Throws<InvalidOperationException>(() =>
            RoutedRuntimeConfiguration.ParseAtLeastThree(value));
    }

}
