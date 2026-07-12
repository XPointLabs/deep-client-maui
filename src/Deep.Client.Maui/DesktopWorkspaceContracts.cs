using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Domain;

namespace Deep.Client.Maui;

public interface IConversationActivationTarget
{
    Task<bool> ActivateConversationAsync(
        ConversationId conversationId,
        CancellationToken cancellationToken = default);
}

public interface IActiveComposerProvider
{
    bool TryGetActiveComposer(out ChatViewModel? chat, out GroupChatViewModel? group);
}
