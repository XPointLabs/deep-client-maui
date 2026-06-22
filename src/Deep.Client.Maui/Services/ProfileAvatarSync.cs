using Deep.Client.Shared.State;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui.Services;

internal static class ProfileAvatarSync
{
    public static async Task SaveAndPublishAsync(
        FileResult photo,
        string avatarPath,
        ClientRuntime runtime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentException.ThrowIfNullOrWhiteSpace(avatarPath);

        var directory = Path.GetDirectoryName(avatarPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using (var source = await photo.OpenReadAsync().ConfigureAwait(false))
        await using (var destination = File.Create(avatarPath))
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        if (!runtime.AvatarProfiles.IsEnabled)
        {
            return;
        }

        var account = await runtime.Accounts.GetActiveAccountAsync(cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return;
        }

        await using var upload = File.OpenRead(avatarPath);
        await runtime.AvatarProfiles
            .UploadAsync(account.SessionId, upload, ResolveContentType(photo), cancellationToken)
            .ConfigureAwait(false);
    }

    private static string ResolveContentType(FileResult photo)
    {
        if (!string.IsNullOrWhiteSpace(photo.ContentType))
        {
            return photo.ContentType;
        }

        return Path.GetExtension(photo.FileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
    }
}
