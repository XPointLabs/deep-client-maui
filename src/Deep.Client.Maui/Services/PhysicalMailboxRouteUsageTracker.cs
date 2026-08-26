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
    private readonly PrivacyMailboxRouteDiagnostics routeDiagnostics;
    private readonly object gate = new();
    private readonly Dictionary<ConversationId, AttemptState> current = [];
    private readonly Dictionary<ConversationId, HashSet<string>> observedDurableRouters = [];
    private readonly Dictionary<ConversationId, Dictionary<string, string>>
        observedDurableCoordinators = [];

    public event EventHandler<ConversationId>? Changed;

    public PhysicalMailboxRouteUsageTracker(
        PrivacyMailboxRouteDiagnostics routeDiagnostics)
    {
        this.routeDiagnostics = routeDiagnostics ??
            throw new ArgumentNullException(nameof(routeDiagnostics));
    }

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
                var entryRouterId = usage.Outcome == MailboxDispatchRouteOutcome.Durable
                    ? Convert.ToHexStringLower(usage.EntryRouterId.Span)
                    : null;
                current[usage.ConversationId] = new AttemptState(
                    usage.AttemptId,
                    entryRouterId);
                if (entryRouterId is not null)
                {
                    var durableEntryRouterId = ResolveDurableEntryRouterId(
                        usage.EntryRouterId.Span);
                    if (durableEntryRouterId is not null)
                    {
                        if (!observedDurableRouters.TryGetValue(
                                usage.ConversationId, out var routers))
                        {
                            routers = new HashSet<string>(StringComparer.Ordinal);
                            observedDurableRouters.Add(usage.ConversationId, routers);
                        }
                        routers.Add(durableEntryRouterId);
                        if (!observedDurableCoordinators.TryGetValue(
                                usage.ConversationId, out var coordinators))
                        {
                            coordinators = new Dictionary<string, string>(
                                StringComparer.Ordinal);
                            observedDurableCoordinators.Add(
                                usage.ConversationId, coordinators);
                        }
                        coordinators[durableEntryRouterId] =
                            Convert.ToHexStringLower(usage.EntryRouterId.Span);
                    }
                }
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

    public IReadOnlyList<string> GetObservedRouterIds(
        ConversationId conversationId)
    {
        lock (gate)
        {
            return observedDurableRouters.TryGetValue(conversationId, out var routers)
                ? routers.Order(StringComparer.Ordinal).ToArray()
                : [];
        }
    }

    public string? GetObservedCoordinatorId(
        ConversationId conversationId,
        string entryRouterId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryRouterId);
        lock (gate)
        {
            return observedDurableCoordinators.TryGetValue(
                       conversationId, out var coordinators) &&
                   coordinators.TryGetValue(entryRouterId, out var coordinator)
                ? coordinator
                : null;
        }
    }

    public void Reset(ConversationId conversationId)
    {
        lock (gate)
        {
            current.Remove(conversationId);
            observedDurableRouters.Remove(conversationId);
            observedDurableCoordinators.Remove(conversationId);
        }
        PublishChanged(conversationId);
    }

    private string? ResolveDurableEntryRouterId(
        ReadOnlySpan<byte> authenticatedCoordinatorId)
    {
        var routes = routeDiagnostics.Current;
        var selection = routeDiagnostics.CurrentSelection;
        if (routes is null || selection is null ||
            authenticatedCoordinatorId.Length != 32 ||
            authenticatedCoordinatorId.IndexOfAnyExcept((byte)0) < 0)
            return null;
        var selected = selection.Route == "primary"
            ? routes.Primary
            : selection.Route == "fallback"
                ? routes.Fallback
                : null;
        if (selected is null || selected.Count != 3)
            return null;

        // The selected privacy exit and the authenticated mailbox coordinator
        // are intentionally independent identities. This physical lane binds
        // the sole authority to the primary route's xnode1 terminal, while the
        // fallback route terminates at forwarding-only xnode2.
        var coordinator = Convert.ToHexStringLower(authenticatedCoordinatorId);
        return string.Equals(selected[0].RouterId, selection.EntryRouterId,
                   StringComparison.Ordinal) &&
               string.Equals(routes.Primary[^1].RouterId, coordinator,
                   StringComparison.Ordinal)
            ? selection.EntryRouterId
            : null;
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
