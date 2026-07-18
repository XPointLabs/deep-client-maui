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
    public void ParseExactlyThree_AcceptsUniqueLowercasePinsAndCanonicalizesUrls()
    {
        var endpoints = RoutedRuntimeConfiguration.ParseExactlyThree(string.Join(';',
            $"{RouterOne}|http://127.0.0.1:29281",
            $"{RouterTwo}|http://localhost:29282",
            $"{RouterThree}|https://router-three.example"));

        Assert.Equal(3, endpoints.Count);
        Assert.Equal([RouterOne, RouterTwo, RouterThree], endpoints.Select(static endpoint => endpoint.ExpectedRouterId));
        Assert.Equal("http://127.0.0.1:29281/", endpoints[0].BaseUrl);
        Assert.Equal("http://localhost:29282/", endpoints[1].BaseUrl);
        Assert.Equal("https://router-three.example/", endpoints[2].BaseUrl);
    }

    [Theory]
    [MemberData(nameof(InvalidRouterSets))]
    public void ParseExactlyThree_RejectsNonExactDuplicateOrUntrustedRouterSets(string value)
    {
        Assert.Throws<InvalidOperationException>(() => RoutedRuntimeConfiguration.ParseExactlyThree(value));
    }

    [Fact]
    public void ValidateExactlyThree_RejectsCanonicalUrlAliases()
    {
        var endpoints = new[]
        {
            new PinnedRouterEndpoint("https://router-one.example", RouterOne),
            new PinnedRouterEndpoint("https://router-one.example/", RouterTwo),
            new PinnedRouterEndpoint("https://router-three.example", RouterThree)
        };

        Assert.Throws<InvalidOperationException>(() =>
            RoutedRuntimeConfiguration.ValidateExactlyThree(endpoints));
    }

    [Fact]
    public void RoutedComposition_RejectsAnyDirectStorageSetting()
    {
        RoutedRuntimeConfiguration.RejectDirectStorageForRoutedComposition(null);
        RoutedRuntimeConfiguration.RejectDirectStorageForRoutedComposition(" ");

        Assert.Throws<InvalidOperationException>(() =>
            RoutedRuntimeConfiguration.RejectDirectStorageForRoutedComposition(
                "http://127.0.0.1:18100"));
    }

    [Theory]
    [InlineData("https://service.example")]
    [InlineData("http://127.0.0.1:18102")]
    [InlineData("http://localhost:18103")]
    public void RequireLiveServiceUrl_AcceptsHttpsOrExplicitLoopbackHttp(string value)
    {
        Assert.True(RoutedRuntimeConfiguration.RequireLiveServiceUrl("TEST_URL", value).IsAbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("http://service.example")]
    [InlineData("ftp://127.0.0.1/service")]
    public void RequireLiveServiceUrl_RejectsMissingRelativeOrCleartextRemoteUrls(string value)
    {
        Assert.Throws<InvalidOperationException>(() =>
            RoutedRuntimeConfiguration.RequireLiveServiceUrl("TEST_URL", value));
    }

    public static TheoryData<string> InvalidRouterSets => new()
    {
        string.Join(';',
            $"{RouterOne}|https://router-one.example",
            $"{RouterTwo}|https://router-two.example"),
        string.Join(';',
            $"{RouterOne}|https://router-one.example",
            $"{RouterTwo}|https://router-two.example",
            $"{RouterThree}|https://router-three.example",
            $"{RouterFour}|https://router-four.example"),
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
}
