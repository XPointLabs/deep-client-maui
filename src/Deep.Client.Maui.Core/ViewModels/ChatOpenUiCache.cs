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
    bool HasOlderMessages,
    DateTimeOffset CachedAt);

public sealed class ChatOpenUiCache
{
    private const int MaxEntries = 32;
    private readonly object gate = new();
    private readonly Dictionary<string, ChatOpenUiSnapshot> oneToOne = new(StringComparer.Ordinal);

    public bool TryGet(SessionId counterpart, out ChatOpenUiSnapshot snapshot)
    {
        lock (gate)
        {
            return oneToOne.TryGetValue(counterpart.Value, out snapshot!);
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
}
