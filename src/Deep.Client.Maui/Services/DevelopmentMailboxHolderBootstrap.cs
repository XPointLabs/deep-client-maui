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
        Directory.CreateDirectory(root);
        RejectReparse(root);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(root, UnixFileMode.UserRead |
                UnixFileMode.UserWrite | UnixFileMode.UserExecute);

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
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, destination, overwrite: true);
            RejectReparse(destination);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Mailbox holder bootstrap path cannot be a reparse point.");
    }
}
