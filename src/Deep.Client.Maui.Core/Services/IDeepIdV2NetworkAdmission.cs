using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Core.Services;

/// <summary>
/// One DID2-only admission and independently verified directory proof.
/// HTTPS UAT additionally verifies the signed network closure and publishes the
/// protected initial inventory through both ONION replicas before recording XIC1.
/// This grants no contact, message, attachment or group transport.
/// </summary>
public interface IDeepIdV2NetworkAdmission
{
    Task VerifyAsync(DeepIdV2AccountService accounts,
        CancellationToken cancellationToken = default);
}
