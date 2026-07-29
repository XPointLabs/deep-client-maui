using Deep.Client.Shared.Persistence;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui.Services;

internal static class StartupLocalStateReset
{
    internal const string WipeLocalDataOnNextLaunchKey = "session.wipe-local-on-next-launch";

    internal static bool IsResetRequired(Exception exception) =>
        exception.GetType() == typeof(LocalStateResetRequiredException);

    // This method represents an already-confirmed destructive user action.
    // Callers must never invoke it as part of ordinary startup error handling.
    internal static bool TryRequestConfirmedReset(Exception exception)
    {
        if (!IsResetRequired(exception))
        {
            return false;
        }

        Preferences.Default.Set(WipeLocalDataOnNextLaunchKey, true);
        return true;
    }
}

internal sealed class StartupLocalStateResetContext
{
    private LocalStateResetRequiredException? failure;

    internal void Clear() => failure = null;

    internal void Capture(LocalStateResetRequiredException exception) =>
        failure = exception;

    internal bool TryRequestConfirmedReset() =>
        failure is not null &&
        StartupLocalStateReset.TryRequestConfirmedReset(failure);
}
