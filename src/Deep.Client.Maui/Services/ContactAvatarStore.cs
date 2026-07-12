using Deep.Client.Shared.Domain;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui.Services;

internal static class ContactAvatarStore
{
    private const string ProfileAvatarFileName = "profile-avatar.jpg";
    private const string ContactAvatarDirectoryName = "contact-avatars";

    public static string GetProfileAvatarPath() =>
        Path.Combine(FileSystem.Current.AppDataDirectory, ProfileAvatarFileName);

    public static string GetContactAvatarPath(SessionId contactId)
    {
        var directory = Path.Combine(FileSystem.Current.AppDataDirectory, ContactAvatarDirectoryName);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{contactId.Value}.jpg");
    }

    public static string GetAvatarPath(SessionId contactId, bool isSelf) =>
        isSelf ? GetProfileAvatarPath() : GetContactAvatarPath(contactId);

    public static async Task SaveLocalContactAvatarAsync(FileResult photo, SessionId contactId, CancellationToken cancellationToken = default)
    {
        var targetPath = GetContactAvatarPath(contactId);
        await using var input = await photo.OpenReadAsync().ConfigureAwait(false);
        await using var output = File.Create(targetPath);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    public static void DeleteAvatar(SessionId contactId, bool isSelf)
    {
        var path = GetAvatarPath(contactId, isSelf);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    internal static void PurgeAll()
    {
        var profilePath = GetProfileAvatarPath();
        if (File.Exists(profilePath))
        {
            File.Delete(profilePath);
        }

        var contactDirectory = Path.Combine(FileSystem.Current.AppDataDirectory, ContactAvatarDirectoryName);
        if (Directory.Exists(contactDirectory))
        {
            Directory.Delete(contactDirectory, recursive: true);
        }
    }
}
