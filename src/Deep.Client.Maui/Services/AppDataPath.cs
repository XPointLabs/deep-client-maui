using Microsoft.Maui.Storage;
using Deep.Client.Maui.Services;

namespace Deep.Client.Maui;

internal static class AppDataPath
{
    internal const string BootstrapEnvironment = "DEEP_E2E_BOOTSTRAP";
    internal const string RootEnvironment = "DEEP_E2E_APPDATA_ROOT";
    internal const string StrictWindowsEnvironment = "DEEP_STRICT_WINDOWS_UI";

    internal static string Resolve()
    {
#if DEBUG
        if (string.Equals(
                Environment.GetEnvironmentVariable(StrictWindowsEnvironment),
                "1",
                StringComparison.Ordinal))
        {
            var bootstrap = Environment.GetEnvironmentVariable(BootstrapEnvironment);
            if (!string.Equals(bootstrap, "stub", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(bootstrap, "live", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{BootstrapEnvironment} must be stub or live in the strict Windows lane.");
            }
            var testRoot = Environment.GetEnvironmentVariable(RootEnvironment);
            if (string.IsNullOrWhiteSpace(testRoot) || !Path.IsPathFullyQualified(testRoot))
            {
                throw new InvalidOperationException(
                    $"{RootEnvironment} must be an absolute temporary path in the strict Windows lane.");
            }

            var fullPath = Path.GetFullPath(testRoot);
            var realPath = Path.GetFullPath(FileSystem.AppDataDirectory);
            if (IsSameOrDescendant(Path.GetRelativePath(realPath, fullPath)) ||
                IsSameOrDescendant(Path.GetRelativePath(fullPath, realPath)))
            {
                throw new InvalidOperationException(
                    $"{RootEnvironment} must be isolated from the real application-data directory.");
            }
            Directory.CreateDirectory(fullPath);
            RejectReparsePoints(fullPath);
            WindowsMailboxAccessControl.EnsurePrivateAppDataRoot(fullPath);
            return fullPath;
        }
#endif
        return FileSystem.AppDataDirectory;
    }

    private static bool IsSameOrDescendant(string relativePath)
    {
        if (relativePath == ".")
        {
            return true;
        }
        if (Path.IsPathFullyQualified(relativePath))
        {
            return false;
        }
        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments[0] != "..";
    }

    private static void RejectReparsePoints(string path)
    {
        var current = new DirectoryInfo(path);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"{RootEnvironment} must not traverse a reparse point.");
            }
            current = current.Parent;
        }
    }
}
