using System.Text.Json;

namespace Deep.Client.Maui.UiTests;

public sealed class WindowsUatPackageApprovalTests
{
    [Fact]
    public void Canonical_tuple_is_parsed_without_exposing_values_to_output()
    {
        WithTuple(Canonical(), path =>
        {
            var approval = WindowsUatPackageApproval.Load(path);

            Assert.Equal("network.xpoint.deep.e2e", approval.InstalledPackageName);
            Assert.Equal(new string('a', 64), approval.SigningCertificateSha256);
            Assert.Equal(new string('b', 64), approval.BuildArtifactSha256);
        });
    }

    [Fact]
    public void Unknown_property_is_rejected()
    {
        var tuple = Canonical();
        tuple["unexpected"] = true;

        WithTuple(tuple, path =>
            Assert.Throws<InvalidDataException>(() =>
                WindowsUatPackageApproval.Load(path)));
    }

    [Theory]
    [InlineData("signingCertificateSha256")]
    [InlineData("buildArtifactSha256")]
    public void Zero_security_hash_is_rejected(string property)
    {
        var tuple = Canonical();
        tuple[property] = new string('0', 64);

        WithTuple(tuple, path =>
            Assert.Throws<InvalidDataException>(() =>
                WindowsUatPackageApproval.Load(path)));
    }

    [WindowsUatInstalledFact]
    public void Installed_package_signature_and_complete_artifact_set_match_tuple()
    {
        var approval = WindowsUatPackageApproval.LoadAndVerify(
            Environment.GetEnvironmentVariable(WindowsUatPackageApproval.EnvironmentKey)!,
            Environment.GetEnvironmentVariable(
                WindowsUatPackageApproval.InstallRootEnvironmentKey)!,
            Environment.GetEnvironmentVariable("DEEP_MAUI_EXE")!);

        Assert.Equal("network.xpoint.deep.e2e", approval.InstalledPackageName);
    }

    private static Dictionary<string, object> Canonical() => new(StringComparer.Ordinal)
    {
        ["schemaVersion"] = 1,
        ["platform"] = "windows",
        ["applicationIdentity"] = "Deep.Client.Maui.exe",
        ["installedPackageName"] = "network.xpoint.deep.e2e",
        ["installedPackageFullName"] = "network.xpoint.deep.e2e_0.2.9.15_arm64__publisher",
        ["installedPackageFamilyName"] = "network.xpoint.deep.e2e_publisher",
        ["publisher"] = "CN=XPoint Labs Development",
        ["signingCertificateSha256"] = new string('a', 64),
        ["buildArtifactSha256"] = new string('b', 64)
    };

    private static void WithTuple(
        Dictionary<string, object> tuple,
        Action<string> assertion)
    {
        var root = Path.Combine(Path.GetTempPath(),
            "deep-windows-uat-approval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "approval.json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(tuple));
            assertion(path);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

internal sealed class WindowsUatInstalledFactAttribute : FactAttribute
{
    public WindowsUatInstalledFactAttribute()
    {
        var approval = Environment.GetEnvironmentVariable(
            WindowsUatPackageApproval.EnvironmentKey);
        var root = Environment.GetEnvironmentVariable(
            WindowsUatPackageApproval.InstallRootEnvironmentKey);
        var executable = Environment.GetEnvironmentVariable("DEEP_MAUI_EXE");
        if (string.IsNullOrWhiteSpace(approval) || !File.Exists(approval) ||
            string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) ||
            string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            Skip = "Installed Windows UAT approval inputs are not configured.";
    }
}
