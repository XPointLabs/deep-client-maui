using Deep.Client.Maui.Services;
using Deep.Client.Shared.Persistence;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class StartupLocalStateResetTests
{
    [Fact]
    public void ResetEligibilityMatchesOnlyTheTypedResetRequiredFailure()
    {
        var typed = new LocalStateResetRequiredException(
            LocalStateResetRequiredReason.UnreadableOrWrongKey,
            "Reset required.");

        Assert.True(StartupLocalStateReset.IsResetRequired(typed));
        Assert.False(StartupLocalStateReset.IsResetRequired(new InvalidOperationException()));
        Assert.False(StartupLocalStateReset.IsResetRequired(new OperationCanceledException()));
        Assert.False(StartupLocalStateReset.IsResetRequired(
            new InvalidOperationException("outer", typed)));
    }

    [Fact]
    public void ConfirmedTypedResetSetsTheRetryableWipeFlag()
    {
        Preferences.Default.Remove(StartupLocalStateReset.WipeLocalDataOnNextLaunchKey);
        try
        {
            var typed = new LocalStateResetRequiredException(
                LocalStateResetRequiredReason.UnsupportedVersion,
                "Reset required.");

            Assert.False(Preferences.Default.Get(
                StartupLocalStateReset.WipeLocalDataOnNextLaunchKey,
                false));
            Assert.True(StartupLocalStateReset.TryRequestConfirmedReset(typed));
            Assert.True(Preferences.Default.Get(
                StartupLocalStateReset.WipeLocalDataOnNextLaunchKey,
                false));
        }
        finally
        {
            Preferences.Default.Remove(StartupLocalStateReset.WipeLocalDataOnNextLaunchKey);
        }
    }

    [Fact]
    public void CapturedPostBootstrapTypedFailureCanAuthorizeConfirmedReset()
    {
        Preferences.Default.Remove(StartupLocalStateReset.WipeLocalDataOnNextLaunchKey);
        try
        {
            var context = new StartupLocalStateResetContext();
            context.Capture(new LocalStateResetRequiredException(
                LocalStateResetRequiredReason.InvalidCurrentSchema,
                "Reset required."));

            Assert.True(context.TryRequestConfirmedReset());
            Assert.True(Preferences.Default.Get(
                StartupLocalStateReset.WipeLocalDataOnNextLaunchKey,
                false));
        }
        finally
        {
            Preferences.Default.Remove(StartupLocalStateReset.WipeLocalDataOnNextLaunchKey);
        }
    }

    [Fact]
    public void ClearedContextCannotAuthorizeAReset()
    {
        var context = new StartupLocalStateResetContext();
        context.Capture(new LocalStateResetRequiredException(
            LocalStateResetRequiredReason.InvalidCurrentSchema,
            "Reset required."));
        context.Clear();

        Assert.False(context.TryRequestConfirmedReset());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryAndCancellationFailuresCannotSetTheWipeFlag(bool cancelled)
    {
        Preferences.Default.Remove(StartupLocalStateReset.WipeLocalDataOnNextLaunchKey);
        try
        {
            Exception exception = cancelled
                ? new OperationCanceledException()
                : new IOException("Disk is busy.");

            Assert.False(StartupLocalStateReset.TryRequestConfirmedReset(exception));
            Assert.False(Preferences.Default.Get(
                StartupLocalStateReset.WipeLocalDataOnNextLaunchKey,
                false));
        }
        finally
        {
            Preferences.Default.Remove(StartupLocalStateReset.WipeLocalDataOnNextLaunchKey);
        }
    }
}
