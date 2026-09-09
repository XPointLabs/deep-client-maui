using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Core.Services;

/// <summary>
/// Opens and owns the local Deep account runtime without composing any network services.
/// </summary>
public interface IDeepAccountRuntimeAccessor : IAsyncDisposable
{
    Task<DeepAccountService> GetAccountsAsync(CancellationToken cancellationToken = default);

    Task ResetLocalStateAsync(CancellationToken cancellationToken = default);
}
