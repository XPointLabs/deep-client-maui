using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.ViewModels.Tests.ViewModels;

public sealed class OnboardingViewModelTests
{
    [Fact]
    public async Task RegisterCreatesActiveSessionId()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var viewModel = new OnboardingViewModel(runtime) { DisplayName = "Nikita" };

        await viewModel.RegisterAsync();

        Assert.True(viewModel.IsLoggedIn);
        Assert.StartsWith("05", viewModel.SessionId);
        Assert.NotNull(await runtime.Accounts.GetActiveAccountAsync());
    }

    [Fact]
    public async Task LoginRestoresAccountAndMarksItAsRecovered()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        await runtime.Accounts.RegisterAsync("Nikita");
        var recoveryPhrase = await runtime.Accounts.GetRecoveryPhraseAsync();
        var viewModel = new OnboardingViewModel(runtime)
        {
            DisplayName = "Nikita",
            RecoveryPhrase = recoveryPhrase!
        };

        await viewModel.LoginAsync();

        Assert.True(viewModel.IsLoggedIn);
        Assert.Empty(viewModel.RecoveryPhrase);
        Assert.False(viewModel.LoginCommand.CanExecute(null));
        var active = await runtime.Accounts.GetActiveAccountAsync();
        Assert.NotNull(active);
        Assert.True(active!.IsRestoredAccount);
    }

    [Fact]
    public async Task LoginWithMalformedRecoveryPhraseSurfacesValidationError()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var viewModel = new OnboardingViewModel(runtime)
        {
            DisplayName = "Nikita",
            RecoveryPhrase = "invalid-session"
        };

        await viewModel.LoginAsync();

        Assert.False(viewModel.IsLoggedIn);
        Assert.Contains("Recovery phrase must contain 13 words", viewModel.ErrorMessage);
        Assert.Empty(viewModel.RecoveryPhrase);
        Assert.False(viewModel.LoginCommand.CanExecute(null));
    }

    [Fact]
    public void LoginCommandRequiresOnlyRecoveryPhrase()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var viewModel = new OnboardingViewModel(runtime)
        {
            DisplayName = string.Empty,
            RecoveryPhrase = "amaze buffet cake entrance symptoms tiger lamb maze nestle python dusted faxed faxed"
        };

        Assert.True(viewModel.LoginCommand.CanExecute(null));
    }

    [Fact]
    public async Task LoginWithoutDisplayNameAndWithoutRecoveredProfileShowsFallbackError()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var viewModel = new OnboardingViewModel(runtime)
        {
            DisplayName = string.Empty,
            RecoveryPhrase = "amaze buffet cake entrance symptoms tiger lamb maze nestle python dusted faxed faxed"
        };

        await viewModel.LoginAsync();

        Assert.False(viewModel.IsLoggedIn);
        Assert.Contains("Unable to recover profile display name from network", viewModel.ErrorMessage);
        Assert.Empty(viewModel.RecoveryPhrase);
    }

    [Fact]
    public async Task CancelledLoginAttemptClearsRecoveryPhrase()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var viewModel = new OnboardingViewModel(runtime)
        {
            RecoveryPhrase = "sensitive recovery phrase"
        };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => viewModel.LoginAsync(cancellation.Token));

        Assert.Empty(viewModel.RecoveryPhrase);
        Assert.False(viewModel.LoginCommand.CanExecute(null));
    }

    [Fact]
    public void ClearRecoveryPhraseDisablesLoginCommand()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var viewModel = new OnboardingViewModel(runtime)
        {
            RecoveryPhrase = "sensitive recovery phrase"
        };

        viewModel.ClearRecoveryPhrase();

        Assert.Empty(viewModel.RecoveryPhrase);
        Assert.False(viewModel.LoginCommand.CanExecute(null));
    }
}
