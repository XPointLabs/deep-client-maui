using Deep.Client.Shared.Domain;

namespace Deep.Client.Maui.Core.ViewModels;

public sealed record ChatOpenUiSnapshot(
    SessionAccount ActiveAccount,
    SessionId Counterpart,
    Conversation Conversation,
    bool IsBlocked,
    bool IsMessageRequest,
    IReadOnlyList<ChatMessageItem> Messages,
    DateTimeOffset? OldestLoadedMessageAt,
    MessageId? OldestLoadedMessageId,
    bool HasOlderMessages,
    DateTimeOffset CachedAt);

public sealed class ChatOpenUiCache
{
    private const int MaxEntries = 32;
    private static readonly TimeSpan EntryTtl = TimeSpan.FromMinutes(2);
    private readonly object gate = new();
    private readonly Dictionary<string, ChatOpenUiSnapshot> oneToOne = new(StringComparer.Ordinal);

    public bool TryGet(SessionId counterpart, DateTimeOffset now, out ChatOpenUiSnapshot snapshot)
    {
        lock (gate)
        {
            if (!oneToOne.TryGetValue(counterpart.Value, out snapshot!)
                || now < snapshot.CachedAt
                || now - snapshot.CachedAt > EntryTtl)
            {
                oneToOne.Remove(counterpart.Value);
                snapshot = null!;
                return false;
            }

            var liveMessages = snapshot.Messages
                .Where(message => message.ExpiresAt is null || message.ExpiresAt > now)
                .ToArray();
            if (liveMessages.Length != snapshot.Messages.Count)
            {
                snapshot = snapshot with { Messages = liveMessages };
                oneToOne[counterpart.Value] = snapshot;
            }

            return true;
        }
    }

    public void Store(ChatOpenUiSnapshot snapshot)
    {
        lock (gate)
        {
            oneToOne[snapshot.Counterpart.Value] = snapshot;
            if (oneToOne.Count <= MaxEntries)
            {
                return;
            }

            var oldest = oneToOne
                .OrderBy(static item => item.Value.CachedAt)
                .First()
                .Key;
            oneToOne.Remove(oldest);
        }
    }

    public void Remove(SessionId counterpart)
    {
        lock (gate)
        {
            oneToOne.Remove(counterpart.Value);
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            oneToOne.Clear();
        }
    }
}
