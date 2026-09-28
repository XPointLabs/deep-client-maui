namespace Deep.Client.Maui.Clean.Tests;

public sealed class CleanStartupCompositionTests
{
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
        Assert.Contains("CreateDeepIdV2NetworkClosureArtifactSource(origin)",
            admission, StringComparison.Ordinal);
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
        Assert.DoesNotContain("new PrivacyRoutedContactResolverTransport", admission,
            StringComparison.Ordinal);
        // Shape/composition guard only; real signature, freshness and floor
        // behavior is exercised by the Shared native/SQLCipher fixture.
        Assert.Contains("clock, verifier, protectedFloor)", admission,
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
