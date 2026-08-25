using System.Reflection;
using System.Text;
using System.Text.Json;
using Deep.Client.Maui.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class MailboxRuntimeProvisioningPathTests
{
    [Theory]
    [InlineData(
        "inventory",
        "Не удалось подготовить Deep",
        "Локальная конфигурация транспорта отсутствует или повреждена. Установите доверенную конфигурацию приложения и повторите запуск.")]
    [InlineData(
        "platform-binding",
        "Конфигурация не подходит устройству",
        "Конфигурация транспорта выпущена для другой платформы. Установите конфигурацию для этого устройства и повторите запуск.")]
    [InlineData(
        "approval-signature",
        "Конфигурация не подтверждена",
        "Не удалось проверить подпись конфигурации транспорта. Установите доверенную конфигурацию и повторите запуск.")]
    public void RuntimeValidationCodesHaveSafeActionablePresentation(
        string code,
        string expectedStatus,
        string expectedGuidance)
    {
        var exception = new MailboxRuntimeValidationException(
            code,
            new InvalidDataException("sensitive internal detail"));

        var presentation = exception.ToUserPresentation();

        Assert.Equal(expectedStatus, presentation.Status);
        Assert.Equal(expectedGuidance, presentation.Guidance);
        Assert.DoesNotContain("sensitive", presentation.Status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sensitive", presentation.Guidance, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("подключ", presentation.Guidance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PinnedApprovalRejectsTamperAndWrongPinBeforeRuntimeImport()
    {
        var payload = Convert.FromHexString("72");
        var publicKey = Convert.FromHexString(
            "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c");
        var signature = Convert.FromHexString(
            "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da0" +
            "85ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00");
        var pin = System.Security.Cryptography.SHA256.HashData(publicKey);

        MailboxRuntimeProvisioning.ValidatePinnedApproval(
            payload, signature, publicKey, pin);

        signature[0] ^= 1;
        Assert.Throws<InvalidDataException>(() =>
            MailboxRuntimeProvisioning.ValidatePinnedApproval(
                payload, signature, publicKey, pin));
        signature[0] ^= 1;
        pin[0] ^= 1;
        Assert.Throws<InvalidDataException>(() =>
            MailboxRuntimeProvisioning.ValidatePinnedApproval(
                payload, signature, publicKey, pin));
    }

    [Fact]
    public void PrivacyRoutesRequireExactDisjointThreeHopHttpsArtifact()
    {
        var routes = MailboxRuntimeProvisioning.ParsePrivacyRoutes(
            Encoding.UTF8.GetBytes(PrivacyRoutesJson()),
            "android");

        Assert.Equal("https://127.0.0.1:41803/", routes.Primary.EntryOrigin.AbsoluteUri);
        Assert.Equal("https://127.0.0.1:41805/", routes.Fallback.EntryOrigin.AbsoluteUri);
        Assert.Equal(3, routes.Primary.Hops.Count);
        Assert.Equal(3, routes.Fallback.Hops.Count);
        Assert.Equal(Enumerable.Repeat((byte)1, 32), routes.Primary.Hops[0].RouterId.ToArray());
        Assert.Equal(Enumerable.Repeat((byte)6, 32), routes.Fallback.Hops[2].RouterId.ToArray());
    }

    [Fact]
    public void PrivacyRouteBytesMustMatchActivationAndSignedPolicyHashes()
    {
        var encoded = Encoding.UTF8.GetBytes(PrivacyRoutesJson());
        var digest = System.Security.Cryptography.SHA256.HashData(encoded);
        var signedPolicy = JsonSerializer.SerializeToUtf8Bytes(new
        {
            privacyRoutesSha256 = Convert.ToHexString(digest).ToLowerInvariant()
        });

        MailboxRuntimeProvisioning.ValidatePrivacyRoutesBinding(
            encoded, digest, signedPolicy);

        digest[0] ^= 1;
        Assert.Throws<InvalidDataException>(() =>
            MailboxRuntimeProvisioning.ValidatePrivacyRoutesBinding(
                encoded, digest, signedPolicy));
        digest[0] ^= 1;
        encoded[0] ^= 1;
        Assert.Throws<InvalidDataException>(() =>
            MailboxRuntimeProvisioning.ValidatePrivacyRoutesBinding(
                encoded, digest, signedPolicy));
    }

    [Fact]
    public void PrivacyRoutesRejectUnknownFieldsWrongPlatformAndRouteOverlap()
    {
        var valid = PrivacyRoutesJson();
        Assert.Throws<InvalidDataException>(() =>
            MailboxRuntimeProvisioning.ParsePrivacyRoutes(
                Encoding.UTF8.GetBytes(valid.Replace(
                    "\"schemaVersion\":1,",
                    "\"schemaVersion\":1,\"unknown\":true,",
                    StringComparison.Ordinal)),
                "android"));
        Assert.Throws<InvalidDataException>(() =>
            MailboxRuntimeProvisioning.ParsePrivacyRoutes(
                Encoding.UTF8.GetBytes(valid),
                "windows"));
        Assert.Throws<InvalidDataException>(() =>
            MailboxRuntimeProvisioning.ParsePrivacyRoutes(
                Encoding.UTF8.GetBytes(valid.Replace(
                    Hex(6), Hex(1), StringComparison.Ordinal)),
                "android"));
    }

    [Theory]
    [InlineData("http://127.0.0.1:41803/")]
    [InlineData("https://127.0.0.1:41803/path")]
    [InlineData("https://user@127.0.0.1:41803/")]
    [InlineData("https://127.0.0.1:41803/?query=1")]
    [InlineData("HTTPS://127.0.0.1:41803/")]
    public void PrivacyRoutesRejectNonCanonicalIngressOrigin(string origin)
    {
        Assert.Throws<InvalidDataException>(() =>
            MailboxRuntimeProvisioning.ParsePrivacyRoutes(
                Encoding.UTF8.GetBytes(PrivacyRoutesJson().Replace(
                    "https://127.0.0.1:41803/", origin, StringComparison.Ordinal)),
                "android"));
    }

    [Fact]
    public void SafeRootAllowsPlatformIndirectionAboveTrustedAppDataAnchor()
    {
        var root = NewRoot();
        try
        {
            var physical = Path.Combine(root, "physical");
            Directory.CreateDirectory(physical);
            var platformLink = Path.Combine(root, "platform-link");
            Directory.CreateSymbolicLink(platformLink, physical);
            var appData = Path.Combine(platformLink, "app-data");
            var runtime = Path.Combine(appData, MailboxRuntimeProvisioning.DirectoryName);
            Directory.CreateDirectory(runtime);

            var accepted = InvokeSafeRoot(appData);

            Assert.Equal(Path.GetFullPath(runtime), accepted);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void SafeRootRejectsIndirectionInsideTrustedAppDataAnchor()
    {
        var root = NewRoot();
        try
        {
            var appData = Path.Combine(root, "app-data");
            var redirectedRuntime = Path.Combine(root, "redirected-runtime");
            Directory.CreateDirectory(appData);
            Directory.CreateDirectory(redirectedRuntime);
            Directory.CreateSymbolicLink(
                Path.Combine(appData, MailboxRuntimeProvisioning.DirectoryName),
                redirectedRuntime);

            var exception = Assert.Throws<TargetInvocationException>(
                () => InvokeSafeRoot(appData));
            var inner = Assert.IsType<InvalidDataException>(exception.InnerException);
            Assert.Equal("Mailbox runtime path contains a reparse point.", inner.Message);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void SafeRootPreservesTrailingSeparatorAndRejectsFilesystemRoot()
    {
        var root = NewRoot();
        try
        {
            var appData = Path.Combine(root, "app-data");
            var runtime = Path.Combine(appData, MailboxRuntimeProvisioning.DirectoryName);
            Directory.CreateDirectory(runtime);

            Assert.Equal(
                Path.GetFullPath(runtime),
                InvokeSafeRoot(appData + Path.DirectorySeparatorChar));

            var fileSystemRoot = Path.GetPathRoot(Path.GetFullPath(root))
                ?? throw new InvalidOperationException("Test root has no filesystem root.");
            var exception = Assert.Throws<TargetInvocationException>(
                () => InvokeSafeRoot(fileSystemRoot));
            var inner = Assert.IsType<InvalidDataException>(exception.InnerException);
            Assert.Equal("App-private data directory cannot be a filesystem root.",
                inner.Message);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void SafeRootRejectsMissingAnchorAndSafeFileRejectsNestedLink()
    {
        var root = NewRoot();
        try
        {
            var missing = Assert.Throws<TargetInvocationException>(
                () => InvokeSafeRoot(Path.Combine(root, "missing")));
            Assert.Equal(
                "App-private data directory is missing.",
                Assert.IsType<InvalidDataException>(missing.InnerException).Message);

            var appData = Path.Combine(root, "app-data");
            var runtime = Path.Combine(appData, MailboxRuntimeProvisioning.DirectoryName);
            Directory.CreateDirectory(runtime);
            var externalFile = Path.Combine(root, "external.json");
            File.WriteAllText(externalFile, "{}");
            var linkedFile = Path.Combine(runtime, "activation.v1.json");
            File.CreateSymbolicLink(linkedFile, externalFile);

            var linked = Assert.Throws<TargetInvocationException>(
                () => InvokeSafeFile(runtime, "activation.v1.json"));
            Assert.Equal(
                "Mailbox runtime path contains a reparse point.",
                Assert.IsType<InvalidDataException>(linked.InnerException).Message);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string InvokeSafeRoot(string appDataDirectory) =>
        (string)(typeof(MailboxRuntimeProvisioning)
            .GetMethod("SafeRoot", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [appDataDirectory, MailboxRuntimeProvisioning.DirectoryName])
            ?? throw new InvalidOperationException("SafeRoot returned no canonical path."));

    private static string InvokeSafeFile(string root, string name) =>
        (string)(typeof(MailboxRuntimeProvisioning)
            .GetMethod("SafeFile", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [root, name])
            ?? throw new InvalidOperationException("SafeFile returned no canonical path."));

    private static string NewRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(), $"deep-mailbox-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string PrivacyRoutesJson() => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        developmentOnly = true,
        platform = "android",
        primary = new
        {
            entryOrigin = "https://127.0.0.1:41803/",
            hops = new[]
            {
                new { routerId = Hex(1), x25519PublicKey = Hex(17) },
                new { routerId = Hex(2), x25519PublicKey = Hex(18) },
                new { routerId = Hex(3), x25519PublicKey = Hex(19) }
            }
        },
        fallback = new
        {
            entryOrigin = "https://127.0.0.1:41805/",
            hops = new[]
            {
                new { routerId = Hex(4), x25519PublicKey = Hex(20) },
                new { routerId = Hex(5), x25519PublicKey = Hex(21) },
                new { routerId = Hex(6), x25519PublicKey = Hex(22) }
            }
        }
    });

    private static string Hex(byte value) =>
        Convert.ToHexString(Enumerable.Repeat(value, 32).ToArray()).ToLowerInvariant();

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
