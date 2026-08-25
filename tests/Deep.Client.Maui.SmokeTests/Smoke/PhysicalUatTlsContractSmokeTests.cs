using System.Security.Cryptography;

namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class PhysicalUatTlsContractSmokeTests
{
    [Fact]
    public void PhysicalProfileIsHttpsCleartextForbiddenAndBoundToExactCa()
    {
        var profile = File.ReadAllText(WorkspacePath("eng", "survival.dev.env"));
        var network = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "Platforms", "Android", "Resources", "xml",
            "network_security_config_physical_e2e.xml"));
        var caPath = WorkspacePath(
            "src", "Deep.Client.Maui", "Platforms", "Android", "Resources", "raw",
            "deep_physical_uat_ca.crt");
        var project = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "Deep.Client.Maui.csproj"));
        var program = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "MauiProgram.cs"));
        var mailboxTransport = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "Services", "StoreBoundNativeMau2Transport.cs"));

        Assert.DoesNotContain("http://192.168.1.43", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("DEEP_TLS_PINS", profile, StringComparison.Ordinal);
        Assert.Contains("https://192.168.1.43:41823", profile, StringComparison.Ordinal);
        Assert.Contains("cleartextTrafficPermitted=\"false\"", network, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(
            network,
            "cleartextTrafficPermitted=\\\"true\\\"",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            .Cast<System.Text.RegularExpressions.Match>());
        Assert.Contains("<domain includeSubdomains=\"false\">192.168.1.43</domain>", network,
            StringComparison.Ordinal);
        Assert.Contains("@raw/deep_physical_uat_ca", network, StringComparison.Ordinal);
        Assert.Contains("Deep.Client.Maui.PhysicalUatRootCa", project,
            StringComparison.Ordinal);
        Assert.Contains("#if DEBUG && DEEP_PHYSICAL_E2E && ANDROID", program,
            StringComparison.Ordinal);
        Assert.Contains("X509ChainTrustMode.CustomRootTrust", program,
            StringComparison.Ordinal);
        Assert.Contains("X509RevocationMode.Online", program,
            StringComparison.Ordinal);
        Assert.Contains("X509RevocationFlag.ExcludeRoot", program,
            StringComparison.Ordinal);
        Assert.Contains("X509VerificationFlags.NoFlag", program,
            StringComparison.Ordinal);
        Assert.Contains("RemoteCertificateNameMismatch", program,
            StringComparison.Ordinal);
        Assert.Contains("RemoteCertificateNotAvailable", program,
            StringComparison.Ordinal);
        Assert.Contains("sslPolicyErrors == System.Net.Security.SslPolicyErrors.None", program,
            StringComparison.Ordinal);
        Assert.Contains("sslPolicyErrors != System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors",
            program, StringComparison.Ordinal);
        Assert.Contains("CreateServiceTransportNetworkHooks()", program,
            StringComparison.Ordinal);
        Assert.Contains("CreateFileTransportNetworkHooks(fileConnectIps)", program,
            StringComparison.Ordinal);
        Assert.Contains("CreatePhysicalUatServerCertificateValidationCallback()", program,
            StringComparison.Ordinal);
        Assert.Contains("new PrivacyRoutedMailboxBinaryIngress(", mailboxTransport,
            StringComparison.Ordinal);
        Assert.DoesNotContain("serverCertificateValidationCallback", mailboxTransport,
            StringComparison.Ordinal);
        var validatorStart = program.IndexOf(
            "private static bool ValidatePhysicalUatServerCertificate(",
            StringComparison.Ordinal);
        var validatorEnd = program.IndexOf(
            "private static System.Security.Cryptography.X509Certificates.X509Certificate2",
            validatorStart,
            StringComparison.Ordinal);
        Assert.True(validatorStart >= 0 && validatorEnd > validatorStart);
        Assert.DoesNotContain("return true;", program[validatorStart..validatorEnd],
            StringComparison.Ordinal);
        Assert.Equal(
            "3fcbafc7014f27e11ab66043ad8fa2c931da048ed9973779e75acca435956f29",
            Convert.ToHexStringLower(SHA256.HashData(
                System.Security.Cryptography.X509Certificates.X509CertificateLoader
                    .LoadCertificateFromFile(caPath).RawData)));
    }

    [Fact]
    public void PhysicalProfileCannotBeRedirectedByProcessOrAdjacentFile()
    {
        var program = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "MauiProgram.cs"));
        var methodStart = program.IndexOf(
            "internal static string? ResolveRuntimeSetting(string key)", StringComparison.Ordinal);
        var methodEnd = program.IndexOf(
            "private static string? ResolveEmbeddedRuntimeSetting", methodStart, StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var method = program[methodStart..methodEnd];

        var physical = method.IndexOf("#if DEBUG && DEEP_PHYSICAL_E2E", StringComparison.Ordinal);
        var fallback = method.IndexOf("#else", physical, StringComparison.Ordinal);
        Assert.True(physical >= 0 && fallback > physical);
        var authoritativeBranch = method[physical..fallback];
        Assert.Contains("ResolveEmbeddedRuntimeSetting(ReleaseRuntimeEnvFile, key)",
            authoritativeBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.GetEnvironmentVariable", authoritativeBranch,
            StringComparison.Ordinal);
        Assert.DoesNotContain("File.Exists", authoritativeBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsReleaseRuntimeEnvFile", authoritativeBranch,
            StringComparison.Ordinal);
    }

    private static string WorkspacePath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "Deep.Client.Maui.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory!.FullName, .. parts]);
    }
}
