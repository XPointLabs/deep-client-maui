using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class IncomingMessageNotificationCoordinatorTests
{
    [Fact]
    public async Task EmptyQueueDoesNotPresentOrAcknowledgeNotification()
    {
        var presented = 0;
        var acknowledged = 0;
        var rearmed = 0;
        var coordinator = new IncomingMessageNotificationCoordinator(
            (_, _) => Task.FromResult<IReadOnlyList<PendingIncomingMessageNotification>>([]),
            (_, _) =>
            {
                acknowledged++;
                return Task.CompletedTask;
            },
            new ActiveConversationTracker(),
            (_, _) =>
            {
                presented++;
                return Task.CompletedTask;
            },
            () => rearmed++);

        var count = await coordinator.PresentPendingAsync();

        Assert.Equal(0, count);
        Assert.Equal(0, presented);
        Assert.Equal(0, acknowledged);
        Assert.Equal(0, rearmed);
    }

    [Fact]
    public async Task BackgroundAlwaysPresentsEvenWhenConversationIsActive()
    {
        var conversationId = NewConversationId();
        var tracker = new ActiveConversationTracker();
        using var active = tracker.ActivateConversation(conversationId);
        var queue = new List<PendingIncomingMessageNotification>
        {
            Notification("incoming", conversationId)
        };
        var presentations = 0;
        var coordinator = CreateQueueCoordinator(queue, tracker, _ => presentations++);

        var count = await coordinator.PresentPendingAsync();

        Assert.Equal(1, count);
        Assert.Equal(1, presentations);
        Assert.Empty(queue);
    }

    [Fact]
    public async Task BackgroundTransitionDuringQueueReadForcesPresentation()
    {
        var conversationId = NewConversationId();
        var tracker = new ActiveConversationTracker();
        tracker.SetApplicationForeground(true);
        using var active = tracker.ActivateConversation(conversationId);
        var pending = Notification("incoming", conversationId);
        var listStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseList = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var presentations = 0;
        var acknowledged = new List<MessageId>();
        var coordinator = new IncomingMessageNotificationCoordinator(
            async (_, _) =>
            {
                listStarted.SetResult();
                await releaseList.Task;
                return [pending];
            },
            (ids, _) =>
            {
                acknowledged.AddRange(ids);
                return Task.CompletedTask;
            },
            tracker,
            (_, _) =>
            {
                presentations++;
                return Task.CompletedTask;
            },
            () => throw new InvalidOperationException("Successful processing must not re-arm work."));

        var processing = coordinator.PresentPendingAsync();
        await listStarted.Task;
        tracker.SetApplicationForeground(false);
        releaseList.SetResult();

        Assert.Equal(1, await processing);
        Assert.Equal(1, presentations);
        Assert.Equal([pending.MessageId], acknowledged);
    }

    [Fact]
    public async Task ForegroundActiveConversationStaysDurableUntilItCanBePresentedOrMarkedRead()
    {
        var conversationId = NewConversationId();
        var tracker = new ActiveConversationTracker();
        tracker.SetApplicationForeground(true);
        using var active = tracker.ActivateConversation(conversationId);
        var queue = new List<PendingIncomingMessageNotification>
        {
            Notification("incoming", conversationId)
        };
        var presentations = 0;
        var coordinator = CreateQueueCoordinator(queue, tracker, _ => presentations++);

        var count = await coordinator.PresentPendingAsync();

        Assert.Equal(0, count);
        Assert.Equal(0, presentations);
        Assert.Equal([Notification("incoming", conversationId)], queue);

        tracker.SetApplicationForeground(false);
        Assert.Equal(1, await coordinator.PresentPendingAsync());
        Assert.Equal(1, presentations);
        Assert.Empty(queue);
    }

    [Fact]
    public async Task MixedBatchSuppressesOnlyTheActiveConversation()
    {
        var activeConversation = NewConversationId();
        var otherConversation = NewConversationId();
        var activeMessage = Notification("active-message", activeConversation);
        var otherMessage = Notification("other-message", otherConversation);
        var tracker = new ActiveConversationTracker();
        tracker.SetApplicationForeground(true);
        using var active = tracker.ActivateConversation(activeConversation);
        var queue = new List<PendingIncomingMessageNotification> { activeMessage, otherMessage };
        var presentationIds = new List<string>();
        var acknowledgedBatches = new List<MessageId[]>();
        var coordinator = CreateQueueCoordinator(
            queue,
            tracker,
            presentationIds.Add,
            acknowledgedBatches);

        var count = await coordinator.PresentPendingAsync();

        Assert.Equal(1, count);
        Assert.Equal([activeMessage], queue);
        Assert.Equal(
            [[otherMessage.MessageId]],
            acknowledgedBatches);
        Assert.Equal(
            [IncomingMessageNotificationCoordinator.CreateNotificationBatchId([otherMessage.MessageId])],
            presentationIds);
    }

    [Fact]
    public async Task PresentsOnceAndAcknowledgesEveryDurableBatch()
    {
        var conversationId = NewConversationId();
        var queue = Enumerable.Range(0, 5)
            .Select(index => Notification($"message-{index}", conversationId))
            .ToList();
        var presentationIds = new List<string>();
        var acknowledgedBatches = new List<MessageId[]>();
        var coordinator = CreateQueueCoordinator(
            queue,
            new ActiveConversationTracker(),
            presentationIds.Add,
            acknowledgedBatches,
            batchLimit: 2);

        var count = await coordinator.PresentPendingAsync();

        Assert.Equal(5, count);
        Assert.Empty(queue);
        Assert.Single(presentationIds);
        Assert.Equal([2, 2, 1], acknowledgedBatches.Select(static batch => batch.Length).ToArray());
    }

    [Fact]
    public async Task PresentationFailureLeavesVisibleMessagesPendingButKeepsSuppressionAcknowledged()
    {
        var activeConversation = NewConversationId();
        var otherConversation = NewConversationId();
        var suppressed = Notification("suppressed", activeConversation);
        var visible = Notification("visible", otherConversation);
        var tracker = new ActiveConversationTracker();
        tracker.SetApplicationForeground(true);
        using var active = tracker.ActivateConversation(activeConversation);
        var queue = new List<PendingIncomingMessageNotification> { suppressed, visible };
        var rearmed = 0;
        var coordinator = CreateQueueCoordinator(
            queue,
            tracker,
            _ => throw new IOException("Injected presentation failure."),
            rearm: () => rearmed++);

        await Assert.ThrowsAsync<IOException>(() => coordinator.PresentPendingAsync());

        Assert.Equal([suppressed, visible], queue);
        Assert.Equal(1, rearmed);
    }

    [Fact]
    public async Task AcknowledgeFailureRearmsAndRetryUsesTheSameNotificationId()
    {
        var conversationId = NewConversationId();
        var queue = new List<PendingIncomingMessageNotification>
        {
            Notification("incoming-b", conversationId),
            Notification("incoming-a", conversationId)
        };
        var presentationIds = new List<string>();
        var rearmed = 0;
        var failAcknowledgement = true;
        IncomingMessageNotificationCoordinator CreateCoordinator() => new(
            (_, _) => Task.FromResult<IReadOnlyList<PendingIncomingMessageNotification>>(queue.ToArray()),
            (ids, _) =>
            {
                if (failAcknowledgement)
                {
                    throw new IOException("Injected durable acknowledgement failure.");
                }

                queue.RemoveAll(item => ids.Contains(item.MessageId));
                return Task.CompletedTask;
            },
            new ActiveConversationTracker(),
            (notificationId, _) =>
            {
                presentationIds.Add(notificationId);
                return Task.CompletedTask;
            },
            () => rearmed++);

        await Assert.ThrowsAsync<IOException>(() => CreateCoordinator().PresentPendingAsync());
        failAcknowledgement = false;
        Assert.Equal(2, await CreateCoordinator().PresentPendingAsync());

        Assert.Equal(1, rearmed);
        Assert.Equal(2, presentationIds.Count);
        Assert.Equal(presentationIds[0], presentationIds[1]);
        Assert.Equal(
            IncomingMessageNotificationCoordinator.CreateNotificationBatchId(
                [MessageId.Parse("incoming-a"), MessageId.Parse("incoming-b")]),
            presentationIds[0]);
    }

    private static IncomingMessageNotificationCoordinator CreateQueueCoordinator(
        List<PendingIncomingMessageNotification> queue,
        IActiveConversationTracker tracker,
        Action<string> present,
        List<MessageId[]>? acknowledgedBatches = null,
        Action? rearm = null,
        int batchLimit = IncomingMessageNotificationLimits.MaxBatchCount) =>
        new(
            (limit, _) => Task.FromResult<IReadOnlyList<PendingIncomingMessageNotification>>(
                queue.Take(limit).ToArray()),
            (ids, _) =>
            {
                var batch = ids.ToArray();
                acknowledgedBatches?.Add(batch);
                queue.RemoveAll(item => batch.Contains(item.MessageId));
                return Task.CompletedTask;
            },
            tracker,
            (notificationId, _) =>
            {
                present(notificationId);
                return Task.CompletedTask;
            },
            rearm ?? (() => throw new InvalidOperationException("Successful processing must not re-arm work.")),
            batchLimit);

    private static PendingIncomingMessageNotification Notification(
        string messageId,
        ConversationId conversationId) =>
        new(MessageId.Parse(messageId), conversationId);

    private static ConversationId NewConversationId() =>
        ConversationId.ForOneToOne(SessionId.CreateNew());
}
