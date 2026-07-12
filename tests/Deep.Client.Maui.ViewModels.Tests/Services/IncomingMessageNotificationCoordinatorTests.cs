using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;

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
            (_, _) => Task.FromResult<IReadOnlyList<MessageId>>([]),
            (_, _) =>
            {
                acknowledged++;
                return Task.CompletedTask;
            },
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
    public async Task PresentsOnceAndAcknowledgesEveryDurableBatch()
    {
        var queue = Enumerable.Range(0, 5)
            .Select(index => MessageId.Parse($"message-{index}"))
            .ToList();
        var presentationIds = new List<string>();
        var acknowledgedBatches = new List<MessageId[]>();
        var coordinator = new IncomingMessageNotificationCoordinator(
            (limit, _) => Task.FromResult<IReadOnlyList<MessageId>>(queue.Take(limit).ToArray()),
            (ids, _) =>
            {
                var batch = ids.ToArray();
                acknowledgedBatches.Add(batch);
                queue.RemoveAll(id => batch.Contains(id));
                return Task.CompletedTask;
            },
            (notificationId, _) =>
            {
                presentationIds.Add(notificationId);
                return Task.CompletedTask;
            },
            () => throw new InvalidOperationException("Successful presentation must not re-arm work."),
            batchLimit: 2);

        var count = await coordinator.PresentPendingAsync();

        Assert.Equal(5, count);
        Assert.Empty(queue);
        Assert.Single(presentationIds);
        Assert.Equal([2, 2, 1], acknowledgedBatches.Select(static batch => batch.Length).ToArray());
    }

    [Fact]
    public async Task AcknowledgeFailureRearmsAndRetryUsesTheSameNotificationId()
    {
        IReadOnlyList<MessageId> queue =
        [
            MessageId.Parse("incoming-b"),
            MessageId.Parse("incoming-a")
        ];
        var presentationIds = new List<string>();
        var rearmed = 0;
        var failAcknowledgement = true;
        IncomingMessageNotificationCoordinator CreateCoordinator() => new(
            (_, _) => Task.FromResult(queue),
            (_, _) =>
            {
                if (failAcknowledgement)
                {
                    throw new IOException("Injected durable acknowledgement failure.");
                }

                queue = [];
                return Task.CompletedTask;
            },
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
}
