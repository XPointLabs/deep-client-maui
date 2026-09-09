using System.Xml.Linq;

namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class PhysicalUatAuthenticatedOnboardingContractSmokeTests
{
    [Fact]
    public void PhysicalDebugUsesProductionCoordinatorWhileOrdinaryDebugKeepsNetworkDormant()
    {
        var program = Read("src", "Deep.Client.Maui", "MauiProgram.cs");
        var onboardingGuard = program.IndexOf(
            "#if DEBUG && !DEEP_PHYSICAL_E2E", StringComparison.Ordinal);
        var sessionOnboarding = program.IndexOf(
            "SessionIdContactMailboxOnboarding", onboardingGuard, StringComparison.Ordinal);
        var productionCoordinator = program.IndexOf(
            "new ProductionMailboxRuntimeCoordinator(", sessionOnboarding,
            StringComparison.Ordinal);

        Assert.True(onboardingGuard >= 0);
        Assert.True(sessionOnboarding > onboardingGuard);
        Assert.True(productionCoordinator > sessionOnboarding);
        Assert.Contains(
            "transportFactory.CreateRequestTransport(",
            Read("src", "Deep.Client.Maui", "Services",
                "ProductionMailboxRuntimeCoordinator.cs"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "new HttpClient",
            Read("src", "Deep.Client.Maui", "Services",
                "ProductionMailboxRuntimeCoordinator.cs"),
            StringComparison.Ordinal);
        var contactPrerequisites = Read(
            "src", "Deep.Client.Maui", "Services",
            "ProductionContactResolveRuntimePrerequisitesSource.cs");
        var contactRuntime = Read(
            "src", "Deep.Client.Maui", "Services",
            "DeepContactResolveRuntimeAccessor.cs");
        Assert.Contains(
            "CreateContactResolveDirectoryArtifactSource(",
            contactPrerequisites,
            StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", contactPrerequisites, StringComparison.Ordinal);
        Assert.DoesNotContain("new HttpContactResolveDirectoryArtifactSource(",
            contactRuntime, StringComparison.Ordinal);
        Assert.Contains(
            "services.GetRequiredService<ProductionMailboxRuntimeCoordinator>()",
            program,
            StringComparison.Ordinal);
        Assert.Contains("new DevelopmentMailboxRuntimeProvisioningSource()",
            program, StringComparison.Ordinal);
        Assert.Contains("Opening the local account and conversation stores must not depend on",
            program, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Authenticated MAU2 in Debug requires an explicit physical UAT build.",
            program,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicalUatTrustInputsAreBuildEmbeddedAndRejectedByRelease()
    {
        var projectPath = Path.Combine(WorkspaceRoot(), "src", "Deep.Client.Maui",
            "Deep.Client.Maui.csproj");
        var projectText = File.ReadAllText(projectPath);
        var project = XDocument.Load(projectPath);
        var physicalMetadata = project.Descendants("ItemGroup")
            .Single(group => ((string?)group.Attribute("Condition"))?.Contains(
                "'$(DeepPhysicalE2E)' == 'true'", StringComparison.Ordinal) == true &&
                group.Elements("AssemblyMetadata").Any());

        Assert.Contains(physicalMetadata.Elements("AssemblyMetadata"), item =>
            (string?)item.Attribute("Include") == "DeepPhysicalUatMrXPublicKeySha256");
        Assert.Contains(physicalMetadata.Elements("AssemblyMetadata"), item =>
            (string?)item.Attribute("Include") == "DeepPhysicalUatTopologyHash");
        Assert.Contains("ValidatePhysicalUatMailboxInputs", projectText,
            StringComparison.Ordinal);
        Assert.Contains("RejectPhysicalUatMailboxInputsOutsidePhysicalDebug", projectText,
            StringComparison.Ordinal);
        Assert.Contains("Physical UAT mailbox inputs are forbidden outside a physical non-Release build.",
            projectText, StringComparison.Ordinal);
        Assert.Contains("DeepPhysicalUatAndroidCodeTransparencyManifest", projectText,
            StringComparison.Ordinal);
        Assert.DoesNotContain("DeepPhysicalUatPrivacyRoutesJson", projectText,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Deep.Client.Maui.production-mailbox-privacy-routes.v2.json",
            projectText, StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicalUatAttestationMeasuresE2ePackageButKeepsProtocolIdentityClosed()
    {
        var trust = Read("src", "Deep.Client.Maui", "Services",
            "ProductionMailboxBuildTrustFloor.cs");
        var attestor = Read("src", "Deep.Client.Maui", "Services",
            "ProductionMailboxClientIdentityAttestor.cs");

        Assert.Contains("DeepPhysicalUatAndroidApplicationId", trust,
            StringComparison.Ordinal);
        Assert.Contains("\"network.xpoint.deep.e2e\"", trust,
            StringComparison.Ordinal);
        Assert.Contains("ProductionMailboxControlPlaneVerifier.AndroidApplicationIdentity",
            trust, StringComparison.Ordinal);
        Assert.Contains("buildIdentity.InstalledApplicationId", attestor,
            StringComparison.Ordinal);
        Assert.Contains("buildIdentity.ApplicationIdentity", attestor,
            StringComparison.Ordinal);
        Assert.DoesNotContain("GetEnvironmentVariable", trust, StringComparison.Ordinal);
        Assert.DoesNotContain("GetEnvironmentVariable", attestor, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsPhysicalUatRequiresInstalledIsolatedMsixAndBuildPinnedSigner()
    {
        var project = Read("src", "Deep.Client.Maui", "Deep.Client.Maui.csproj");
        var trust = Read("src", "Deep.Client.Maui", "Services",
            "ProductionMailboxBuildTrustFloor.cs");
        var attestor = Read("src", "Deep.Client.Maui", "Services",
            "ProductionMailboxClientIdentityAttestor.cs");

        Assert.Contains("DeepPhysicalUatWindowsPackageName", project,
            StringComparison.Ordinal);
        Assert.Contains("DeepPhysicalUatWindowsSigningCertificateSha256", project,
            StringComparison.Ordinal);
        Assert.Contains("network.xpoint.deep.e2e", trust, StringComparison.Ordinal);
        Assert.Contains("VerifyInstalledPhysicalUatWindowsTuple", attestor,
            StringComparison.Ordinal);
        Assert.Contains("unpackaged folder copies are not trusted", attestor,
            StringComparison.Ordinal);
        Assert.Contains("package.Id.FullName", attestor, StringComparison.Ordinal);
        Assert.Contains("package.Id.FamilyName", attestor, StringComparison.Ordinal);
        Assert.Contains("ProductionMailboxControlPlaneVerifier.WindowsApplicationIdentity",
            trust, StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicalLauncherRequiresSignedUatArtifactsAndPassesOnlyUatProperties()
    {
        var script = Read("eng", "Invoke-SurvivalDevClient.ps1");
        var windowsScript = Read("eng", "Invoke-PhysicalUatWindowsMsix.ps1");

        Assert.Contains("DEEP_PHYSICAL_UAT_TRUST_FLOOR_BUNDLE", script,
            StringComparison.Ordinal);
        Assert.Contains("-ExpectedAndroidApplicationId $androidPackage", script,
            StringComparison.Ordinal);
        Assert.Contains("DeepPhysicalUatMrXPublicKeySha256", script,
            StringComparison.Ordinal);
        Assert.Contains("DeepPhysicalUatPrivacyRoutesSignature", script,
            StringComparison.Ordinal);
        Assert.Contains("DeepPhysicalUatAndroidCodeTransparencyManifest", script,
            StringComparison.Ordinal);
        Assert.Contains("ACT1 SHA-256 differs from the UAT trust-floor approval", script,
            StringComparison.Ordinal);
        Assert.Contains("DeepPhysicalUatWindowsSigningCertificateSha256", windowsScript,
            StringComparison.Ordinal);
        Assert.Contains("WindowsBuildArtifactSha256", windowsScript,
            StringComparison.Ordinal);
        Assert.Contains("shell:AppsFolder", windowsScript, StringComparison.Ordinal);
        Assert.Contains("ProductionPackageUntouched = $true", windowsScript,
            StringComparison.Ordinal);
        Assert.Contains("UnpackagedCopySupported = $false", windowsScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain("-p:DeepProductionMrXPublicKeySha256", windowsScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain("-p:DeepProductionMrXPublicKeySha256", script,
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
