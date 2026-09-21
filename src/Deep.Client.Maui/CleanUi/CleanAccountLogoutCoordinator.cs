using Deep.Client.Maui.Core.Services;

namespace Deep.Client.Maui.CleanUi;

internal sealed class CleanAccountLogoutCoordinator(
    IDeepAccountRuntimeAccessor accountRuntime) : IAccountLogoutCoordinator
{
    public Task LogoutAsync(CancellationToken cancellationToken = default) =>
        accountRuntime.ResetLocalStateAsync(cancellationToken);
}
