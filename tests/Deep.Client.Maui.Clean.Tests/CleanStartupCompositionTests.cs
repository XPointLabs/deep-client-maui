namespace Deep.Client.Maui.Clean.Tests;

public sealed class CleanStartupCompositionTests
{
    [Fact]
    public void HttpsAndroidDiagnosticHasOnlySystemTrustAndExactPublicCaCrlHostException()
    {
        var config = System.Xml.Linq.XDocument.Parse(ReadSource(Path.Combine("Platforms", "Android", "Resources", "xml", "network_security_config_did2_https.xml")));
        var root = config.Root!;
        Assert.Equal("false", (string?)root.Element("base-config")!.Attribute("cleartextTrafficPermitted"));
        Assert.Equal("system", (string?)Assert.Single(root.Descendants("certificates")).Attribute("src"));
        var domain = Assert.Single(root.Elements("domain-config"));
        Assert.Equal("true", (string?)domain.Attribute("cleartextTrafficPermitted"));
        var host = Assert.Single(domain.Elements("domain"));
        Assert.Equal("false", (string?)host.Attribute("includeSubdomains"));
        Assert.Equal("c.pki.goog", host.Value);
        Assert.Equal(2, root.Elements().Count());
        var project = System.Xml.Linq.XDocument.Parse(ReadSource("Deep.Client.Maui.csproj"));
        var item = Assert.Single(project.Descendants("AndroidResource"), value =>
            (string?)value.Attribute("Include") == "Platforms\\Android\\Resources\\xml\\network_security_config_did2_https.xml");
        var condition = (string?)item.Parent!.Attribute("Condition");
        Assert.Contains("'$(DeepDid2HttpsAdmission)' == 'true'", condition);
        Assert.Contains("'$(DeepPhysicalE2E)' == 'true'", condition);
        Assert.Contains("'$(Configuration)' != 'Release'", condition);
    }

    [Fact]
    public void DefaultClientStartsDid2OwnerWithoutLegacyRuntime()
    {
        var project = ReadSource("Deep.Client.Maui.csproj");
        var startup = ReadSource("MauiProgram.Clean.cs");
        var did2 = ReadSource("MauiProgram.Did2.cs");
        var app = ReadSource("App.Did2.cs");

        Assert.Contains("Deep.Client.Maui.Core.Did2.csproj", project,
            StringComparison.Ordinal);
        Assert.DoesNotContain("<ProjectReference Include=\"..\\Deep.Client.Maui.Core\\Deep.Client.Maui.Core.csproj\"",
            project, StringComparison.Ordinal);
        Assert.Contains("<Compile Remove=\"Services\\**\\*.cs\" />", project,
            StringComparison.Ordinal);
        Assert.Contains("public static MauiApp CreateMauiApp() => CreateDid2MauiApp();",
            startup, StringComparison.Ordinal);
        Assert.Contains("new DeepIdV2AccountRuntimeAccessor(", did2,
            StringComparison.Ordinal);
        Assert.DoesNotContain("DeepAccountRuntimeAccessor", did2,
            StringComparison.Ordinal);
        Assert.Contains("#if DEEP_DID2_ACCOUNT_PROBE", app,
            StringComparison.Ordinal);
        Assert.Contains("Не удаляйте данные приложения", app,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Did2HttpsDiagnosticRequiresPhysicalUatAndCannotEnterRelease()
    {
        var project = ReadSource("Deep.Client.Maui.csproj");
        var startup = ReadSource("MauiProgram.Did2.cs");
        var admission = ReadSource(Path.Combine("Services",
            "DeepIdV2CanaryNetworkAdmission.cs"));

        Assert.Contains("Target Name=\"RejectUnsafeDid2HttpsAdmission\"", project,
            StringComparison.Ordinal);
        Assert.Contains("'$(DeepPhysicalE2E)' != 'true'", project,
            StringComparison.Ordinal);
        Assert.Contains("'$(Configuration)' == 'Release'", project,
            StringComparison.Ordinal);
        Assert.Contains("'$(DeepDid2AccountProbe)' == 'true'", project,
            StringComparison.Ordinal);
        Assert.Contains("#if DEEP_DID2_CANARY_ADMISSION || DEEP_DID2_HTTPS_ADMISSION",
            startup, StringComparison.Ordinal);
        Assert.Contains("uri.Scheme != Uri.UriSchemeHttps || uri.IsLoopback",
            admission, StringComparison.Ordinal);
        Assert.Contains("DeepIdV2DirectoryBootstrapVerifier.RestoreGenesis",
            admission, StringComparison.Ordinal);
        Assert.Contains("CreateDeepIdV2NetworkClosureArtifactSource(origin,",
            admission, StringComparison.Ordinal);
        Assert.Contains("PooledConnectionLifetime: TimeSpan.FromTicks(1)", admission,
            StringComparison.Ordinal);
        Assert.Contains("accounts.OpenNetworkLkgStoreAsync(genesisPin,",
            admission, StringComparison.Ordinal);
        Assert.Contains("networkSource.VerifyCurrentNetworkAsync(",
            admission, StringComparison.Ordinal);
        Assert.Contains("accounts.EnsureOwnInitialPreKeyInventoryAsync(networkSource,",
            admission, StringComparison.Ordinal);
        Assert.Contains("accounts.OpenOwnOnionClientCustodyAsync(cancellationToken)",
            admission, StringComparison.Ordinal);
        Assert.Contains("accounts.PublishOwnStagedPreKeyInventoryAsync(networkSource,",
            admission, StringComparison.Ordinal);
        Assert.Contains("HttpStageAsync(\"AccountProof\"", admission);
        Assert.Contains("HttpStageAsync(\"NetworkVerification\"", admission);
        Assert.Contains("HttpStageAsync(\"PreKeyStaging\"", admission);
        Assert.Contains("HttpStageAsync(\"PreKeyPublication\"", admission);
        Assert.Contains("exception.HttpRequestError", admission);
        Assert.Contains("<Compile Include=\"Services\\Did2TlsFailureClassifier.cs\" />", project,
            StringComparison.Ordinal);
        Assert.Contains("Did2TlsFailureClassifier.Classify(exception)", admission,
            StringComparison.Ordinal);
        Assert.Contains("catch (TimeoutException exception)", admission);
        Assert.Contains("DID2 {stage} failed (Timeout).", admission);
        Assert.DoesNotContain("new PrivacyRoutedContactResolverTransport", admission,
            StringComparison.Ordinal);
        // Shape/composition guard only; real signature, freshness and floor
        // behavior is exercised by the Shared native/SQLCipher fixture.
        Assert.Contains("clock, verifier, protectedFloor, clientOptions: publicClientOptions)", admission,
            StringComparison.Ordinal);
        Assert.Contains("accounts, proof, closure, networkFloor, clock)", admission,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HttpsPhysicalBuildRejectsRetiredTrustInputsAndUsesSeparateWindowsCustody()
    {
        var project = System.Xml.Linq.XDocument.Parse(ReadSource("Deep.Client.Maui.csproj"));
        var legacyGate = project.Root!.Elements("Target").Single(value =>
            (string?)value.Attribute("Name") == "ValidatePhysicalUatMailboxInputs");
        Assert.Contains("'$(DeepDid2HttpsAdmission)' != 'true'",
            (string?)legacyGate.Attribute("Condition"));
        var did2Gate = project.Root.Elements("Target").Single(value =>
            (string?)value.Attribute("Name") == "RejectUnsafeDid2HttpsAdmission");
        Assert.Contains(did2Gate.Elements("Error"), value =>
            ((string?)value.Attribute("Text"))?.Contains("retired mailbox/PMA",
                StringComparison.Ordinal) == true);
        Assert.Contains(did2Gate.Elements("Error"), value =>
            ((string?)value.Attribute("Text"))?.Contains("explicit nonzero network ID",
                StringComparison.Ordinal) == true);
        var paths = ReadSource(Path.Combine("Services", "AppDataPath.cs"));
        Assert.Contains("#if DEEP_DID2_HTTPS_ADMISSION && WINDOWS", paths);
        Assert.Contains("Did2ProbeStorageScope.ResolvePhysicalRoot(", paths);
        Assert.True(paths.IndexOf("RejectReparsePoints(physicalRoot)", StringComparison.Ordinal)
            < paths.IndexOf("Directory.CreateDirectory(physicalRoot)", StringComparison.Ordinal));
        var script = File.ReadAllText(Path.Combine(FindRepository(), "eng",
            "Invoke-Did2HttpsWindowsBuild.ps1"));
        Assert.Contains("$commit -cne $ExpectedCommit", script);
        Assert.Contains("$dirty.Count -ne 0", script);
        Assert.Contains("[switch]$Execute", script);
        Assert.Contains("'-p:DeepLocalDev=false'", script);
        Assert.Contains("'-p:DeepDid2AccountProbe=false'", script);
        Assert.Contains("'-p:WindowsPackageType=None'", script);
        Assert.DoesNotContain("Pma", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Start-Process", script);
        Assert.DoesNotContain("Remove-Item", script);
    }

    [Fact]
    public void Did2HttpsAndroidUsesSeparatePackageAndSystemTrust()
    {
        var project = System.Xml.Linq.XDocument.Parse(ReadSource("Deep.Client.Maui.csproj"));
        var package = project.Descendants("ApplicationId").Single(value =>
            value.Value == "network.xpoint.deep.did2https");
        Assert.Contains("'$(DeepDid2HttpsAdmission)' == 'true'", (string?)package.Attribute("Condition"));
        Assert.Contains("'$(Configuration)' != 'Release'", (string?)package.Attribute("Condition"));
        var physicalCa = project.Root!.Elements("ItemGroup").Single(group =>
            group.Elements("AndroidResource").Any(value =>
                (string?)value.Attribute("Include") == "Platforms\\Android\\Resources\\raw\\deep_physical_uat_ca.crt"));
        Assert.Contains("'$(DeepDid2HttpsAdmission)' != 'true'", (string?)physicalCa.Attribute("Condition"));
        var builder = File.ReadAllText(Path.Combine(FindRepository(), "eng", "Invoke-Did2HttpsAndroidBuild.ps1"));
        Assert.Contains("Invoke-Did2HttpsWindowsBuild.ps1", builder);
        Assert.Contains("'--ks-pass', \"file:$passwordFile\"", builder);
        Assert.Contains("'--ks-type', 'PKCS12'", builder);
        Assert.DoesNotContain("AndroidSigningKeyPass=", builder);
        Assert.DoesNotContain("'--key-pass'", builder);
        Assert.Contains("android-arm64\\network.xpoint.deep.did2https-Signed.apk", builder);
        Assert.Contains("$Matches[1] -cne $ExpectedSignerSha256", builder);
        Assert.DoesNotContain("pm clear", builder);
        var installer = File.ReadAllText(Path.Combine(FindRepository(), "eng", "Invoke-PhysicalDid2AccountProbeAndroid.ps1"));
        Assert.Contains("[ValidateSet('Did2Account', 'Did2Https')]", installer);
        Assert.Contains("'network.xpoint.deep.did2https'", installer);
        Assert.Contains("Assert-SameSnapshot $before[$package] $after[$package] $package", installer);
        Assert.Contains("'Inspect', 'SetName', 'DismissKeyboard', 'CreateAccount', 'Settings', 'ScrollSettings', 'VerifyNetwork', 'Restart'", installer);
        Assert.Contains("$Lane -ne 'Did2Https'", installer);
        Assert.Contains("mCurrentFocus=", installer);
        Assert.Contains("@('shell', 'dumpsys', 'window')", installer);
        Assert.DoesNotContain("@('shell', 'dumpsys', 'window', 'windows')", installer);
        Assert.Contains("$settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit", installer);
        Assert.Contains("$field.Count -ne 1", installer);
        Assert.Contains("$result.uiAfter = (Read-ProbeUi).Summary", installer);
        Assert.Contains("Click-ProbeControl $ui 'Welcome.CreateAccount'", installer);
        Assert.DoesNotContain("Click-ProbeControl $ui 'Welcome.Create'", installer);
        Assert.Contains("merge-base --is-ancestor $apkSourceCommit $commit", installer);
        Assert.Contains("$Phase -eq 'Install'", installer);
        Assert.Contains("$_ -cnotin $harnessOnly", installer);
        Assert.Contains("commit = $apkSourceCommit", installer);
        Assert.Contains("harnessCommit = $commit", installer);
        Assert.Contains("$Ui.ImeShowing -and $Id -cne 'Welcome.DisplayName'", installer);
        Assert.Contains("'DismissKeyboard'", installer);
        Assert.Contains("'Did2Workspace.MobileSettings'", installer);
        Assert.Contains("'ScrollSettings'", installer);
        Assert.Contains("'BeginReset', 'ConfirmReset'", installer);
        Assert.Contains("ConfirmReset requires explicit -ConfirmIsolatedAccountReset.", installer);
        Assert.Contains("$Ui.Summary.resetConfirmationVisible", installer);
        Assert.Contains("'android:id/button1'", installer);
        Assert.Contains("'Удалить тестовый аккаунт'", installer);
        Assert.Contains("Test-OwnedResetDialogFocus $focus $packageOwner", installer);
        Assert.Contains("mOwnerUid=(?<uid>", installer);
        Assert.Contains("$ownedResetDialog -and -not $resetConfirmationVisible", installer);
        Assert.Contains("${probePackage}:id/Page.Settings", installer);
        Assert.Contains("'android.widget.ScrollView'", installer);
        Assert.DoesNotContain("uninstall", installer);
        Assert.DoesNotContain("pm clear", installer);
    }

    [Fact]
    public void WindowsUatPackageCanAdvanceRevisionWithoutChangingProductionVersion()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "eng",
                "Invoke-PhysicalUatWindowsMsix.ps1");
            if (!File.Exists(candidate)) continue;
            var script = File.ReadAllText(candidate);
            Assert.Contains("[int]$PackageRevision", script, StringComparison.Ordinal);
            Assert.Contains("$PSBoundParameters.ContainsKey('PackageRevision')", script,
                StringComparison.Ordinal);
            Assert.Contains("[int]$versionCode", script, StringComparison.Ordinal);
            return;
        }
        throw new FileNotFoundException("Windows UAT build script is unavailable.");
    }

    [Fact]
    public void AndroidReleasePrivacyScreenDoesNotDependOnRetiredSettings()
    {
        var activity = ReadSource(Path.Combine("Platforms", "Android", "MainActivity.cs"));
        Assert.Contains("Window.AddFlags(WindowManagerFlags.Secure);", activity,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ClientSettingKeys.PrivacyScreenSecurity", activity,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Preferences.Default.Get", activity,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidClearsNativeInputFocusBeforeMauiActivityPause()
    {
        var activity = ReadSource(Path.Combine("Platforms", "Android", "MainActivity.cs"));
        var pauseStart = activity.IndexOf("protected override void OnPause()",
            StringComparison.Ordinal);
        var pauseEnd = activity.IndexOf("private void ApplyPrivacyScreenSetting()",
            pauseStart, StringComparison.Ordinal);
        Assert.True(pauseStart >= 0 && pauseEnd > pauseStart);
        var pause = activity[pauseStart..pauseEnd];
        var clearFocus = pause.IndexOf("CurrentFocus?.ClearFocus();",
            StringComparison.Ordinal);
        var basePause = pause.IndexOf("base.OnPause();", StringComparison.Ordinal);
        Assert.True(clearFocus >= 0 && basePause > clearFocus);
    }

    private static string ReadSource(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Deep.Client.Maui", name);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException($"MAUI source {name} is unavailable.");
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "eng",
                    "Invoke-Did2HttpsWindowsBuild.ps1"))) return directory.FullName;
        throw new FileNotFoundException("DID2 Windows build script is unavailable.");
    }
}
