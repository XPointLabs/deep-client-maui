using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Services;

public static class PushUnsubscribeRetryBootstrapper
{
    public static async Task<bool> TryRetryAsync(
        IServiceProvider? services,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask.ConfigureAwait(false);
        // A future Deep messaging runtime must expose a local pending-unsubscribe
        // preflight before any network composition is allowed here.
        return false;
    }
}
