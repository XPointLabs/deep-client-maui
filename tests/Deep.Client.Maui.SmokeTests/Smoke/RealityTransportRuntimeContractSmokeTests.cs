namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class RealityTransportRuntimeContractSmokeTests
{
    [Fact]
    public void AndroidPublishesItsImmutableRuntimeCatalogWithoutStartingNativeTransport()
    {
        var source = ReadRepositoryFile(
            "src",
            "Deep.Client.Maui",
            "Services",
            "RealityTransportRuntime.cs");

        Assert.Contains(
            "private readonly IReadOnlyList<RealityRouterEndpoint> routerEndpoints;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "routerEndpoints = RealityTransportConfiguration.BuildRouterEndpoints(bootstrap);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "public IReadOnlyList<RealityRouterEndpoint> RouterEndpoints => routerEndpoints;",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsAndAndroidBothImplementTheSharedRuntimeCatalogContract()
    {
        var contract = ReadRepositoryFile(
            "src",
            "Deep.Client.Maui",
            "Services",
            "IRealityTransportRuntime.cs");
        var android = ReadRepositoryFile(
            "src",
            "Deep.Client.Maui",
            "Services",
            "RealityTransportRuntime.cs");
        var windows = ReadRepositoryFile(
            "src",
            "Deep.Client.Maui",
            "Platforms",
            "Windows",
            "WindowsRealityTransport.cs");

        const string propertyDeclaration =
            "IReadOnlyList<RealityRouterEndpoint> RouterEndpoints";
        Assert.Contains(propertyDeclaration, contract, StringComparison.Ordinal);
        Assert.Contains(propertyDeclaration, android, StringComparison.Ordinal);
        Assert.Contains(propertyDeclaration, windows, StringComparison.Ordinal);
        Assert.Contains(
            "RealityTransportConfiguration.BuildRouterEndpoints",
            android,
            StringComparison.Ordinal);
        Assert.Contains(
            "RealityTransportConfiguration.BuildRouterEndpoints",
            windows,
            StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "Deep.Client.Maui.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine([directory!.FullName, .. parts]));
    }
}
