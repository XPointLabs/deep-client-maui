using Deep.Client.Maui.Core.Services;

namespace Deep.Client.Maui.Core.Navigation;

/// <summary>
/// Opens a direct-message runtime only from a verifier-minted ContactV1 target.
/// Implementations must return false when the new direct runtime is not active;
/// they must never reinterpret the opaque identifier as a legacy SessionId.
/// </summary>
public interface IVerifiedDirectConversationActivationTarget
{
    Task<bool> ActivateVerifiedDirectConversationAsync(
        VerifiedDirectConversationTarget target,
        CancellationToken cancellationToken = default);
}
