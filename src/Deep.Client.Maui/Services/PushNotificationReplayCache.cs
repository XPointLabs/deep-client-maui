using System.Text.Json;

namespace Deep.Client.Maui.Services;

public sealed class PushNotificationReplayCache(
    Func<string?> load,
    Action<string?> save,
    int maxEntries = 256)
{
    private readonly object gate = new();
    private readonly int capacity = maxEntries > 0
        ? maxEntries
        : throw new ArgumentOutOfRangeException(nameof(maxEntries));

    public bool TryAccept(
        string replayId,
        long expirationUnixSeconds,
        long nowUnixSeconds,
        Action? beforePersistAcceptance = null)
    {
        if (string.IsNullOrWhiteSpace(replayId) || replayId.Length != 64 ||
            !replayId.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            expirationUnixSeconds <= nowUnixSeconds)
        {
            return false;
        }

        lock (gate)
        {
            var entries = LoadEntries()
                .Where(entry => IsReplayId(entry.ReplayId) && entry.ExpirationUnixSeconds > nowUnixSeconds)
                .GroupBy(static entry => entry.ReplayId, StringComparer.Ordinal)
                .Select(static group => group.OrderByDescending(entry => entry.ExpirationUnixSeconds).First())
                .ToList();
            if (entries.Any(entry => string.Equals(entry.ReplayId, replayId, StringComparison.Ordinal)))
            {
                Persist(entries);
                return false;
            }

            beforePersistAcceptance?.Invoke();
            entries.Add(new ReplayEntry(replayId, expirationUnixSeconds));
            if (entries.Count > capacity)
            {
                entries = entries
                    .OrderByDescending(static entry => entry.ExpirationUnixSeconds)
                    .Take(capacity)
                    .ToList();
            }

            Persist(entries);
            return true;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            save(null);
        }
    }

    private IReadOnlyList<ReplayEntry> LoadEntries()
    {
        var json = load();
        if (string.IsNullOrWhiteSpace(json) || json.Length > 64 * 1024)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ReplayEntry>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void Persist(IReadOnlyList<ReplayEntry> entries) =>
        save(entries.Count == 0 ? null : JsonSerializer.Serialize(entries));

    private static bool IsReplayId(string? replayId) =>
        replayId is { Length: 64 } &&
        replayId.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record ReplayEntry(string ReplayId, long ExpirationUnixSeconds);
}

internal static class MauiPushNotificationReplayStore
{
    private const string ReplayCacheKey = "push.replay-cache.v1";
    private static readonly PushNotificationReplayCache Cache = new(
        () => Microsoft.Maui.Storage.Preferences.Default.Get(ReplayCacheKey, string.Empty),
        value =>
        {
            if (value is null)
            {
                Microsoft.Maui.Storage.Preferences.Default.Remove(ReplayCacheKey);
            }
            else
            {
                Microsoft.Maui.Storage.Preferences.Default.Set(ReplayCacheKey, value);
            }
        });

    public static bool TryAccept(
        string replayId,
        long expirationUnixSeconds,
        long nowUnixSeconds,
        Action? beforePersistAcceptance = null) =>
        Cache.TryAccept(replayId, expirationUnixSeconds, nowUnixSeconds, beforePersistAcceptance);

    public static void Clear() => Cache.Clear();
}
