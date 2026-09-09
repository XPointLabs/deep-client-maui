using System.Reflection;
using Deep.Client.Maui.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class ProductionMailboxPrivacyRouteBootstrapTests
{
    [Fact]
    public void HostAdaptersAreExplicitlyUnavailable()
    {
        Assert.False(ProductionMailboxPrivacyRouteBootstrap.HostAdaptersAvailable);

        var exception = Assert.Throws<InvalidOperationException>(
            ProductionMailboxPrivacyRouteBootstrap.RequireHostAdapters);

        Assert.Equal(
            ProductionMailboxPrivacyRouteBootstrap.UnavailableCode,
            exception.Message);
    }

    [Fact]
    public void LegacyRawRouteBootstrapHasNoLoadOrParseEntryPoint()
    {
        var methods = typeof(ProductionMailboxPrivacyRouteBootstrap)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(static method => method.Name)
            .ToArray();

        Assert.DoesNotContain("Load", methods);
        Assert.DoesNotContain("ParseCanonical", methods);
        Assert.DoesNotContain("ParseRoute", methods);
    }
}
