using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Domain;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class ActiveConversationTrackerTests
{
    [Fact]
    public void SuppressesOnlyTheActiveConversationWhileForeground()
    {
        var tracker = new ActiveConversationTracker();
        var active = ConversationId.ForOneToOne(SessionId.CreateNew());
        var other = ConversationId.ForOneToOne(SessionId.CreateNew());

        using var registration = tracker.ActivateConversation(active);
        Assert.False(tracker.Snapshot.ShouldSuppressNotification(active));

        tracker.SetApplicationForeground(true);

        Assert.True(tracker.Snapshot.ShouldSuppressNotification(active));
        Assert.False(tracker.Snapshot.ShouldSuppressNotification(other));

        tracker.SetApplicationForeground(false);
        Assert.False(tracker.Snapshot.ShouldSuppressNotification(active));
    }

    [Fact]
    public void StaleRegistrationCannotClearANewerActivationOfTheSameConversation()
    {
        var tracker = new ActiveConversationTracker();
        var conversationId = ConversationId.ForOneToOne(SessionId.CreateNew());
        var stale = tracker.ActivateConversation(conversationId);
        var current = tracker.ActivateConversation(conversationId);

        stale.Dispose();

        Assert.Equal(conversationId, tracker.Snapshot.ConversationId);
        current.Dispose();
        Assert.Null(tracker.Snapshot.ConversationId);
    }

    [Fact]
    public async Task ConcurrentLifecycleAndPageUpdatesLeaveAConsistentSnapshot()
    {
        var tracker = new ActiveConversationTracker();
        var conversationIds = Enumerable.Range(0, 64)
            .Select(_ => ConversationId.ForOneToOne(SessionId.CreateNew()))
            .ToArray();

        await Task.WhenAll(conversationIds.Select((conversationId, index) => Task.Run(() =>
        {
            using var registration = tracker.ActivateConversation(conversationId);
            tracker.SetApplicationForeground(index % 2 == 0);
            var snapshot = tracker.Snapshot;
            if (snapshot.ConversationId is { } activeConversation)
            {
                _ = snapshot.ShouldSuppressNotification(activeConversation);
            }
        })));

        Assert.Null(tracker.Snapshot.ConversationId);
    }
}
