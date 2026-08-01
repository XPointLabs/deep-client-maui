using System.Reflection;
using Deep.Client.Maui.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class MailboxRuntimeProvisioningPathTests
{
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
