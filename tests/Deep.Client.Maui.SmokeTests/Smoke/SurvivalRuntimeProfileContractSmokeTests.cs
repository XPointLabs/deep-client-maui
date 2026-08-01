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
        Assert.DoesNotContain("MetadataPrivateTransportRequired = false", program, StringComparison.Ordinal);
        Assert.Contains("a mode string is not authority", program, StringComparison.Ordinal);
        Assert.Contains("ResolveMailboxDeliveryPolicy", program, StringComparison.Ordinal);
        Assert.Contains("bool survivalDevelopment) => new();", program, StringComparison.Ordinal);
        Assert.Contains("RoutedSessionStorageTransportOptions transportOptions", factory, StringComparison.Ordinal);
        Assert.Contains("new RoutedSessionStorageMessageTransport(", factory, StringComparison.Ordinal);
        Assert.Contains("transportOptions);", factory, StringComparison.Ordinal);
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
        Assert.DoesNotContain("uninstall", script, StringComparison.OrdinalIgnoreCase);
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
