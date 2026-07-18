namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class RoutedCompositionContractSmokeTests
{
    [Fact]
    public void ReleaseCompositionRequiresExactPinnedRouteAndCompilesDirectStorageOnlyForDebug()
    {
        var program = ReadWorkspaceFile("src", "Deep.Client.Maui", "MauiProgram.cs");

        Assert.Contains(
            "routerBaseUrls.Count != RoutedRuntimeConfiguration.RequiredRouterCount",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "RoutedRuntimeConfiguration.RejectDirectStorageForRoutedComposition(storageBaseUrl);",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "Release composition requires routed XNODE_URLS transport and has no direct storage fallback.",
            program,
            StringComparison.Ordinal);

        var directProvider = program.IndexOf("new DirectStorageRouteProvider(storageBaseUrl)", StringComparison.Ordinal);
        var directTransport = program.IndexOf("new SessionStorageMessageTransport(", StringComparison.Ordinal);
        Assert.True(directProvider >= 0);
        Assert.True(directTransport >= 0);
        Assert.True(program.LastIndexOf("#if DEBUG", directProvider, StringComparison.Ordinal) >= 0);
        Assert.True(program.LastIndexOf("#if DEBUG", directTransport, StringComparison.Ordinal) >= 0);
    }

    [Fact]
    public void StrictLivePreflightRejectsDirectStorageAndNonCanonicalEndpointConfiguration()
    {
        var lane = ReadWorkspaceFile("eng", "Invoke-StrictClientLane.ps1");

        Assert.Contains("'^[0-9a-f]{64}$'", lane, StringComparison.Ordinal);
        Assert.DoesNotContain("'^[0-9a-fA-F]{64}$'", lane, StringComparison.Ordinal);
        Assert.Contains("Test-StrictLiveUrl", lane, StringComparison.Ordinal);
        Assert.Contains("$uri.IsLoopback", lane, StringComparison.Ordinal);
        Assert.Contains("direct-storage-absent", lane, StringComparison.Ordinal);
        Assert.Contains(
            "DEEP_STORAGE_URL is forbidden in routed live evidence",
            lane,
            StringComparison.Ordinal);
        Assert.Contains(
            "@('DEEP_FILE_URL', 'DEEP_PUSH_URL', 'DEEP_CALL_SIGNALING_BASE_URL')",
            lane,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RoutedAcceptanceCapturesRouteShapeAndKeepsDirectDiagnosticOutOfStrictFilter()
    {
        var live = ReadWorkspaceFile(
            "tests",
            "Deep.Client.Maui.ViewModels.Tests",
            "ViewModels",
            "ClientLiveAcceptanceTests.cs");
        var direct = ReadWorkspaceFile(
            "tests",
            "Deep.Client.Maui.ViewModels.Tests",
            "ViewModels",
            "ClientDirectStorageDiagnosticTests.cs");
        var lane = ReadWorkspaceFile("eng", "Invoke-StrictClientLane.ps1");

        Assert.Contains("fixture.Router.CurrentRoute", live, StringComparison.Ordinal);
        Assert.Contains("Assert.Equal(\"onion-storage\", route.Mode);", live, StringComparison.Ordinal);
        Assert.Contains("Assert.Equal([0, 1, 2]", live, StringComparison.Ordinal);
        Assert.Contains("storage_route", live, StringComparison.Ordinal);
        Assert.Contains("onion_request", live, StringComparison.Ordinal);
        Assert.Contains("MakeRouterApisUnavailable", live, StringComparison.Ordinal);
        Assert.Contains("UnexpectedDestinationRequests", live, StringComparison.Ordinal);
        Assert.DoesNotContain("new SessionStorageMessageTransport(", live, StringComparison.Ordinal);
        Assert.Contains("[DirectStorageContractFact]", direct, StringComparison.Ordinal);
        Assert.Contains("ClientLiveAcceptanceTests", lane, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientDirectStorageDiagnosticTests", lane, StringComparison.Ordinal);
    }

    private static string ReadWorkspaceFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "Deep.Client.Maui.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine([directory!.FullName, .. parts]));
    }
}
