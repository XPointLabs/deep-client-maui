using Deep.Client.Maui.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class PushLifecyclePrimitiveTests
{
    [Fact]
    public async Task PushTokenBridgeCompletesDelayedPlatformCallback()
    {
        PushTokenBridge.Clear();
        try
        {
            var token = PushTokenBridge.WaitForTokenAsync("apns", TimeSpan.FromSeconds(2));
            Assert.False(token.IsCompleted);

            PushTokenBridge.Set("apns", "delayed-token");

            Assert.Equal("delayed-token", await token);
        }
        finally
        {
            PushTokenBridge.Clear();
        }
    }

    [Fact]
    public async Task PushTokenBridgeHonorsCancellationWithoutPoisoningLaterCallbacks()
    {
        PushTokenBridge.Clear();
        using var cancellation = new CancellationTokenSource();
        var cancelled = PushTokenBridge.WaitForTokenAsync("apns", TimeSpan.FromSeconds(2), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelled);

        PushTokenBridge.Set("apns", "next-token");
        Assert.Equal("next-token", await PushTokenBridge.WaitForTokenAsync("apns", TimeSpan.FromSeconds(1)));
        PushTokenBridge.Clear();
    }

    [Fact]
    public void ReplayCachePersistsAcceptanceRejectsReplayAndPrunesExpiredEntries()
    {
        string? persisted = null;
        var cache = new PushNotificationReplayCache(() => persisted, value => persisted = value, maxEntries: 2);
        var first = new string('a', 64);
        var second = new string('b', 64);
        var third = new string('c', 64);

        Assert.True(cache.TryAccept(first, 200, 100));
        Assert.False(cache.TryAccept(first, 200, 100));
        Assert.True(cache.TryAccept(second, 201, 100));
        Assert.True(cache.TryAccept(third, 202, 100));

        var reloaded = new PushNotificationReplayCache(() => persisted, value => persisted = value, maxEntries: 2);
        Assert.False(reloaded.TryAccept(third, 202, 100));
        Assert.True(reloaded.TryAccept(first, 300, 250));
    }

    [Fact]
    public void ReplayCachePublishesPendingWorkBeforePersistingAcceptance()
    {
        string? persisted = null;
        var sequence = new List<string>();
        var cache = new PushNotificationReplayCache(
            () => persisted,
            value =>
            {
                sequence.Add("acceptance");
                persisted = value;
            });
        var replayId = new string('d', 64);

        Assert.True(cache.TryAccept(replayId, 200, 100, () => sequence.Add("pending-work")));
        Assert.Equal(["pending-work", "acceptance"], sequence);

        sequence.Clear();
        Assert.False(cache.TryAccept(replayId, 200, 100, () => sequence.Add("pending-work")));
        Assert.Equal(["acceptance"], sequence);
    }

    [Fact]
    public void ReplayCacheDoesNotPersistAcceptanceWhenPendingWorkCannotBePublished()
    {
        string? persisted = null;
        var cache = new PushNotificationReplayCache(() => persisted, value => persisted = value);
        var replayId = new string('e', 64);

        Assert.Throws<InvalidOperationException>(() =>
            cache.TryAccept(replayId, 200, 100, () => throw new InvalidOperationException("marker failed")));
        Assert.Null(persisted);
        Assert.True(cache.TryAccept(replayId, 200, 100));
    }
}
