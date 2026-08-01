using System.Reflection;
using Deep.Client.Maui.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class MailboxRuntimeProvisioningPathTests
{
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
