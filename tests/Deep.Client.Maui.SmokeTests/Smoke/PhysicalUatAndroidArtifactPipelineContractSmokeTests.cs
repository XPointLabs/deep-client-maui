namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class PhysicalUatAndroidArtifactPipelineContractSmokeTests
{
    [Fact]
    public void PipelineResolvesDefaultPathsAfterParameterBindingWithoutExecutingBuild()
    {
        var script = Read("eng", "Invoke-PhysicalUatAndroidBuild.ps1");

        Assert.Contains("[AllowNull()][AllowEmptyString()][string]$DevOpsRoot,", script,
            StringComparison.Ordinal);
        Assert.Contains("[AllowNull()][AllowEmptyString()][string]$RuntimeEnvironmentPath,", script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("$DevOpsRoot = (Join-Path $PSScriptRoot", script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("$RuntimeEnvironmentPath = (Join-Path $PSScriptRoot", script,
            StringComparison.Ordinal);

        const string devOpsDefault = """
            if ([string]::IsNullOrWhiteSpace($DevOpsRoot)) {
                $DevOpsRoot = Join-Path $PSScriptRoot '..\..\deep-devops'
            }
            """;
        const string runtimeDefault = """
            if ([string]::IsNullOrWhiteSpace($RuntimeEnvironmentPath)) {
                $RuntimeEnvironmentPath = Join-Path $PSScriptRoot 'survival.dev.env'
            }
            """;
        Assert.Contains(devOpsDefault, script, StringComparison.Ordinal);
        Assert.Contains(runtimeDefault, script, StringComparison.Ordinal);
        Assert.True(script.IndexOf(devOpsDefault, StringComparison.Ordinal) <
            script.IndexOf("$devops = [IO.Path]::GetFullPath($DevOpsRoot)",
                StringComparison.Ordinal));
        Assert.True(script.IndexOf(runtimeDefault, StringComparison.Ordinal) <
            script.IndexOf("$runtime = Resolve-ExactFile $RuntimeEnvironmentPath",
                StringComparison.Ordinal));
    }

    [Fact]
    public void PipelineBuildsAndValidatesARealAndroidTrustBundleWithoutDeviceAccess()
    {
        var script = Read("eng", "Invoke-PhysicalUatAndroidBuild.ps1");
        var launcher = Read("eng", "Invoke-SurvivalDevClient.ps1");

        Assert.Contains("DeepPhysicalUatAndroidTransparencyPreparation=true", script,
            StringComparison.Ordinal);
        Assert.Contains("sign-uat-seed", script, StringComparison.Ordinal);
        Assert.Contains("Deep.AndroidTransparency.UatSigner", script, StringComparison.Ordinal);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(script,
            @"\$uatTransparencySigner, '-c', 'Release', '--',\s*'sign-uat-seed'",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant).Count);
        Assert.Contains("duplicate-password-source", script, StringComparison.Ordinal);
        Assert.Contains("'explicit candidate APK signing'", script, StringComparison.Ordinal);
        Assert.Contains("$badgingOutput = @(& $aapt dump badging $candidateApk)", script,
            StringComparison.Ordinal);
        Assert.True(script.IndexOf("$candidateBadgingExitCode = $LASTEXITCODE",
                StringComparison.Ordinal) <
            script.IndexOf("$badging = $badgingOutput | Select-Object -First 1",
                StringComparison.Ordinal));
        var finalBadgingCaptureIndex = script.IndexOf(
            "$finalBadgingOutput = @(& $aapt dump badging $finalApk)",
            StringComparison.Ordinal);
        Assert.True(finalBadgingCaptureIndex >= 0);
        Assert.True(script.IndexOf("$finalBadgingExitCode = $LASTEXITCODE",
                StringComparison.Ordinal) > finalBadgingCaptureIndex);
        Assert.True(script.IndexOf("$finalBadging = $finalBadgingOutput | Select-Object -First 1",
                StringComparison.Ordinal) > finalBadgingCaptureIndex);
        Assert.Contains("$apkSigner @('sign'", script, StringComparison.Ordinal);
        Assert.Contains("'explicit final APK signing'", script, StringComparison.Ordinal);
        Assert.Contains("Final APK signer does not match the UAT trust-floor lineage.", script,
            StringComparison.Ordinal);
        Assert.Contains("'replace-act1'", script, StringComparison.Ordinal);
        Assert.Contains("'restart APK zipalign'", script, StringComparison.Ordinal);
        Assert.Contains("'restart final physical UAT ACT1 verification'", script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("restart-predecessor-authority.pma1", script,
            StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(script,
            @"(?m)^& \$bootstrap ",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant).Cast<
                System.Text.RegularExpressions.Match>());
        Assert.Contains("$predecessorGeneration = $serverPredecessorGeneration",
            script, StringComparison.Ordinal);
        Assert.Contains("$expectedServerGeneration = $predecessorGeneration + 1", script,
            StringComparison.Ordinal);
        Assert.True(script.IndexOf("& $bootstrap ", StringComparison.Ordinal) >
            script.IndexOf("if ($finalSemanticExitCode -ne 0)",
                StringComparison.Ordinal));
        Assert.DoesNotContain("[ulong]", script, StringComparison.Ordinal);
        Assert.Contains("'--force-recreate', '--no-deps'", script, StringComparison.Ordinal);
        Assert.Contains("'production-like UAT monotonic state transition'", script,
            StringComparison.Ordinal);
        Assert.Contains("$passwordLeaseDirectory", script, StringComparison.Ordinal);
        Assert.Contains("dispose-password-source", script, StringComparison.Ordinal);
        Assert.Contains("finally", script, StringComparison.Ordinal);
        Assert.Contains("publish-production-uat-routes", script, StringComparison.Ordinal);
        Assert.Contains("PreviousTrustFloorBundle", script, StringComparison.Ordinal);
        Assert.Contains("-RequireAndroid", script, StringComparison.Ordinal);
        Assert.Contains("Successor UAT trust floor does not bind one exact generation and ACT1/package/signer tuple.",
            script, StringComparison.Ordinal);
        Assert.Contains("'verify'", script, StringComparison.Ordinal);
        Assert.Contains("-BuildOnly -NoInstall", script, StringComparison.Ordinal);
        Assert.Contains("BuildOnly requires Target Android, NoInstall, and an enabled build.",
            launcher, StringComparison.Ordinal);
        Assert.Contains("Invoke-ExplicitApkSigning", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("dummy", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("'install', '-r'", script, StringComparison.OrdinalIgnoreCase);

        var tool = Read("eng", "tools", "Deep.AndroidTransparency.Tool", "Program.cs");
        var uatSigner = Read("eng", "tools", "Deep.AndroidTransparency.UatSigner", "Program.cs");
        Assert.Contains("APK must contain exactly one canonical ACT1 asset.", tool,
            StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.FixedTimeEquals(frozen, manifest)", tool,
            StringComparison.Ordinal);
        Assert.DoesNotContain("sign-uat-seed", tool, StringComparison.Ordinal);
        Assert.DoesNotContain("SignDetached", tool, StringComparison.Ordinal);
        Assert.Contains("sign-uat-seed", uatSigner, StringComparison.Ordinal);
        Assert.Contains("PublicKeyAuth.SignDetached", uatSigner, StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.ZeroMemory", uatSigner,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PipelineSeparatesClientFloorFromServerSuccessorBootstrap()
    {
        var script = Read("eng", "Invoke-PhysicalUatAndroidBuild.ps1");

        Assert.Contains("[AllowNull()][AllowEmptyString()][string]$ClientTrustFloorBundle,",
            script, StringComparison.Ordinal);
        Assert.Contains("$ClientTrustFloorBundle = $serverPredecessorTrustSource", script,
            StringComparison.Ordinal);
        Assert.Contains("Resolve-ExactFile $ClientTrustFloorBundle 'client trust-floor bundle'",
            script, StringComparison.Ordinal);
        Assert.Contains("Copy-Item -LiteralPath $clientTrustFloorSource -Destination " +
            "$clientTrustFloorSourcePath", script, StringComparison.Ordinal);
        Assert.Contains("Import-ProductionTrustBundle -Path $clientTrustFloorSourcePath",
            script, StringComparison.Ordinal);
        Assert.Contains("Import-ProductionTrustBundle -Path $serverPredecessorTrustPath",
            script, StringComparison.Ordinal);

        Assert.Contains("DeepPhysicalUatAuthorityGeneration=" +
            "$($clientFloorTrust.DeepProductionAuthorityGeneration)", script,
            StringComparison.Ordinal);
        Assert.Contains("authorityGeneration = " +
            "$clientFloorTrust.DeepProductionAuthorityGeneration", script,
            StringComparison.Ordinal);
        Assert.Contains("-PreviousTrustFloorBundle $serverPredecessorTrustPath", script,
            StringComparison.Ordinal);
        Assert.Contains("-PreviousAuthorityArtifact $serverPredecessorAuthorityPath", script,
            StringComparison.Ordinal);
        Assert.Contains("$expectedServerGeneration = $predecessorGeneration + 1", script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("$expectedServerGeneration = $clientFloorGeneration + 1", script,
            StringComparison.Ordinal);

        const ulong clientGeneration = 18;
        const ulong serverPredecessorGeneration = 21;
        Assert.True(clientGeneration <= serverPredecessorGeneration);
        Assert.Equal(22UL, serverPredecessorGeneration + 1UL);

        Assert.Contains("clientTrustFloorSource = $clientTrustFloorSourcePath", script,
            StringComparison.Ordinal);
        Assert.Contains("clientTrustFloorGeneration = $clientFloorGeneration", script,
            StringComparison.Ordinal);
        Assert.Contains("serverPredecessorTrustFloor = $serverPredecessorTrustPath", script,
            StringComparison.Ordinal);
        Assert.Contains("serverPredecessorGeneration = $serverPredecessorGeneration", script,
            StringComparison.Ordinal);
        Assert.Contains("serverSuccessorTrustFloor = $serverTrustOutputPath", script,
            StringComparison.Ordinal);
        Assert.Contains("serverSuccessorGeneration = $expectedServerGeneration", script,
            StringComparison.Ordinal);
        Assert.Contains("serverTrustFloor = $serverTrustOutputPath", script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PipelineRejectsForeignFutureAndEqualGenerationForkClientFloors()
    {
        var script = Read("eng", "Invoke-PhysicalUatAndroidBuild.ps1");

        Assert.Contains("DeepProductionMrXPublicKeySha256 -cne", script,
            StringComparison.Ordinal);
        Assert.Contains("DeepProductionNetworkId -cne", script, StringComparison.Ordinal);
        Assert.Contains(
            "Client trust floor and server predecessor belong to different trust domains.",
            script, StringComparison.Ordinal);
        Assert.Contains("$clientFloorGeneration -gt $serverPredecessorGeneration", script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Client trust-floor generation cannot be newer than the server predecessor.",
            script, StringComparison.Ordinal);
        Assert.Contains("$clientFloorGeneration -eq $serverPredecessorGeneration", script,
            StringComparison.Ordinal);
        Assert.Contains("DeepProductionAuthorityHash -cne", script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Client trust floor and server predecessor contain an equal-generation authority fork.",
            script, StringComparison.Ordinal);
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
