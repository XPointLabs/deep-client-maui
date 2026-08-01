using System.Security.AccessControl;
using System.Security.Principal;

namespace Deep.Client.Maui.Services;

/// <summary>
/// Windows-only ownership boundary for the DEV-local mailbox material.  The
/// app-data directory itself is owned by the platform, so this policy starts
/// at the dedicated mailbox roots and never attempts to loosen an ancestor.
/// </summary>
#pragma warning disable CA1416 // Guarded by OperatingSystem.IsWindows at every public boundary.
internal static class WindowsMailboxAccessControl
{
    private static readonly SecurityIdentifier LocalSystem =
        new("S-1-5-18");
    private static readonly SecurityIdentifier BuiltinAdministrators =
        new("S-1-5-32-544");

    internal static void ProtectNewDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        ApplyExact(path, isDirectory: true);
    }

    internal static void ProtectNewFile(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        ApplyExact(path, isDirectory: false);
    }

    internal static void ValidateDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        ValidateExact(path, isDirectory: true);
    }

    internal static void ValidateFile(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        ValidateExact(path, isDirectory: false);
    }

    internal static void ValidateTree(string root)
    {
        if (!OperatingSystem.IsWindows()) return;
        ValidateDirectory(root);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*",
                     SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    "Windows mailbox runtime contains a reparse point.");
            if (Directory.Exists(entry)) ValidateDirectory(entry);
            else ValidateFile(entry);
        }
    }

    private static SecurityIdentifier CurrentUser()
    {
        var user = WindowsIdentity.GetCurrent().User;
        if (user is null)
            throw new InvalidOperationException(
                "Windows mailbox ACL policy requires a current user SID.");
        return user;
    }

    private static void ApplyExact(string path, bool isDirectory)
    {
        RejectReparse(path);
        var owner = CurrentUser();
        FileSystemSecurity security = isDirectory
            ? new DirectorySecurity()
            : new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(owner);
        foreach (var identity in new[] { owner, LocalSystem, BuiltinAdministrators })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                identity,
                FileSystemRights.FullControl,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        try
        {
            if (isDirectory)
                new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
            else
                new FileInfo(path).SetAccessControl((FileSecurity)security);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new IOException("Could not establish the exact Windows mailbox ACL.",
                exception);
        }
        ValidateExact(path, isDirectory);
    }

    private static void ValidateExact(string path, bool isDirectory)
    {
        RejectReparse(path);
        var owner = CurrentUser();
        FileSystemSecurity security = isDirectory
            ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();
        var actualOwner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (!security.AreAccessRulesProtected || actualOwner is null ||
            !actualOwner.Equals(owner))
        {
            throw new InvalidDataException(
                "Windows mailbox ACL owner or inheritance is not exact.");
        }

        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            owner.Value, LocalSystem.Value, BuiltinAdministrators.Value
        };
        var rules = security.GetAccessRules(includeExplicit: true,
            includeInherited: true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>().ToArray();
        if (rules.Length != expected.Count || rules.Any(rule =>
                rule.AccessControlType != AccessControlType.Allow ||
                rule.IsInherited ||
                rule.FileSystemRights != FileSystemRights.FullControl ||
                rule.InheritanceFlags != InheritanceFlags.None ||
                rule.PropagationFlags != PropagationFlags.None ||
                rule.IdentityReference is not SecurityIdentifier sid ||
                !expected.Remove(sid.Value)))
        {
            throw new InvalidDataException("Windows mailbox ACL is not canonical.");
        }
    }

    private static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Windows mailbox ACL path cannot be a reparse point.");
    }
}
#pragma warning restore CA1416
