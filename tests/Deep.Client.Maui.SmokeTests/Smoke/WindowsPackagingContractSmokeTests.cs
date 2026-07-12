namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class WindowsPackagingContractSmokeTests
{
    [Fact]
    public void ManifestUsesTheExecutableResolvedByTheReleaseScript()
    {
        var manifest = ReadWorkspaceFile(
            "src",
            "Deep.Client.Maui",
            "Platforms",
            "Windows",
            "Package.appxmanifest.template");

        Assert.DoesNotContain("$targetnametoken$", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            3,
            System.Text.RegularExpressions.Regex.Matches(
                manifest,
                "Executable=\"__EXECUTABLE_NAME__\"",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant).Count);
    }

    [Fact]
    public void ReleaseScriptRejectsTokensAndVerifiesThePackagedExecutable()
    {
        var script = ReadWorkspaceFile("eng", "build-windows-msix.ps1");

        Assert.Contains("-getProperty:TargetName,TargetFramework", script, StringComparison.Ordinal);
        Assert.Contains(".Replace('__EXECUTABLE_NAME__'", script, StringComparison.Ordinal);
        Assert.Contains(
            "Assert-NoUnresolvedManifestTokens $packagedManifestText 'The packaged AppxManifest.xml'",
            script,
            StringComparison.Ordinal);
        Assert.Contains("$comServers = @($packagedManifest.SelectNodes('//com:ExeServer'", script, StringComparison.Ordinal);
        Assert.Contains("Test-MsixContainsEntry $package.FullName $executableName", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseScriptEmitsACompleteFrameworkDependentSideloadPayload()
    {
        var script = ReadWorkspaceFile("eng", "build-windows-msix.ps1");

        Assert.Contains("'AppPackages') + [IO.Path]::DirectorySeparatorChar", script, StringComparison.Ordinal);
        Assert.Contains("-p:AppxPackageDir=$appxPackageDir", script, StringComparison.Ordinal);
        Assert.Contains("EndsWith('_Test'", script, StringComparison.Ordinal);
        Assert.Contains("Microsoft.WindowsAppRuntime.", script, StringComparison.Ordinal);
        Assert.Contains("foreach ($dependency in $packageDependencies)", script, StringComparison.Ordinal);
        Assert.Contains("Test-MsixContainsEntry $dependencyPackage.FullName 'AppxSignature.p7x'", script, StringComparison.Ordinal);
        Assert.Contains("@('Install.ps1', 'Add-AppDevPackage.ps1')", script, StringComparison.Ordinal);
        Assert.Contains("Copy-Item -LiteralPath $sourcePayload.FullName -Destination $finalPayload -Recurse", script, StringComparison.Ordinal);
        Assert.Contains("SHA256SUMS.txt", script, StringComparison.Ordinal);
        Assert.Contains("[IO.Compression.ZipFile]::CreateFromDirectory", script, StringComparison.Ordinal);
        Assert.Contains("[IO.Compression.CompressionLevel]::NoCompression", script, StringComparison.Ordinal);
        Assert.DoesNotContain("[IO.Path]::GetRelativePath", script, StringComparison.Ordinal);
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
