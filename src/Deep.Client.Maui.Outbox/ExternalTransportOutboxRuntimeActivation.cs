using Deep.Client.Shared.Features;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Outbox;

public sealed record ExternalTransportOutboxRuntimeActivation(
    ClientFeatureFlags EffectiveFeatureFlags,
    ExternalTransportOutboxBootstrapStatus Status,
    IExternalTransportOutboxExecutor? Executor)
{
    public static async Task<ExternalTransportOutboxRuntimeActivation> ResolveAsync(
        ClientFeatureFlags requestedFeatureFlags,
        ProcessExternalTransportOutboxExecutorOptions? options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedFeatureFlags);
        var bootstrap = await ProcessExternalTransportOutboxExecutor.BootstrapAsync(
                requestedFeatureFlags.PersistentTransportOutboxEnabled,
                options,
                cancellationToken)
            .ConfigureAwait(false);
        if (bootstrap.IsReady)
        {
            return new(requestedFeatureFlags, bootstrap.Status, bootstrap.Executor);
        }

        return new(
            requestedFeatureFlags with { PersistentTransportOutboxEnabled = false },
            bootstrap.Status,
            null);
    }
}
