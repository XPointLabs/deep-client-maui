using Deep.Client.Maui.Core.Navigation;

namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class RouteCatalogSmokeTests
{
    [Fact]
    public void ShellRoutesCoverRequiredFirstScreens()
    {
        var routes = ShellRouteCatalog.Routes.Select(item => item.Route).ToHashSet(StringComparer.Ordinal);

        Assert.Contains(ShellRouteCatalog.Onboarding, routes);
        Assert.Contains(ShellRouteCatalog.Conversations, routes);
        Assert.Contains(ShellRouteCatalog.Chat, routes);
        Assert.Contains(ShellRouteCatalog.GroupChat, routes);
        Assert.Contains(ShellRouteCatalog.Groups, routes);
        Assert.Contains(ShellRouteCatalog.SettingsDetail, routes);
    }
}
