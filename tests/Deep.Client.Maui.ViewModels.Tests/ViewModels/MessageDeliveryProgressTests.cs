using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Domain;

namespace Deep.Client.Maui.ViewModels.Tests.ViewModels;

public sealed class MessageDeliveryProgressTests
{
    private sealed record DeliveryRow(
        MessageId Id,
        MessageDeliveryState State,
        DateTimeOffset CreatedAt);

    [Theory]
    [InlineData(MessageDeliveryState.Sent, MessageDeliveryState.Draft)]
    [InlineData(MessageDeliveryState.Sent, MessageDeliveryState.Sending)]
    [InlineData(MessageDeliveryState.Sent, MessageDeliveryState.Failed)]
    [InlineData(MessageDeliveryState.Delivered, MessageDeliveryState.Sent)]
    [InlineData(MessageDeliveryState.Read, MessageDeliveryState.Delivered)]
    [InlineData(MessageDeliveryState.Read, MessageDeliveryState.Sending)]
    public void SuccessfulDeliveryCannotRegress(
        MessageDeliveryState current,
        MessageDeliveryState candidate) =>
        Assert.True(MessageDeliveryProgress.IsRegression(current, candidate));

    [Theory]
    [InlineData(MessageDeliveryState.Draft, MessageDeliveryState.Sending)]
    [InlineData(MessageDeliveryState.Sending, MessageDeliveryState.Failed)]
    [InlineData(MessageDeliveryState.Failed, MessageDeliveryState.Sending)]
    [InlineData(MessageDeliveryState.Sent, MessageDeliveryState.Delivered)]
    [InlineData(MessageDeliveryState.Delivered, MessageDeliveryState.Read)]
    [InlineData(MessageDeliveryState.Read, MessageDeliveryState.Read)]
    public void ForwardAndRetryTransitionsRemainAllowed(
        MessageDeliveryState current,
        MessageDeliveryState candidate) =>
        Assert.False(MessageDeliveryProgress.IsRegression(current, candidate));

    [Fact]
    public void DisjointRecentWindowPreservesOnlyCurrentLocallyQueuedMessage()
    {
        var start = DateTimeOffset.Parse("2026-08-25T00:00:00Z");
        var oldRow = new DeliveryRow(MessageId.NewId(), MessageDeliveryState.Read, start);
        var localRow = new DeliveryRow(MessageId.NewId(), MessageDeliveryState.Sent, start.AddMinutes(30));
        var current = new[] { oldRow, localRow };
        var candidate = Enumerable.Range(1, 20)
            .Select(index => new DeliveryRow(
                MessageId.NewId(),
                MessageDeliveryState.Delivered,
                start.AddMinutes(index)))
            .ToArray();

        var merged = MessageDeliveryProgress.MergeSnapshot(
            current,
            candidate,
            new HashSet<MessageId> { localRow.Id },
            static row => row.Id,
            static row => row.State,
            static row => row.CreatedAt);

        Assert.Equal(21, merged.Count);
        Assert.Contains(localRow, merged);
        Assert.DoesNotContain(oldRow, merged);
        Assert.Equal(localRow, merged[^1]);
    }

    [Fact]
    public void SnapshotKeepsNewerSuccessfulStateForMatchingMessage()
    {
        var id = MessageId.NewId();
        var createdAt = DateTimeOffset.Parse("2026-08-25T00:00:00Z");
        var current = new DeliveryRow(id, MessageDeliveryState.Sent, createdAt);
        var stale = current with { State = MessageDeliveryState.Sending };

        var merged = MessageDeliveryProgress.MergeSnapshot(
            [current],
            [stale],
            new HashSet<MessageId>(),
            static row => row.Id,
            static row => row.State,
            static row => row.CreatedAt);

        Assert.Equal(current, Assert.Single(merged));
    }
}
