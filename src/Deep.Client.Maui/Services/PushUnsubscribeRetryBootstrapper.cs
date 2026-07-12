using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Services;

public static class PushUnsubscribeRetryBootstrapper
{
    public static async Task<bool> TryRetryAsync(
        IServiceProvider? services,
        CancellationToken cancellationToken = default)
    {
        if (services?.GetService(typeof(ClientRuntimeBootstrapper)) is not ClientRuntimeBootstrapper bootstrapper)
        {
            return false;
        }

        await bootstrapper.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (services.GetService(typeof(IPushRegistrationCoordinator)) is not IPushUnsubscribeRetryCoordinator retryCoordinator)
        {
            return false;
        }

        return await retryCoordinator.RetryPendingUnsubscribeAsync(cancellationToken).ConfigureAwait(false);
    }
}
