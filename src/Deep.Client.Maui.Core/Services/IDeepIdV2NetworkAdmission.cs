using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Core.Services;

/// <summary>
/// One DID2-only admission and independently verified directory proof.
/// This grants no contact, message, attachment or group transport.
/// </summary>
public interface IDeepIdV2NetworkAdmission
{
    Task VerifyAsync(DeepIdV2AccountService accounts,
        CancellationToken cancellationToken = default);
}
