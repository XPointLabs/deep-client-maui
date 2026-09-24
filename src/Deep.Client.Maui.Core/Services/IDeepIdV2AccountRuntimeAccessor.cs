using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Core.Services;

/// <summary>
/// Opens only the local DID2 account generation. No V1 identity, network
/// bootstrap or contact/message authority is implied by this contract.
/// </summary>
public interface IDeepIdV2AccountRuntimeAccessor : IAsyncDisposable
{
    Task<DeepIdV2AccountService> GetAccountsAsync(
        CancellationToken cancellationToken = default);
}
