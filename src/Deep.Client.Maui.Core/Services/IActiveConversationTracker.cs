using Deep.Client.Shared.Domain;

namespace Deep.Client.Maui.Core.Services;

public readonly record struct ActiveConversationState(
    bool IsApplicationForeground,
    ConversationId? ConversationId)
{
    public bool ShouldSuppressNotification(ConversationId conversationId) =>
        IsApplicationForeground && ConversationId == conversationId;
}

public interface IActiveConversationTracker
{
    ActiveConversationState Snapshot { get; }

    IDisposable ActivateConversation(ConversationId conversationId);

    void SetApplicationForeground(bool isForeground);
}

public sealed class ActiveConversationTracker : IActiveConversationTracker
{
    private readonly object gate = new();
    private ActiveConversationState state;
    private long activeRegistrationId;
    private long nextRegistrationId;

    public ActiveConversationState Snapshot
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    public IDisposable ActivateConversation(ConversationId conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId.Value))
        {
            throw new ArgumentException("Conversation ID must not be empty.", nameof(conversationId));
        }

        long registrationId;
        lock (gate)
        {
            registrationId = ++nextRegistrationId;
            activeRegistrationId = registrationId;
            state = state with { ConversationId = conversationId };
        }

        return new Registration(this, registrationId);
    }

    public void SetApplicationForeground(bool isForeground)
    {
        lock (gate)
        {
            state = state with { IsApplicationForeground = isForeground };
        }
    }

    private void Deactivate(long registrationId)
    {
        lock (gate)
        {
            if (activeRegistrationId != registrationId)
            {
                return;
            }

            activeRegistrationId = 0;
            state = state with { ConversationId = null };
        }
    }

    private sealed class Registration(ActiveConversationTracker owner, long registrationId) : IDisposable
    {
        private ActiveConversationTracker? owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref owner, null)?.Deactivate(registrationId);
    }
}
