using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Protocol.Identity;

namespace Deep.Client.Maui.Core.Services;

/// <summary>Account-bound business commands only. No protocol events, private
/// keys, routes, caller proofs or acknowledgement inputs cross this boundary.</summary>
public interface IDeepIdV2ConversationRuntime
{
    Task<DeepIdV2ContactStartResult> StartAsync(DeepIdV2AccountService accounts, DeepPermanentIdV2 address,
        ReadOnlyMemory<byte> intent, CancellationToken ct = default);
    Task<IReadOnlyList<DeepIdV2ConversationSnapshot>> ListAsync(DeepIdV2AccountService accounts, CancellationToken ct = default);
    Task AcceptAsync(DeepIdV2AccountService accounts, DeepIdV2Conversation conversation, ReadOnlyMemory<byte> operation, CancellationToken ct = default);
    Task SendTextAsync(DeepIdV2AccountService accounts, DeepIdV2Conversation conversation,
        ReadOnlyMemory<byte> operation, string text, CancellationToken ct = default);
    Task<IReadOnlyList<DirectMessageCreateSnapshot>> MessagesAsync(DeepIdV2AccountService accounts,
        DeepIdV2Conversation conversation, CancellationToken ct = default);
    Task<DeepIdV2MailboxSynchronizationResult> SynchronizeAsync(DeepIdV2AccountService accounts, CancellationToken ct = default);
}
