using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Persistence;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class StartupLocalStateResetTests
{
    [Fact]
    public void ResetEligibilityMatchesOnlyTheTypedResetRequiredFailure()
    {
        var localState = new LocalStateResetRequiredException(
            LocalStateResetRequiredReason.UnreadableOrWrongKey,
            "Reset required.");
        var identity = new ProtectedIdentityResetRequiredException(
            ProtectedIdentityResetRequiredReason.Missing,
            "Reset required.");

        Assert.True(StartupLocalStateReset.IsResetRequired(localState));
        Assert.True(StartupLocalStateReset.IsResetRequired(identity));
        Assert.False(StartupLocalStateReset.IsResetRequired(new InvalidOperationException()));
        Assert.False(StartupLocalStateReset.IsResetRequired(new OperationCanceledException()));
        Assert.False(StartupLocalStateReset.IsResetRequired(
            new InvalidOperationException("outer", localState)));
    }

    [Fact]
    public void DatabaseResetReasonsHaveDistinctSafePresentations()
    {
        var incompatible = StartupLocalStateReset.ToUserPresentation(
            new LocalStateResetRequiredException(
                LocalStateResetRequiredReason.UnsupportedVersion,
                "sensitive-incompatible-detail"));
        var damaged = StartupLocalStateReset.ToUserPresentation(
            new LocalStateResetRequiredException(
                LocalStateResetRequiredReason.InvalidCurrentSchema,
                "sensitive-corruption-detail"));
        var unreadable = StartupLocalStateReset.ToUserPresentation(
            new LocalStateResetRequiredException(
                LocalStateResetRequiredReason.UnreadableOrWrongKey,
                "sensitive-key-detail"));

        Assert.Equal("local-state-incompatible-version", incompatible.Code);
        Assert.Equal("local-state-damaged", damaged.Code);
        Assert.Equal("local-state-unreadable", unreadable.Code);
        Assert.NotEqual(incompatible.Status, damaged.Status);
        Assert.NotEqual(damaged.Status, unreadable.Status);
        Assert.DoesNotContain("sensitive", incompatible.Guidance, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive", damaged.Guidance, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive", unreadable.Guidance, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ProtectedIdentityResetRequiredReason.Missing, "protected-identity-missing")]
    [InlineData(ProtectedIdentityResetRequiredReason.Incompatible, "protected-identity-incompatible")]
    [InlineData(ProtectedIdentityResetRequiredReason.AccountMismatch, "protected-identity-account-mismatch")]
    public void ProtectedIdentityReasonsHaveDistinctSafePresentations(
        ProtectedIdentityResetRequiredReason reason,
        string expectedCode)
    {
        var presentation = StartupLocalStateReset.ToUserPresentation(
            new ProtectedIdentityResetRequiredException(reason, "sensitive-identity-detail"));

        Assert.Equal(expectedCode, presentation.Code);
        Assert.DoesNotContain("sensitive", presentation.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive", presentation.Guidance, StringComparison.Ordinal);
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
    public void CapturedIdentityFailurePreservesReasonWithoutRequestingReset()
    {
        Preferences.Default.Remove(StartupLocalStateReset.WipeLocalDataOnNextLaunchKey);
        try
        {
            var context = new StartupLocalStateResetContext();
            context.Capture(new ProtectedIdentityResetRequiredException(
                ProtectedIdentityResetRequiredReason.AccountMismatch,
                "Reset required."));

            Assert.True(context.TryGetPresentation(out var presentation));
            Assert.Equal("protected-identity-account-mismatch", presentation.Code);
            Assert.False(Preferences.Default.Get(
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
