using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Services;

/// <summary>
/// Physical-E2E diagnostic bridge. Registration is restricted to DEBUG physical
/// composition; production composition never creates or injects this tracker.
/// </summary>
internal sealed class PhysicalMailboxRouteUsageTracker :
    IMailboxDispatchRouteUsageObserver
{
    private readonly object gate = new();
    private readonly Dictionary<ConversationId, AttemptState> current = [];

    public event EventHandler<ConversationId>? Changed;

    public void Observe(MailboxDispatchRouteUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var changed = false;
        lock (gate)
        {
            if (usage.Outcome == MailboxDispatchRouteOutcome.Started)
            {
                current[usage.ConversationId] = new AttemptState(
                    usage.AttemptId,
                    EntryRouterId: null);
                changed = true;
            }
            else if (current.TryGetValue(usage.ConversationId, out var active) &&
                     active.AttemptId == usage.AttemptId)
            {
                current[usage.ConversationId] = new AttemptState(
                    usage.AttemptId,
                    usage.Outcome == MailboxDispatchRouteOutcome.Durable
                        ? Convert.ToHexStringLower(usage.EntryRouterId.Span)
                        : null);
                changed = true;
            }
        }

        if (changed)
            PublishChanged(usage.ConversationId);
    }

    public string? GetCurrentRouterId(ConversationId conversationId)
    {
        lock (gate)
        {
            return current.TryGetValue(conversationId, out var state)
                ? state.EntryRouterId
                : null;
        }
    }

    private void PublishChanged(ConversationId conversationId)
    {
        var handlers = Changed;
        if (handlers is null)
            return;
        foreach (EventHandler<ConversationId> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, conversationId);
            }
            catch
            {
                // A diagnostic UI subscriber must never affect message delivery.
            }
        }
    }

    private sealed record AttemptState(Guid AttemptId, string? EntryRouterId);
}
