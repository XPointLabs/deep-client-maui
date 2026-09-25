using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Core.Services;

/// <summary>
/// Diagnostic DID2 peer lookup. Success authenticates a current directory
/// entry; it does not accept a relationship or enable messaging.
/// </summary>
public interface IDeepIdV2ContactDiscovery
{
    Task VerifyAsync(DeepIdV2AccountService accounts,
        string compactDescriptor, string exactDid2Hex,
        CancellationToken cancellationToken = default);
}
