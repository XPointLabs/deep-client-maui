namespace Microsoft.Maui.ApplicationModel
{
    public sealed record OpenFileRequest(string Title, Microsoft.Maui.Storage.ReadOnlyFile File);

    public sealed class LauncherImplementation
    {
        public Task<bool> OpenAsync(OpenFileRequest request) => Task.FromResult(true);
    }

    public static class Launcher
    {
        public static LauncherImplementation Default { get; } = new();
    }
}

namespace Microsoft.Maui.ApplicationModel.DataTransfer
{
    public sealed record ShareFile(string File, string ContentType);

    public sealed class ShareFileRequest
    {
        public string? Title { get; set; }

        public ShareFile? File { get; set; }
    }

    public sealed class ShareImplementation
    {
        public Task RequestAsync(ShareFileRequest request) => Task.CompletedTask;
    }

    public static class Share
    {
        public static ShareImplementation Default { get; } = new();
    }
}

namespace Microsoft.Maui.Controls
{
    public class Page
    {
        public Task<bool> DisplayAlertAsync(string title, string message, string cancel) => Task.FromResult(true);
    }
}

namespace Microsoft.Maui.Storage
{
    public sealed record ReadOnlyFile(string FullPath, string ContentType);

    public static class FileSystem
    {
        public static string CacheDirectory { get; } = InitializeDirectory("cache");

        public static string AppDataDirectory { get; } = InitializeDirectory("app-data");

        private static string InitializeDirectory(string name)
        {
            var path = Path.Combine(Path.GetTempPath(), "deep-client-maui-tests", name);
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public sealed class SecureStorageImplementation
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

        public Task<string?> GetAsync(string key) =>
            Task.FromResult(values.TryGetValue(key, out var value) ? value : null);

        public Task SetAsync(string key, string value)
        {
            values[key] = value;
            return Task.CompletedTask;
        }

        public bool Remove(string key) => values.Remove(key);
    }

    public static class SecureStorage
    {
        public static SecureStorageImplementation Default { get; } = new();

        public static Task<string?> GetAsync(string key) => Default.GetAsync(key);

        public static Task SetAsync(string key, string value) => Default.SetAsync(key, value);

        public static bool Remove(string key) => Default.Remove(key);
    }

    public sealed class PreferencesImplementation
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> values = new(StringComparer.Ordinal);

        public bool Get(string key, bool defaultValue) =>
            values.TryGetValue(key, out var value) && value is bool typed ? typed : defaultValue;

        public string Get(string key, string defaultValue) =>
            values.TryGetValue(key, out var value) && value is string typed ? typed : defaultValue;

        public void Set(string key, bool value) => values[key] = value;

        public void Set(string key, string value) => values[key] = value;

        public bool Remove(string key) => values.TryRemove(key, out _);
    }

    public static class Preferences
    {
        public static PreferencesImplementation Default { get; } = new();
    }
}
