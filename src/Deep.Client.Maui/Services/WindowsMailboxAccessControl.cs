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
        ApplyExact(path, isDirectory: true, InheritanceFlags.None);
    }

    internal static void ProtectNewFile(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        ApplyExact(path, isDirectory: false, InheritanceFlags.None);
    }

    internal static void EnsurePrivateAppDataRoot(string path)
    {
        if (!OperatingSystem.IsWindows()) return;

        ApplyExact(
            path,
            isDirectory: true,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            preserveExistingOwner: true);

        // Older strict-lane roots were protected without inheritable ACEs. Files
        // created below them therefore retained an empty DACL after their first
        // handle closed. Repair only the closed set of app-owned root files; all
        // future SQLite sidecars and diagnostics inherit the exact root policy.
        var rootFiles = Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly)
            .ToDictionary(
                static candidate => Path.GetFileName(candidate),
                static candidate => candidate,
                StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[]
                 {
                     "client-state.db",
                     "client-state.db-wal",
                     "client-state.db-shm",
                     "crash.log",
                     "crash.log.1"
                 })
        {
            if (rootFiles.TryGetValue(name, out var candidate))
            {
                ApplyExact(
                    candidate,
                    isDirectory: false,
                    InheritanceFlags.None,
                    preserveExistingOwner: true);
            }
        }
    }

    internal static void ValidatePrivateAppDataRoot(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        ValidateExact(
            path,
            isDirectory: true,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
    }

    internal static void ValidateDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        ValidateExact(path, isDirectory: true, InheritanceFlags.None);
    }

    internal static void ValidateFile(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        ValidateExact(path, isDirectory: false, InheritanceFlags.None);
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

    private static void ApplyExact(
        string path,
        bool isDirectory,
        InheritanceFlags inheritanceFlags,
        bool preserveExistingOwner = false)
    {
        RejectReparse(path);
        var owner = CurrentUser();
        if (preserveExistingOwner)
        {
            FileSystemSecurity existing = isDirectory
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Owner);
            var existingOwner = existing.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (existingOwner is null || !existingOwner.Equals(owner))
            {
                throw new InvalidDataException(
                    "Windows app-data ACL repair refuses a non-owner path.");
            }
        }

        FileSystemSecurity security = isDirectory
            ? new DirectorySecurity()
            : new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        if (!preserveExistingOwner)
        {
            security.SetOwner(owner);
        }
        foreach (var identity in new[] { owner, LocalSystem, BuiltinAdministrators })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                identity,
                FileSystemRights.FullControl,
                inheritanceFlags,
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
        ValidateExact(path, isDirectory, inheritanceFlags);
    }

    private static void ValidateExact(
        string path,
        bool isDirectory,
        InheritanceFlags inheritanceFlags)
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
                rule.InheritanceFlags != inheritanceFlags ||
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
