using System.Security.Cryptography;
using System.Text.Json;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Services;

internal static class DevelopmentMailboxHolderBootstrap
{
    internal const string DirectoryName = "mailbox-holder-bootstrap-v1";

    public static void Publish(
        string appDataDirectory,
        MailboxClientPlatform platform,
        MailboxHolderIdentity holder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        ArgumentNullException.ThrowIfNull(holder);
        if (!Enum.IsDefined(platform) || holder.Ed25519PublicKey.Length != 32)
            throw new InvalidOperationException("Mailbox holder bootstrap identity is invalid.");

        var root = Path.GetFullPath(Path.Combine(appDataDirectory, DirectoryName));
        var rootAlreadyExisted = Directory.Exists(root);
        Directory.CreateDirectory(root);
        RejectReparse(root);
        if (rootAlreadyExisted)
        {
            ValidatePrivateDirectory(root);
        }
        else
        {
            ProtectNewPrivateDirectory(root);
        }

        var platformName = platform.ToString().ToLowerInvariant();
        var destination = Path.Combine(root, platformName + ".holder.v1.json");
        var temporary = Path.Combine(root, "." + platformName + "." +
            Guid.NewGuid().ToString("N") + ".tmp");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            developmentOnly = true,
            platform = platformName,
            sessionId = holder.SessionId.Value,
            ed25519PublicKey = Convert.ToHexStringLower(holder.Ed25519PublicKey.Span)
        });
        try
        {
            if (File.Exists(destination))
            {
                RejectReparse(destination);
                ValidatePrivateFile(destination);
                var info = new FileInfo(destination);
                if (info.Length == payload.Length)
                {
                    var existing = File.ReadAllBytes(destination);
                    try
                    {
                        if (CryptographicOperations.FixedTimeEquals(existing, payload)) return;
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(existing);
                    }
                }
            }
            using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }
            ProtectNewPrivateFile(temporary);
            File.Move(temporary, destination, overwrite: true);
            RejectReparse(destination);
            ValidatePrivateFile(destination);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void ProtectNewPrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsMailboxAccessControl.ProtectNewDirectory(path);
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead |
            UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        ValidatePrivateDirectory(path);
    }

    private static void ProtectNewPrivateFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsMailboxAccessControl.ProtectNewFile(path);
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        ValidatePrivateFile(path);
    }

    private static void ValidatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsMailboxAccessControl.ValidateDirectory(path);
            return;
        }

        RequireUnixMode(path, UnixFileMode.UserRead |
            UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void ValidatePrivateFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsMailboxAccessControl.ValidateFile(path);
            return;
        }

        RequireUnixMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void RequireUnixMode(string path, UnixFileMode expected)
    {
        var actual = File.GetUnixFileMode(path);
        if (actual != expected)
        {
            throw new InvalidDataException(
                "Mailbox holder bootstrap permissions are not canonical.");
        }
    }

    private static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Mailbox holder bootstrap path cannot be a reparse point.");
    }
}
