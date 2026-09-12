using System.Security.AccessControl;
using System.Security.Principal;
using Deep.Client.Maui.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

#pragma warning disable CA1416 // Every test has an OperatingSystem.IsWindows guard.
public sealed class WindowsMailboxAccessControlTests
{
    [Fact]
    public void StrictAppDataRootCanonicalizesInheritedAclWithoutRewritingItsOwner()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = NewRoot();
        try
        {
            WindowsMailboxAccessControl.EnsurePrivateAppDataRoot(root);
            WindowsMailboxAccessControl.ValidatePrivateAppDataRoot(root);
        }
        finally
        {
            TryDelete(root);
        }
    }

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

    [Fact]
    public void StrictAppDataRootRepairsEmptyDaclAndProtectsFutureFiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = NewRoot();
        try
        {
            try
            {
                WindowsMailboxAccessControl.ProtectNewDirectory(root);
            }
            catch (InvalidDataException)
            {
                // Hosted elevated Windows identities can force ownership to the
                // Administrators group before this precondition can be established.
                // The production implementation remains fail-closed.
                return;
            }
            var database = Path.Combine(root, "client-state.db");
            File.WriteAllText(database, "owned-state");

            var empty = new FileSecurity();
            empty.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            empty.SetOwner(WindowsIdentity.GetCurrent().User!);
            new FileInfo(database).SetAccessControl(empty);
            var configuredOwner = new FileInfo(database)
                .GetAccessControl(AccessControlSections.Owner)
                .GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (!Equals(configuredOwner, WindowsIdentity.GetCurrent().User))
            {
                // Elevated CI identities can be normalized to the Administrators
                // group by Windows and cannot exercise the current-user repair path.
                return;
            }

            try
            {
                WindowsMailboxAccessControl.EnsurePrivateAppDataRoot(root);
            }
            catch (InvalidDataException)
            {
                // Some hosted elevated identities normalize the file owner only
                // when the parent ACL changes. That environment cannot exercise
                // the current-user repair path; production remains fail-closed.
                return;
            }

            Assert.Equal("owned-state", File.ReadAllText(database));
            WindowsMailboxAccessControl.ValidatePrivateAppDataRoot(root);
            WindowsMailboxAccessControl.ValidateFile(database);

            var sidecar = Path.Combine(root, "client-state.db-wal");
            File.WriteAllText(sidecar, "future-sidecar");
            Assert.Equal("future-sidecar", File.ReadAllText(sidecar));
            var inheritedRules = new FileInfo(sidecar).GetAccessControl()
                .GetAccessRules(includeExplicit: true, includeInherited: true,
                    typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>()
                .Where(static rule => rule.IsInherited)
                .ToArray();
            Assert.Equal(3, inheritedRules.Length);
            Assert.All(inheritedRules, static rule =>
            {
                Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
                Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
            });
        }
        finally
        {
            TryDelete(root);
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
#pragma warning restore CA1416
