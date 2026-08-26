using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Services;
using Deep.Protocol.DeepExtension.PrivacyRouting;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class PhysicalMailboxRouteUsageTrackerTests
{
    [Fact]
    public void StartedAndTerminalOutcomesNeverReusePriorRoute()
    {
        var tracker = new PhysicalMailboxRouteUsageTracker(
            new PrivacyMailboxRouteDiagnostics());
        var firstAttempt = Guid.NewGuid();
        tracker.Observe(Usage(ConversationA, firstAttempt,
            MailboxDispatchRouteOutcome.Started));
        Assert.Null(tracker.GetCurrentRouterId(ConversationA));
        tracker.Observe(Usage(ConversationA, firstAttempt,
            MailboxDispatchRouteOutcome.Durable, RouterA));
        Assert.Equal(Convert.ToHexStringLower(RouterA),
            tracker.GetCurrentRouterId(ConversationA));

        foreach (var outcome in new[]
                 {
                     MailboxDispatchRouteOutcome.NoDispatch,
                     MailboxDispatchRouteOutcome.Failed,
                     MailboxDispatchRouteOutcome.Canceled
                 })
        {
            var retryAttempt = Guid.NewGuid();
            tracker.Observe(Usage(ConversationA, retryAttempt,
                MailboxDispatchRouteOutcome.Started));
            Assert.Null(tracker.GetCurrentRouterId(ConversationA));
            tracker.Observe(Usage(ConversationA, retryAttempt, outcome));
            Assert.Null(tracker.GetCurrentRouterId(ConversationA));
        }
    }

    [Fact]
    public void LateCompletionCannotReplaceNewerAttemptOrAnotherConversation()
    {
        var tracker = new PhysicalMailboxRouteUsageTracker(
            new PrivacyMailboxRouteDiagnostics());
        var oldA = Guid.NewGuid();
        var newA = Guid.NewGuid();
        var attemptB = Guid.NewGuid();
        tracker.Observe(Usage(ConversationA, oldA,
            MailboxDispatchRouteOutcome.Started));
        tracker.Observe(Usage(ConversationB, attemptB,
            MailboxDispatchRouteOutcome.Started));
        tracker.Observe(Usage(ConversationA, newA,
            MailboxDispatchRouteOutcome.Started));
        tracker.Observe(Usage(ConversationA, newA,
            MailboxDispatchRouteOutcome.Durable, RouterA));
        tracker.Observe(Usage(ConversationB, attemptB,
            MailboxDispatchRouteOutcome.Durable, RouterB));

        tracker.Observe(Usage(ConversationA, oldA,
            MailboxDispatchRouteOutcome.Durable, RouterB));
        tracker.Observe(Usage(ConversationA, oldA,
            MailboxDispatchRouteOutcome.Failed));

        Assert.Equal(Convert.ToHexStringLower(RouterA),
            tracker.GetCurrentRouterId(ConversationA));
        Assert.Equal(Convert.ToHexStringLower(RouterB),
            tracker.GetCurrentRouterId(ConversationB));
    }

    [Fact]
    public void SubscriberFailureIsContainedAndEventsRemainConversationScoped()
    {
        var tracker = new PhysicalMailboxRouteUsageTracker(
            new PrivacyMailboxRouteDiagnostics());
        var changed = new List<ConversationId>();
        tracker.Changed += (_, _) => throw new InvalidOperationException("diagnostic");
        tracker.Changed += (_, conversationId) => changed.Add(conversationId);

        var attempt = Guid.NewGuid();
        tracker.Observe(Usage(ConversationB, attempt,
            MailboxDispatchRouteOutcome.Started));
        tracker.Observe(Usage(ConversationB, attempt,
            MailboxDispatchRouteOutcome.Durable, RouterB));

        Assert.Equal([ConversationB, ConversationB], changed);
        Assert.Null(tracker.GetCurrentRouterId(ConversationA));
    }

    [Fact]
    public void DurableHistoryPreservesBothRoutesUntilExplicitConversationReset()
    {
        var diagnostics = CreateDiagnostics();
        var tracker = new PhysicalMailboxRouteUsageTracker(diagnostics);
        var fallbackAttempt = Guid.NewGuid();
        var primaryAttempt = Guid.NewGuid();
        tracker.Observe(Usage(ConversationA, fallbackAttempt,
            MailboxDispatchRouteOutcome.Started));
        diagnostics.ObserveSelection(
            PrivacyMailboxRouteSelection.Fallback, EntryB);
        tracker.Observe(Usage(ConversationA, fallbackAttempt,
            MailboxDispatchRouteOutcome.Durable, RouterB));
        tracker.Observe(Usage(ConversationA, primaryAttempt,
            MailboxDispatchRouteOutcome.Started));
        diagnostics.ObserveSelection(
            PrivacyMailboxRouteSelection.Primary, EntryA);
        tracker.Observe(Usage(ConversationA, primaryAttempt,
            MailboxDispatchRouteOutcome.Durable, RouterA));

        Assert.Equal(
            [Convert.ToHexStringLower(EntryA), Convert.ToHexStringLower(EntryB)],
            tracker.GetObservedRouterIds(ConversationA));

        tracker.Reset(ConversationA);

        Assert.Empty(tracker.GetObservedRouterIds(ConversationA));
        Assert.Null(tracker.GetCurrentRouterId(ConversationA));
    }

    private static PrivacyMailboxRouteDiagnostics CreateDiagnostics()
    {
        var diagnostics = new PrivacyMailboxRouteDiagnostics();
        diagnostics.Publish(new MailboxPrivacyRouteSet(
            Route("primary", EntryA, RouterA, 0x31),
            Route("fallback", EntryB, RouterB, 0x71)));
        return diagnostics;
    }

    private static PrivacyMailboxRoute Route(
        string host, byte[] entry, byte[] coordinator, byte keySeed) => new(
        new Uri($"https://{host}.example:443/"),
        [
            new PrivacyRoutingHop(entry, Bytes(0x11, keySeed)),
            new PrivacyRoutingHop(Bytes(0x21, keySeed), Bytes(0x31, keySeed)),
            new PrivacyRoutingHop(coordinator, Bytes(0x41, keySeed))
        ]);

    private static byte[] Bytes(byte value, byte offset) =>
        Enumerable.Repeat(unchecked((byte)(value + offset)), 32).ToArray();

    private static MailboxDispatchRouteUsage Usage(
        ConversationId conversationId,
        Guid attemptId,
        MailboxDispatchRouteOutcome outcome,
        byte[]? routerId = null) => new(
        new MessageId($"message-{attemptId:N}"),
        conversationId,
        new SessionId(conversationId.Value),
        attemptId,
        outcome,
        routerId ?? []);

    private static ConversationId ConversationA { get; } =
        ConversationId.ForOneToOne(SessionId.Parse(
            "051111111111111111111111111111111111111111111111111111111111111111"));

    private static ConversationId ConversationB { get; } =
        ConversationId.ForOneToOne(SessionId.Parse(
            "052222222222222222222222222222222222222222222222222222222222222222"));

    private static byte[] RouterA { get; } =
        Enumerable.Repeat((byte)0xa1, 32).ToArray();

    private static byte[] RouterB { get; } =
        Enumerable.Repeat((byte)0xb2, 32).ToArray();

    private static byte[] EntryA { get; } =
        Enumerable.Repeat((byte)0xc3, 32).ToArray();

    private static byte[] EntryB { get; } =
        Enumerable.Repeat((byte)0xd4, 32).ToArray();
}
