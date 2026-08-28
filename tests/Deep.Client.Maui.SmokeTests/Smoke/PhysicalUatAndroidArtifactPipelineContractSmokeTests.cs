namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class PhysicalUatAndroidArtifactPipelineContractSmokeTests
{
    [Fact]
    public void PipelineBuildsAndValidatesARealAndroidTrustBundleWithoutDeviceAccess()
    {
        var script = Read("eng", "Invoke-PhysicalUatAndroidBuild.ps1");
        var launcher = Read("eng", "Invoke-SurvivalDevClient.ps1");

        Assert.Contains("DeepPhysicalUatAndroidTransparencyPreparation=true", script,
            StringComparison.Ordinal);
        Assert.Contains("sign-uat-seed", script, StringComparison.Ordinal);
        Assert.Contains("duplicate-password-source", script, StringComparison.Ordinal);
        Assert.Contains("'explicit candidate APK signing'", script, StringComparison.Ordinal);
        Assert.Contains("$badgingOutput = @(& $aapt dump badging $candidateApk)", script,
            StringComparison.Ordinal);
        Assert.True(script.IndexOf("$candidateBadgingExitCode = $LASTEXITCODE",
                StringComparison.Ordinal) <
            script.IndexOf("$badging = $badgingOutput | Select-Object -First 1",
                StringComparison.Ordinal));
        Assert.Contains("$apkSigner @('sign'", script, StringComparison.Ordinal);
        Assert.Contains("'explicit final APK signing'", script, StringComparison.Ordinal);
        Assert.Contains("Final APK signer does not match the UAT trust-floor lineage.", script,
            StringComparison.Ordinal);
        Assert.Contains("'replace-act1'", script, StringComparison.Ordinal);
        Assert.Contains("'restart APK zipalign'", script, StringComparison.Ordinal);
        Assert.Contains("'restart final physical UAT ACT1 verification'", script,
            StringComparison.Ordinal);
        Assert.Contains("restart-predecessor-authority.pma1", script, StringComparison.Ordinal);
        Assert.Contains("'--force-recreate', '--no-deps'", script, StringComparison.Ordinal);
        Assert.Contains("'production-like UAT monotonic state transition'", script,
            StringComparison.Ordinal);
        Assert.Contains("$passwordLeaseDirectory", script, StringComparison.Ordinal);
        Assert.Contains("dispose-password-source", script, StringComparison.Ordinal);
        Assert.Contains("finally", script, StringComparison.Ordinal);
        Assert.Contains("publish-production-uat-routes", script, StringComparison.Ordinal);
        Assert.Contains("PreviousTrustFloorBundle", script, StringComparison.Ordinal);
        Assert.Contains("-RequireAndroid", script, StringComparison.Ordinal);
        Assert.Contains("Successor UAT trust floor does not bind the exact ACT1/package/signer tuple.",
            script, StringComparison.Ordinal);
        Assert.Contains("'verify'", script, StringComparison.Ordinal);
        Assert.Contains("-BuildOnly -NoInstall", script, StringComparison.Ordinal);
        Assert.Contains("BuildOnly requires Target Android, NoInstall, and an enabled build.",
            launcher, StringComparison.Ordinal);
        Assert.Contains("Invoke-ExplicitApkSigning", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("dummy", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("'install', '-r'", script, StringComparison.OrdinalIgnoreCase);

        var tool = Read("eng", "tools", "Deep.AndroidTransparency.Tool", "Program.cs");
        Assert.Contains("APK must contain exactly one canonical ACT1 asset.", tool,
            StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.FixedTimeEquals(frozen, manifest)", tool,
            StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([WorkspaceRoot(), .. parts]));

    private static string WorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "Deep.Client.Maui.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
