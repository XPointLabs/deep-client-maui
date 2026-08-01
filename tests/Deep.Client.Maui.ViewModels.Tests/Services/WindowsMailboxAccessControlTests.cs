using System.Security.AccessControl;
using System.Security.Principal;
using Deep.Client.Maui.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class WindowsMailboxAccessControlTests
{
    [Fact]
    public void CanonicalWindowsAclAcceptsOnlyTheExactThreePrincipals()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = NewRoot();
        try
        {
            WindowsMailboxAccessControl.ProtectNewDirectory(root);
            var file = Path.Combine(root, "public.json");
            File.WriteAllText(file, "{}");
            WindowsMailboxAccessControl.ProtectNewFile(file);

            WindowsMailboxAccessControl.ValidateTree(root);

            var acl = new FileInfo(file).GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier("S-1-5-32-545"),
                FileSystemRights.Read,
                AccessControlType.Allow));
            new FileInfo(file).SetAccessControl(acl);
            Assert.Throws<InvalidDataException>(
                () => WindowsMailboxAccessControl.ValidateFile(file));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void CanonicalWindowsAclRejectsReparseTraversal()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = NewRoot();
        var outside = NewRoot();
        try
        {
            var link = Path.Combine(root, "linked");
            Directory.CreateSymbolicLink(link, outside);
            Assert.Throws<IOException>(
                () => WindowsMailboxAccessControl.ValidateTree(link));
        }
        finally
        {
            TryDelete(root);
            TryDelete(outside);
        }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "deep-windows-mailbox-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*",
                         SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                info.IsReadOnly = false;
            }
            Directory.Delete(root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
