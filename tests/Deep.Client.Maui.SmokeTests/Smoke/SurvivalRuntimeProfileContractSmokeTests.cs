using System.Xml.Linq;

namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class SurvivalRuntimeProfileContractSmokeTests
{
    [Fact]
    public void ProjectSelectsOverrideOnlyForNonReleasePhysicalE2eBuilds()
    {
        var project = XDocument.Load(WorkspacePath("src", "Deep.Client.Maui", "Deep.Client.Maui.csproj"));
        var resources = project.Descendants("EmbeddedResource")
            .Where(item => (string?)item.Element("LogicalName") == "deep.release.env")
            .Select(item => new
            {
                Include = (string?)item.Attribute("Include"),
                Condition = (string?)item.Parent?.Attribute("Condition")
            })
            .ToArray();

        Assert.Contains(resources, item =>
            item.Include == "deep.release.env" &&
            item.Condition is not null &&
            item.Condition.Contains("'$(Configuration)' == 'Release'", StringComparison.Ordinal) &&
            item.Condition.Contains("'$(DeepPhysicalE2E)' != 'true'", StringComparison.Ordinal));
        Assert.Contains(resources, item =>
            item.Include == "$(DeepSurvivalRuntimeEnv)" &&
            item.Condition is not null &&
            item.Condition.Contains("'$(Configuration)' != 'Release'", StringComparison.Ordinal) &&
            item.Condition.Contains("'$(DeepPhysicalE2E)' == 'true'", StringComparison.Ordinal));

        var validationTarget = project.Descendants("Target")
            .Single(target => (string?)target.Attribute("Name") == "ValidateDeepSurvivalRuntimeEnvironment");
        var errors = validationTarget.Elements("Error").Select(error => (string?)error.Attribute("Condition") ?? string.Empty).ToArray();
        Assert.Contains(errors, condition => condition.Contains("'$(Configuration)' == 'Release'", StringComparison.Ordinal));
        Assert.Contains(errors, condition => condition.Contains("'$(DeepPhysicalE2E)' != 'true'", StringComparison.Ordinal));
    }

    [Fact]
    public void SurvivalEnvironmentMatchesPersistentComposeEndpoints()
    {
        var environment = File.ReadAllText(WorkspacePath("eng", "survival.dev.env"));

        Assert.Contains(
            "XNODE_URLS=4cb5abf6ad79fbf5abbccafcc269d85cd2651ed4b885b5869f241aedf0a5ba29|http://127.0.0.1:41801;" +
            "7422b9887598068e32c4448a949adb290d0f4e35b9e01b0ee5f1a1e600fe2674|http://127.0.0.1:41802;" +
            "f381626e41e7027ea431bfe3009e94bdd25a746beec468948d6c3c7c5dc9a54b|http://127.0.0.1:41803;" +
            "fd50b8e3b144ea244fbf7737f550bc8dd0c2650bbc1aada833ca17ff8dbf329b|http://127.0.0.1:41804;" +
            "fde4fba030ad002f7c2f7d4c331f49d13fb0ec747eceebec634f1ff4cbca9def|http://127.0.0.1:41805;" +
            "b4c92afb3ba57f3ab959ffe6d319c98484a2155a0f4c65b2c37011ffd197b075|http://127.0.0.1:41806",
            environment,
            StringComparison.Ordinal);
        Assert.Contains("DEEP_FILE_URL=http://127.0.0.1:41821", environment, StringComparison.Ordinal);
        Assert.Contains("DEEP_PUSH_URL=http://127.0.0.1:41822", environment, StringComparison.Ordinal);
        Assert.Contains("DEEP_CALL_SIGNALING_BASE_URL=http://127.0.0.1:41823", environment, StringComparison.Ordinal);
        Assert.Contains("SURVIVAL_ENV=Development", environment, StringComparison.Ordinal);
        Assert.Contains("DEEP_TRANSPORT_OWNERSHIP=user-managed", environment, StringComparison.Ordinal);
        Assert.DoesNotContain("DEEP_STORAGE_URL", environment, StringComparison.Ordinal);
    }

    [Fact]
    public void SurvivalRuntimeHasNoLegacyMetadataCompatibilityRelaxation()
    {
        var program = File.ReadAllText(WorkspacePath("src", "Deep.Client.Maui", "MauiProgram.cs"));
        var factory = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui.Core", "Services", "RoutedProductionCompositionFactory.cs"));

        Assert.DoesNotContain("SessionStorageMetadataMode.LegacyCompatibility", program, StringComparison.Ordinal);
        Assert.Contains("SURVIVAL_ENV", program, StringComparison.Ordinal);
        const string stubRelaxation = "MetadataPrivateTransportRequired = false";
        Assert.Equal(1, program.Split(stubRelaxation, StringSplitOptions.None).Length - 1);
        var relaxationIndex = program.IndexOf(stubRelaxation, StringComparison.Ordinal);
        var bootstrapIndex = program.LastIndexOf(
            "Environment.GetEnvironmentVariable(E2eBootstrapEnv)",
            relaxationIndex,
            StringComparison.Ordinal);
        var debugGuardIndex = program.LastIndexOf("#if DEBUG", relaxationIndex, StringComparison.Ordinal);
        var debugGuardEndIndex = program.IndexOf("#endif", relaxationIndex, StringComparison.Ordinal);
        Assert.True(bootstrapIndex >= 0);
        Assert.True(debugGuardIndex >= 0);
        Assert.True(debugGuardEndIndex > relaxationIndex);
        Assert.InRange(relaxationIndex, bootstrapIndex + 1, debugGuardEndIndex - 1);
        Assert.Contains("RuntimeTransportProtocol.AuthenticatedMau2", program, StringComparison.Ordinal);
        Assert.Contains("StoreBoundNativeMau2Transport", program, StringComparison.Ordinal);
        Assert.Contains("bool survivalDevelopment) => new();", program, StringComparison.Ordinal);
        Assert.Contains("RoutedSessionStorageTransportOptions transportOptions", factory, StringComparison.Ordinal);
        Assert.Contains("OpaqueSessionStorageDependencies? opaqueDependencies", factory, StringComparison.Ordinal);
        Assert.Contains("new RoutedSessionStorageMessageTransport(", factory, StringComparison.Ordinal);
        Assert.Contains("opaqueDependencies);", factory, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidSetupUsesOnlyTheSeparateE2ePackageAndAllComposePorts()
    {
        var script = File.ReadAllText(WorkspacePath("eng", "Invoke-SurvivalDevClient.ps1"));

        Assert.Contains("$androidPackage = 'network.xpoint.deep.e2e'", script, StringComparison.Ordinal);
        Assert.Contains("$productionPackage = 'network.xpoint.deep'", script, StringComparison.Ordinal);
        Assert.Contains("$reversePorts = @(41545) + @(41801..41806) + @(41810..41823)", script, StringComparison.Ordinal);
        Assert.Contains("-p:DeepPhysicalE2E=true", script, StringComparison.Ordinal);
        Assert.Contains("-p:DeepSurvivalRuntimeEnv=", script, StringComparison.Ordinal);
        Assert.Contains("-p:DeepMrXPublicKeySha256=", script, StringComparison.Ordinal);
        Assert.Contains("Get-ApkSignerSha256", script, StringComparison.Ordinal);
        Assert.Contains("Signer #(?<number>[1-9][0-9]*)", script, StringComparison.Ordinal);
        Assert.Contains("$signers.Count -ne 1 -or $signers[0].number -ne 1", script,
            StringComparison.Ordinal);
        Assert.Contains("exactly one canonical signer and no rotation ambiguity", script,
            StringComparison.Ordinal);
        Assert.Contains("Candidate APK signer differs from the installed E2E package signer.",
            script, StringComparison.Ordinal);
        Assert.Contains("DEEP_MR_X_PUBLIC_KEY_SHA256 must be exactly 64 lowercase hexadecimal characters.",
            script, StringComparison.Ordinal);
        Assert.DoesNotContain("uninstall", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AndroidMailboxBootstrapPublishesHolderBoundRuntimeAtomically()
    {
        var script = File.ReadAllText(WorkspacePath(
            "eng", "Invoke-AndroidMailboxBootstrap.ps1"));

        Assert.Contains("network.xpoint.deep.e2e", script, StringComparison.Ordinal);
        Assert.Contains("ValidateSet('ExportHolder', 'PublishRuntime')", script,
            StringComparison.Ordinal);
        Assert.Contains("Copy-ToAppPrivate", script, StringComparison.Ordinal);
        Assert.Contains("RedirectStandardInput = $true", script, StringComparison.Ordinal);
        Assert.Contains("files/.mailbox-runtime-v1.backup", script, StringComparison.Ordinal);
        Assert.Contains("The new mailbox runtime does not belong to the installed Android holder.",
            script, StringComparison.Ordinal);
        Assert.Contains("Test-AppPrivateRuntimeMatchesSource", script, StringComparison.Ordinal);
        Assert.Contains("if ($mode -cne '600')", script, StringComparison.Ordinal);
        Assert.Contains("if ($mode -cne '700')", script, StringComparison.Ordinal);
        Assert.Contains("Published Android mailbox runtime failed its final byte-for-byte reread.",
            script, StringComparison.Ordinal);
        Assert.Contains("selections).Count -ne 0", script, StringComparison.Ordinal);
        Assert.Contains("@($_.replicas).Count -ne 0", script, StringComparison.Ordinal);
        Assert.Contains("Deep.AndroidLab.PolicyVerifier", script, StringComparison.Ordinal);
        Assert.Contains("Mr. X Ed25519 approval signature is invalid", script,
            StringComparison.Ordinal);
        Assert.Contains("Get-RelativeChildPath", script, StringComparison.Ordinal);
        Assert.DoesNotContain("[IO.Path]::GetRelativePath", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/data/local/tmp", script, StringComparison.Ordinal);
        Assert.DoesNotContain("rm -rf files/mailbox-runtime-v1", script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicalMau2RunnerIsPhasedSanitizedAndNeverOwnsProductionOrLiveRuntime()
    {
        var runner = File.ReadAllText(WorkspacePath(
            "eng", "Invoke-PhysicalMau2CrossPlatform.ps1"));
        var ui = File.ReadAllText(WorkspacePath(
            "tests", "Deep.Client.Maui.UiTests", "StrictCrossPlatformUiTests.cs"));

        Assert.Contains("ValidateSet('Attach', 'HappyPath', 'RestartDurability', 'ManualResendAfterRestart', 'AutomaticRetryAfterRestart', 'NegativeRuntime')", runner,
            StringComparison.Ordinal);
        Assert.Contains("DEEP_MAU2_E2E_PHASE", runner, StringComparison.Ordinal);
        Assert.Contains("e2e-runs", runner, StringComparison.Ordinal);
        Assert.Contains("Initialize-ProtectedRunsRoot $e2eRunsRoot", runner,
            StringComparison.Ordinal);
        Assert.Contains("Set-ProtectedRunItem $Path", runner,
            StringComparison.Ordinal);
        Assert.Contains("[IO.DirectoryInfo]::new($Path).SetAccessControl", runner,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Set-Acl -LiteralPath $Path", runner,
            StringComparison.Ordinal);
        var runsRootGuard = runner.IndexOf(
            "Initialize-ProtectedRunsRoot $e2eRunsRoot",
            StringComparison.Ordinal);
        var firstChildWrite = runner.IndexOf(
            "[IO.Directory]::CreateDirectory($runRoot)",
            StringComparison.Ordinal);
        Assert.True(runsRootGuard >= 0 && firstChildWrite > runsRootGuard,
            "The existing e2e-runs anchor must be rejected when it is a junction before any child write.");
        Assert.Contains("Assert-SanitizedState", runner, StringComparison.Ordinal);
        Assert.Contains("shared-dev-storage-non-replicated", runner, StringComparison.Ordinal);
        Assert.Contains("DEEP_TRANSPORT_PROTOCOL=authenticated-mau2", runner, StringComparison.Ordinal);
        Assert.Contains("DEEP_TRANSPORT_OWNERSHIP=user-managed", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("[string]$AdbPath", runner, StringComparison.Ordinal);
        Assert.Contains("$adb = $approvedAdb", runner, StringComparison.Ordinal);
        Assert.Contains("$env:DEEP_E2E_REPOSITORY_ROOT = $repoRoot", runner,
            StringComparison.Ordinal);
        Assert.Contains("git -C $repoRoot status --porcelain=v1 --untracked-files=all", runner,
            StringComparison.Ordinal);
        Assert.Contains("$productionBefore = Get-PackageSnapshot $productionPackage", runner,
            StringComparison.Ordinal);
        Assert.Contains("$productionBefore -cne $productionAfter", runner,
            StringComparison.Ordinal);
        Assert.Contains("-Action Status", runner, StringComparison.Ordinal);
        Assert.Contains("function Test-AbsoluteWindowsPath", runner,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[IO.Path]::IsPathFullyQualified", runner,
            StringComparison.Ordinal);
        Assert.Contains("[Text.UTF8Encoding]::new($false)", runner,
            StringComparison.Ordinal);
        Assert.DoesNotContain("-Encoding utf8NoBOM", runner,
            StringComparison.Ordinal);
        Assert.DoesNotContain("pm clear", runner, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("uninstall", runner, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker compose", runner, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Legacy_destructive_fixture", ui, StringComparison.Ordinal);
        Assert.DoesNotContain("DEEP_ALLOW_LEGACY", ui, StringComparison.Ordinal);
        Assert.Contains("--logger 'trx;LogFileName=physical-phase.trx'", runner,
            StringComparison.Ordinal);
        Assert.Contains("Assert-ExactPhysicalTestResult $trxPath", runner,
            StringComparison.Ordinal);
        Assert.Contains("$total -ne 1 -or $executed -ne 1 -or $passed -ne 1", runner,
            StringComparison.Ordinal);
        Assert.Contains("FullyQualifiedName=Deep.Client.Maui.UiTests.StrictCrossPlatformUiTests.Physical_android_and_windows_exchange_persist_and_decrypt_an_attachment", runner,
            StringComparison.Ordinal);
        Assert.Contains("case Mau2PhysicalPhase.Attach", ui, StringComparison.Ordinal);
        Assert.Contains("case Mau2PhysicalPhase.HappyPath", ui, StringComparison.Ordinal);
        Assert.Contains("case Mau2PhysicalPhase.RestartDurability", ui, StringComparison.Ordinal);
        Assert.Contains("case Mau2PhysicalPhase.ManualResendAfterRestart", ui, StringComparison.Ordinal);
        Assert.Contains("case Mau2PhysicalPhase.AutomaticRetryAfterRestart", ui, StringComparison.Ordinal);
        Assert.Contains("case Mau2PhysicalPhase.NegativeRuntime", ui, StringComparison.Ordinal);
        Assert.Contains("RequireSupportedChaosEvidence", ui, StringComparison.Ordinal);
        Assert.Contains("survival-dev-mailbox-negative-runtime.ps1", runner,
            StringComparison.Ordinal);
        Assert.Contains("Canonical live Windows runtime changed", runner,
            StringComparison.Ordinal);
        Assert.Contains("AssertStartupFailClosed", ui, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearE2ePackageData();", ui, StringComparison.Ordinal);
        Assert.Contains("exact installed APK SHA-256 equality", ui, StringComparison.Ordinal);
        Assert.DoesNotContain("details.Replace(\":\", string.Empty", ui,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsMailboxBootstrapPublishesProtectedHolderBoundRuntimeAtomically()
    {
        var script = File.ReadAllText(WorkspacePath(
            "eng", "Invoke-WindowsMailboxBootstrap.ps1"));
        var provisioning = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "Services", "MailboxRuntimeProvisioning.cs"));
        var acl = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "Services", "WindowsMailboxAccessControl.cs"));

        Assert.Contains("ValidateSet('ExportHolder', 'PublishRuntime')", script,
            StringComparison.Ordinal);
        Assert.Contains("Deep.AndroidLab.PolicyVerifier", script, StringComparison.Ordinal);
        Assert.Contains("--no-build --no-restore", script, StringComparison.Ordinal);
        Assert.Contains("The new mailbox runtime does not belong to the installed Windows holder.", script,
            StringComparison.Ordinal);
        Assert.Contains(".mailbox-runtime-v1.backup", script, StringComparison.Ordinal);
        Assert.Contains("Test-RuntimeMatchesSource", script, StringComparison.Ordinal);
        Assert.Contains("Directories = $directories", script, StringComparison.Ordinal);
        Assert.DoesNotContain("[Text.Json.JsonDocument]", script, StringComparison.Ordinal);
        Assert.Contains("Published Windows mailbox runtime failed its final byte-for-byte reread.",
            script, StringComparison.Ordinal);
        Assert.Contains("[IO.Directory]::Move($stage, $destination)", script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item -LiteralPath $destination", script,
            StringComparison.Ordinal);
        Assert.Contains("Assert-CanonicalMailboxTreeAcl", script,
            StringComparison.Ordinal);
        Assert.Contains("WindowsMailboxAccessControl.ValidateTree(root)", provisioning,
            StringComparison.Ordinal);
        Assert.Contains("S-1-5-18", acl, StringComparison.Ordinal);
        Assert.Contains("S-1-5-32-544", acl, StringComparison.Ordinal);
        Assert.Contains("FileSystemRights.FullControl", acl, StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicalMrXTrustRootIsCompiledAndReleaseUnreachable()
    {
        var project = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "Deep.Client.Maui.csproj"));
        var program = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "MauiProgram.cs"));

        Assert.Contains("GeneratePhysicalLabTrustRoot", project, StringComparison.Ordinal);
        Assert.Contains("DeepMrXPublicKeySha256", project, StringComparison.Ordinal);
        Assert.Contains("RejectPhysicalLabTrustRootOutsidePhysicalDebug", project,
            StringComparison.Ordinal);
        Assert.Contains("PhysicalLabTrustRoot.MrXPublicKeySha256", program,
            StringComparison.Ordinal);
        Assert.DoesNotContain("MrXPublicKeySha256Env", program, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveRuntimeSetting(\"DEEP_MR_X_PUBLIC_KEY_SHA256\")",
            program, StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicalLabPolicyIssuerIsCommitBoundAtomicAndZeroizesPrivateMaterial()
    {
        var script = File.ReadAllText(WorkspacePath("eng", "Issue-AndroidLabPolicy.ps1"));
        var issuer = File.ReadAllText(WorkspacePath(
            "eng", "Deep.AndroidLab.PolicyIssuer", "Program.cs"));

        Assert.Contains("status --porcelain=v1 --untracked-files=all", script,
            StringComparison.Ordinal);
        Assert.Contains(".protected-source.stage", script, StringComparison.Ordinal);
        Assert.Contains(".protected-source.backup", script, StringComparison.Ordinal);
        Assert.Contains("Published Android lab policy source failed its final reread.", script,
            StringComparison.Ordinal);
        Assert.Contains("apksigner verify --print-certs", script, StringComparison.Ordinal);
        Assert.Contains("exactly one signer", script, StringComparison.Ordinal);
        Assert.Contains("$policy.tools.$role.version = Get-ExactToolVersion", script,
            StringComparison.Ordinal);
        Assert.Contains("foreach ($role in @('adb','aapt','apksigner'))", script,
            StringComparison.Ordinal);
        Assert.Contains("$process.WaitForExit(15000)", script, StringComparison.Ordinal);
        Assert.Contains("PublicKeyAuth.SignDetached", issuer, StringComparison.Ordinal);
        Assert.Contains("PublicKeyAuth.VerifyDetached", issuer, StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.ZeroMemory(privateKey)", issuer,
            StringComparison.Ordinal);
        Assert.Contains("FileOptions.WriteThrough", issuer, StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicalMailboxProvisioningEagerlyRejectsPresentRuntimeAndKeepsBootstrapLazy()
    {
        var program = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "MauiProgram.cs"));
        var transport = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "Services", "StoreBoundNativeMau2Transport.cs"));
        var bootstrap = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "Services", "DevelopmentMailboxHolderBootstrap.cs"));

        Assert.Contains("Directory.Exists(runtimeRoot)", program,
            StringComparison.Ordinal);
        Assert.Contains("startupProvisioning ?? MailboxRuntimeProvisioning.LoadDevelopment(", program,
            StringComparison.Ordinal);
        Assert.Contains("VerifyEd25519Detached", File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "Services", "MailboxRuntimeProvisioning.cs")),
            StringComparison.Ordinal);
        var publish = transport.IndexOf(
            "holderAvailable(holder);", StringComparison.Ordinal);
        var load = transport.IndexOf(
            "var options = importOptionsFactory()", StringComparison.Ordinal);
        Assert.True(publish >= 0 && publish < load);
        Assert.Contains("ed25519PublicKey", bootstrap, StringComparison.Ordinal);
        Assert.Contains("sessionId", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain("RecoveryPhrase", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivateKey", bootstrap, StringComparison.Ordinal);
    }

    [Fact]
    public void SurvivalClientScriptResolvesDefaultRuntimeEnvironmentAfterParameterBinding()
    {
        var script = File.ReadAllText(WorkspacePath("eng", "Invoke-SurvivalDevClient.ps1"));

        Assert.DoesNotContain(
            "[string]$RuntimeEnvironmentPath = (Join-Path $PSScriptRoot",
            script,
            StringComparison.Ordinal);
        Assert.Contains("if ([string]::IsNullOrWhiteSpace($RuntimeEnvironmentPath))", script, StringComparison.Ordinal);
        Assert.Contains("$RuntimeEnvironmentPath = Join-Path $PSScriptRoot 'survival.dev.env'", script, StringComparison.Ordinal);
        Assert.Contains("Resolve-CanonicalRuntimeEnvironmentFile", script, StringComparison.Ordinal);
        Assert.Contains("Runtime environment path must not traverse a reparse point.", script, StringComparison.Ordinal);
        Assert.Contains("$runtimeEnvironment = Resolve-CanonicalRuntimeEnvironmentFile -Path $RuntimeEnvironmentPath", script, StringComparison.Ordinal);
    }

    private static string WorkspacePath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deep.Client.Maui.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory!.FullName, .. parts]);
    }
}
