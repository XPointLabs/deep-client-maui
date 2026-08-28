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
        Assert.DoesNotContain(resources, item =>
            item.Include == "deep.release.env" &&
            item.Condition is not null &&
            item.Condition.Contains("'$(DeepSurvivalRuntimeEnv)' == ''", StringComparison.Ordinal));
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
        Assert.Contains(errors, condition =>
            condition.Contains("'$(DeepPhysicalE2E)' == 'true'", StringComparison.Ordinal) &&
            condition.Contains("'$(DeepSurvivalRuntimeEnv)' == ''", StringComparison.Ordinal));
        Assert.Contains(validationTarget.Elements("Error"), error =>
            ((string?)error.Attribute("Text"))?.Contains(
                "never fall back to the release runtime environment", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void SurvivalEnvironmentMatchesPersistentComposeEndpoints()
    {
        var environment = File.ReadAllText(WorkspacePath("eng", "survival.dev.env"));

        Assert.Contains(
            "XNODE_URLS=4cb5abf6ad79fbf5abbccafcc269d85cd2651ed4b885b5869f241aedf0a5ba29|https://192.168.1.43:41801;" +
            "7422b9887598068e32c4448a949adb290d0f4e35b9e01b0ee5f1a1e600fe2674|https://192.168.1.43:41802;" +
            "f381626e41e7027ea431bfe3009e94bdd25a746beec468948d6c3c7c5dc9a54b|https://192.168.1.43:41803;" +
            "fd50b8e3b144ea244fbf7737f550bc8dd0c2650bbc1aada833ca17ff8dbf329b|https://192.168.1.43:41804;" +
            "fde4fba030ad002f7c2f7d4c331f49d13fb0ec747eceebec634f1ff4cbca9def|https://192.168.1.43:41805;" +
            "b4c92afb3ba57f3ab959ffe6d319c98484a2155a0f4c65b2c37011ffd197b075|https://192.168.1.43:41806",
            environment,
            StringComparison.Ordinal);
        Assert.Contains("DEEP_FILE_URL=https://192.168.1.43:41821", environment, StringComparison.Ordinal);
        Assert.Contains("DEEP_PUSH_URL=https://192.168.1.43:41822", environment, StringComparison.Ordinal);
        Assert.Contains("DEEP_CALL_SIGNALING_BASE_URL=https://192.168.1.43:41823", environment, StringComparison.Ordinal);
        Assert.DoesNotContain("http://192.168.1.43", environment, StringComparison.Ordinal);
        Assert.Contains("SURVIVAL_ENV=Development", environment, StringComparison.Ordinal);
        Assert.Contains("DEEP_TRANSPORT_OWNERSHIP=official-managed", environment, StringComparison.Ordinal);
        Assert.DoesNotContain("DEEP_STORAGE_URL", environment, StringComparison.Ordinal);
    }

    [Fact]
    public void SurvivalRuntimeHasNoLegacyMetadataCompatibilityRelaxation()
    {
        var program = File.ReadAllText(WorkspacePath("src", "Deep.Client.Maui", "MauiProgram.cs"));
        var nativeTransport = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "Services", "StoreBoundNativeMau2Transport.cs"));

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
        Assert.Contains("transportFactory.CreatePrivacyRoutedMailboxIngress(", nativeTransport,
            StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClientMailboxBinaryIngress", nativeTransport,
            StringComparison.Ordinal);
        Assert.False(File.Exists(WorkspacePath(
            "src", "Deep.Client.Maui.Core", "Services", "RoutedProductionCompositionFactory.cs")));
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
        Assert.Contains("function Set-ProtectedHolderOutput", script,
            StringComparison.Ordinal);
        Assert.Contains("[IO.FileInfo]::new($Path).SetAccessControl($acl)", script,
            StringComparison.Ordinal);
        Assert.Contains("Holder output already exists; replace it explicitly outside this command.",
            script, StringComparison.Ordinal);
        Assert.Contains("Holder output must have Unix mode 0600.", script,
            StringComparison.Ordinal);
        Assert.Contains("selections).Count -ne 0", script, StringComparison.Ordinal);
        Assert.Contains("@($_.replicas).Count -ne 0", script, StringComparison.Ordinal);
        Assert.Contains("Deep.AndroidLab.PolicyVerifier", script, StringComparison.Ordinal);
        Assert.Contains("Mr. X Ed25519 approval signature is invalid", script,
            StringComparison.Ordinal);
        Assert.Contains("privacy-routes.v1.json", script, StringComparison.Ordinal);
        Assert.Contains("signedPolicy.privacyRoutesSha256", script, StringComparison.Ordinal);
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
        var chaosController = File.ReadAllText(WorkspacePath(
            "tests", "Deep.Client.Maui.UiTests", "PhysicalChaosController.cs"));
        var windowsUi = File.ReadAllText(WorkspacePath(
            "tests", "Deep.Client.Maui.UiTests", "WindowsUiSmokeTests.cs"));
        var attachmentOpen = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "Services", "AttachmentOpenService.cs"));
        var mauiProgram = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "MauiProgram.cs"));
        var startConversation = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "Pages", "StartConversationPage.xaml"));

        Assert.Contains("ValidateSet('ProvisionIdentity', 'Attach', 'PayloadMatrix', 'PrivacyFallback', 'Call', 'RestartDurability', 'ManualResendAfterRestart', 'AutomaticRetryAfterRestart', 'AckCrashWindow', 'NegativeRuntime')", runner,
            StringComparison.Ordinal);
        Assert.Contains("DEEP_MAU2_E2E_PHASE", runner, StringComparison.Ordinal);
        Assert.Contains("[switch]$ResetWindowsUatLocalState", runner,
            StringComparison.Ordinal);
        Assert.Contains("[switch]$ResetAndroidE2eLocalState", runner,
            StringComparison.Ordinal);
        Assert.Contains("$Phase -cne 'ProvisionIdentity'", runner,
            StringComparison.Ordinal);
        Assert.Contains("DEEP_MAU2_E2E_UAT_RESET_BINDING", runner,
            StringComparison.Ordinal);
        Assert.Contains("${policySha256}:$releaseInvocationId", runner,
            StringComparison.Ordinal);
        Assert.Contains("$env:DEEP_MAU2_E2E_UAT_RESET_BINDING = $null", runner,
            StringComparison.Ordinal);
        Assert.Contains("DEEP_MAU2_E2E_ANDROID_RESET_BINDING", runner,
            StringComparison.Ordinal);
        Assert.Contains("android-e2e-local-reset-v1:${policySha256}:$releaseInvocationId", runner,
            StringComparison.Ordinal);
        Assert.Contains("$env:DEEP_MAU2_E2E_ANDROID_RESET_BINDING = $null", runner,
            StringComparison.Ordinal);
        Assert.Contains("$env:DEEP_STRICT_WINDOWS_UI = '1'", runner,
            StringComparison.Ordinal);
        Assert.Contains(
            "startInfo.Environment[StrictLaneEnvironment.WindowsUiEnabledKey] = \"1\";",
            windowsUi,
            StringComparison.Ordinal);
        Assert.Contains("application.GetMainWindow(", windowsUi, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAllTopLevelWindows", windowsUi, StringComparison.Ordinal);
        Assert.Contains("private Window CurrentWindow() => window;", windowsUi,
            StringComparison.Ordinal);
        Assert.Contains("'Startup.Status'", runner, StringComparison.Ordinal);
        Assert.Contains("e2e-runs", runner, StringComparison.Ordinal);
        Assert.Contains("Initialize-ProtectedRunsRoot $e2eRunsRoot", runner,
            StringComparison.Ordinal);
        Assert.Contains("Set-ProtectedRunItem $Path", runner,
            StringComparison.Ordinal);
        Assert.Contains("[IO.FileSystemAclExtensions]::SetAccessControl(", runner,
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
        Assert.Contains("[regex]::Escape('\"phase\":\"PayloadMatrix\"')", runner,
            StringComparison.Ordinal);
        Assert.Contains("'\"phase\":\"MatrixPhase\"'", runner,
            StringComparison.Ordinal);
        Assert.Contains("shared-dev-storage-non-replicated", runner, StringComparison.Ordinal);
        Assert.Contains("DEEP_TRANSPORT_PROTOCOL=authenticated-mau2", runner, StringComparison.Ordinal);
        Assert.Contains("DEEP_TRANSPORT_OWNERSHIP=official-managed", runner, StringComparison.Ordinal);
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
        Assert.Contains("'-Action', 'Status'", runner, StringComparison.Ordinal);
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
        Assert.Contains("StartupResetLocalStateButton", ui, StringComparison.Ordinal);
        Assert.Contains("options.AllowAndroidE2eLocalReset", ui, StringComparison.Ordinal);
        Assert.Contains("android.TapExactResourceIdWithExactText(\"android:id/button1\", \"Сбросить\")", ui,
            StringComparison.Ordinal);
        Assert.Contains("Confirmed Android E2E reset did not reach a clean provisioning surface", ui,
            StringComparison.Ordinal);
        Assert.Contains("windows.WaitForAutomationId(\"PrimaryButton\"", ui,
            StringComparison.Ordinal);
        Assert.Contains("Confirmed Windows UAT reset did not reach a clean provisioning surface", ui,
            StringComparison.Ordinal);
        Assert.DoesNotContain("docker compose", runner, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Legacy_destructive_fixture", ui, StringComparison.Ordinal);
        Assert.DoesNotContain("DEEP_ALLOW_LEGACY", ui, StringComparison.Ordinal);
        Assert.Contains("'--logger', 'trx;LogFileName=physical-phase.trx'", runner,
            StringComparison.Ordinal);
        Assert.Contains("Assert-ExactPhysicalTestResult $trxPath", runner,
            StringComparison.Ordinal);
        Assert.Contains("if ($Execute)", runner, StringComparison.Ordinal);
        Assert.Contains("preflight completed; UI test was not executed", runner,
            StringComparison.Ordinal);
        Assert.Contains("$total -ne 1 -or $executed -ne 1 -or $passed -ne 1", runner,
            StringComparison.Ordinal);
        Assert.Contains("FullyQualifiedName=Deep.Client.Maui.UiTests.StrictCrossPlatformUiTests.Physical_android_and_windows_exchange_persist_and_decrypt_an_attachment", runner,
            StringComparison.Ordinal);
        Assert.Contains("case Mau2PhysicalPhase.ProvisionIdentity", ui, StringComparison.Ordinal);
        Assert.Contains("case Mau2PhysicalPhase.Attach", ui, StringComparison.Ordinal);
        Assert.Contains("case Mau2PhysicalPhase.PayloadMatrix", ui, StringComparison.Ordinal);
        Assert.Contains("case Mau2PhysicalPhase.RestartDurability", ui, StringComparison.Ordinal);
        Assert.Contains("case Mau2PhysicalPhase.ManualResendAfterRestart", ui, StringComparison.Ordinal);
        Assert.Contains("case Mau2PhysicalPhase.AutomaticRetryAfterRestart", ui, StringComparison.Ordinal);
        Assert.Contains("case Mau2PhysicalPhase.AckCrashWindow", ui, StringComparison.Ordinal);
        Assert.Contains("case Mau2PhysicalPhase.NegativeRuntime", ui, StringComparison.Ordinal);
        Assert.Contains("android.DismissKeyboard();", ui, StringComparison.Ordinal);
        Assert.Contains("KEYCODE_BACK", ui, StringComparison.Ordinal);
        Assert.Contains("WaitForWindowsSessionId", ui, StringComparison.Ordinal);
        Assert.Contains("^(05|15|25)[0-9a-f]{64}$", ui, StringComparison.Ordinal);
        Assert.Contains("internal void ColdStart()", ui, StringComparison.Ordinal);
        Assert.Contains("DismissStaleDocumentPicker();", ui, StringComparison.Ordinal);
        Assert.Contains("RequireSingleResumedActivityComponent", ui,
            StringComparison.Ordinal);
        Assert.Contains("component.Contains(\"documentsui\"",
            ui, StringComparison.Ordinal);
        Assert.Contains("ForceStop();", ui, StringComparison.Ordinal);
        Assert.Contains("already-running task", ui, StringComparison.Ordinal);
        Assert.Contains("RequireChaosPhase", ui, StringComparison.Ordinal);
        Assert.Contains("PhysicalChaosController.LoadRequired", ui, StringComparison.Ordinal);
        Assert.Contains("post-durable-response-drop", ui, StringComparison.Ordinal);
        Assert.Contains("pre-dispatch-outage", ui, StringComparison.Ordinal);
        Assert.Contains("post-durable-ack-response-drop", ui, StringComparison.Ordinal);
        Assert.Contains("Assert-VerifiedChaosEvidence", runner, StringComparison.Ordinal);
        Assert.Contains("Assert-ChaosOffBaseline", runner, StringComparison.Ordinal);
        Assert.Contains("DEEP_E2E_CHAOS_HTTPS_ORIGIN", runner, StringComparison.Ordinal);
        Assert.Contains("DEEP_E2E_UAT_CA_CERTIFICATE", runner, StringComparison.Ordinal);
        Assert.Contains("primary-ingress-rejected-before-forward", ui, StringComparison.Ordinal);
        Assert.Contains("Uri.UriSchemeHttps", chaosController, StringComparison.Ordinal);
        Assert.Contains("origin.Port != 41801", chaosController, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", chaosController, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("9feb7fa2416025b672748c4e0ee512d5ba571803", runner,
            StringComparison.Ordinal);
        Assert.Contains("9feb7fa2416025b672748c4e0ee512d5ba571803", chaosController,
            StringComparison.Ordinal);
        Assert.Contains("survival-dev-mailbox-negative-runtime.ps1", runner,
            StringComparison.Ordinal);
        Assert.Contains("Canonical live Windows runtime changed", runner,
            StringComparison.Ordinal);
        Assert.Contains("Live Windows mailbox runtime does not match the currently issued runtime", runner,
            StringComparison.Ordinal);
        Assert.Contains("AssertStartupFailClosed", ui, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearE2ePackageData();", ui, StringComparison.Ordinal);
        Assert.Contains("exact installed APK SHA-256 equality", ui, StringComparison.Ordinal);
        Assert.DoesNotContain("details.Replace(\":\", string.Empty", ui,
            StringComparison.Ordinal);
        Assert.Contains("ResolveWindowsDownloadsDirectoryAsync", attachmentOpen,
            StringComparison.Ordinal);
        Assert.Contains("CreationCollisionOption.FailIfExists", attachmentOpen,
            StringComparison.Ordinal);
        Assert.Contains("TryGetItemAsync(folderName)", attachmentOpen,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CreationCollisionOption.OpenIfExists", attachmentOpen,
            StringComparison.Ordinal);
        Assert.Contains("LayoutHandler.Mapper.AppendToMapping", mauiProgram,
            StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetAccessibilityView", mauiProgram,
            StringComparison.Ordinal);
        Assert.Contains("AccessibilityView.Content", mauiProgram,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Button Grid.ColumnSpan=\"2\" AutomationId=\"StartConversation.NewMessage\" SemanticProperties.Description=\"Новое сообщение\"",
            startConversation,
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
        Assert.Contains("privacy-routes.v1.json", script, StringComparison.Ordinal);
        Assert.Contains("signedPolicy.privacyRoutesSha256", script, StringComparison.Ordinal);
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
    public void PhysicalUatTrustFloorIsCompiledAndReleaseUnreachable()
    {
        var project = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "Deep.Client.Maui.csproj"));
        var program = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "MauiProgram.cs"));

        Assert.Contains("GeneratePhysicalLabTrustRoot", project, StringComparison.Ordinal);
        Assert.Contains("DeepMrXPublicKeySha256", project, StringComparison.Ordinal);
        Assert.Contains(
            @"deep-physical\$(TargetFramework)\$(RuntimeIdentifier)\PhysicalLabTrustRoot.g.cs",
            project,
            StringComparison.Ordinal);
        Assert.Contains("RejectPhysicalLabTrustRootOutsidePhysicalDebug", project,
            StringComparison.Ordinal);
        Assert.Contains("DeepPhysicalUatMrXPublicKeySha256", project,
            StringComparison.Ordinal);
        Assert.Contains("RejectPhysicalUatMailboxInputsOutsidePhysicalDebug", project,
            StringComparison.Ordinal);
        Assert.Contains("ProductionMailboxRuntimeCoordinator", program,
            StringComparison.Ordinal);
        Assert.DoesNotContain("MrXPublicKeySha256Env", program, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveRuntimeSetting(\"DEEP_MR_X_PUBLIC_KEY_SHA256\")",
            program, StringComparison.Ordinal);
        Assert.DoesNotContain("IsPhysicalMailboxCredentialStateConflict", program,
            StringComparison.Ordinal);
        Assert.DoesNotContain("runtime.Inbox.SynchronizeAsync", program,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicalLabPolicyIssuerIsCommitBoundAtomicAndZeroizesPrivateMaterial()
    {
        var script = File.ReadAllText(WorkspacePath("eng", "Issue-AndroidLabPolicy.ps1"));
        var validator = File.ReadAllText(WorkspacePath("eng", "Invoke-StrictClientLane.ps1"));
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
        Assert.Contains(
            "$policy.tools.runner.version = Get-ExactToolVersion $stageRunner @('--version') 'runner'",
            script,
            StringComparison.Ordinal);
        Assert.Contains("$process.WaitForExit(15000)", script, StringComparison.Ordinal);
        Assert.Contains("[int]$props['ro.build.version.sdk'] -lt 28", script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[int]$props['ro.build.version.sdk'] -lt 26", script,
            StringComparison.Ordinal);
        Assert.Contains("[int]$policy.device.sdk -lt 28", validator,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[int]$policy.device.sdk -lt 26", validator,
            StringComparison.Ordinal);
        Assert.Contains("PublicKeyAuth.SignDetached", issuer, StringComparison.Ordinal);
        Assert.Contains("PublicKeyAuth.VerifyDetached", issuer, StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.ZeroMemory(privateKey)", issuer,
            StringComparison.Ordinal);
        Assert.Contains("FileOptions.WriteThrough", issuer, StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicalAuthenticatedOnboardingUsesControlPlaneWithoutDevelopmentPairProvisioning()
    {
        var program = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "MauiProgram.cs"));
        var transport = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "Services", "StoreBoundNativeMau2Transport.cs"));
        Assert.Contains("services.GetRequiredService<ProductionMailboxRuntimeCoordinator>()",
            program,
            StringComparison.Ordinal);
        Assert.DoesNotContain("MailboxRuntimeProvisioning.LoadDevelopment(", program,
            StringComparison.Ordinal);
        Assert.Contains("PublicKeyAuth.VerifyDetached", File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "Services", "MailboxRuntimeProvisioning.cs")),
            StringComparison.Ordinal);
        Assert.Contains("IResumableMailboxIdentityAuthenticatedRawTransport", transport,
            StringComparison.Ordinal);
        Assert.Contains("runtime.Transport.TryResumeScopedMailboxBatchAsync(", transport,
            StringComparison.Ordinal);
        Assert.Contains("runtime.Transport.PrepareScopedMailboxLogicalBatchAsync(", transport,
            StringComparison.Ordinal);
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
