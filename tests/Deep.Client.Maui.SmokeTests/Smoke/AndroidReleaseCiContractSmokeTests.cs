namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class AndroidReleaseCiContractSmokeTests
{
    [Fact]
    public void ReleaseBuildMaterializesAndRemovesFirebaseConfiguration()
    {
        var workflow = ReadWorkspaceFile(".github", "workflows", "ci.yml");

        Assert.Contains("secrets.XPOINT_GOOGLE_SERVICES_JSON_BASE64", workflow, StringComparison.Ordinal);
        Assert.Contains("base64 --decode", workflow, StringComparison.Ordinal);
        Assert.Contains("network.xpoint.deep", workflow, StringComparison.Ordinal);
        Assert.Contains("rm -f src/Deep.Client.Maui/Platforms/Android/google-services.json", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void AppleBuildRunsOnlyWhenExplicitlyDispatched()
    {
        var workflow = ReadWorkspaceFile(".github", "workflows", "ci.yml");

        Assert.Contains("workflow_dispatch:", workflow, StringComparison.Ordinal);
        Assert.Contains("if: github.event_name == 'workflow_dispatch'", workflow, StringComparison.Ordinal);
        Assert.Contains("$requiredJobs += 'apple-build'", workflow, StringComparison.Ordinal);
    }

    private static string ReadWorkspaceFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deep.Client.Maui.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine([directory!.FullName, .. parts]));
    }
}
