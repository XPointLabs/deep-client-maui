using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Domain;

namespace Deep.Client.Maui.ViewModels.Tests.ViewModels;

public sealed class OnboardingViewModelTests
{
    [Fact]
    public async Task RestoreCreatesNewDeviceInPendingActivationState()
    {
        await using var source = new DeepAccountTestRuntime();
        var original = await source.CreateAsync("Nikita");
        await using var target = new DeepAccountTestRuntime();
        var navigation = new AuthNavigationState(target);
        var viewModel = new OnboardingViewModel(target, navigation)
        {
            DisplayName = "Nikita",
            RecoveryPhrase = original.RecoveryPhrase
        };

        await viewModel.RestoreAsync();

        Assert.True(viewModel.IsRestored);
        Assert.Empty(viewModel.RecoveryPhrase);
        Assert.Equal(
            original.Result.Identity.Account.PermanentId,
            viewModel.Account!.PermanentId);
        Assert.Equal(
            DeepAccountActivationState.RestorePendingActivation,
            viewModel.Account.ActivationState);
        Assert.True(navigation.IsAuthenticated);
    }

    [Fact]
    public async Task RestoreWithMalformedPhraseFailsWithoutLocalMutationAndClearsInput()
    {
        await using var accounts = new DeepAccountTestRuntime();
        var viewModel = new OnboardingViewModel(accounts)
        {
            DisplayName = "Nikita",
            RecoveryPhrase = "invalid recovery phrase"
        };

        await viewModel.RestoreAsync();

        Assert.False(viewModel.IsRestored);
        Assert.Null(viewModel.Account);
        Assert.Empty(viewModel.RecoveryPhrase);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.ErrorMessage));
        Assert.Null(await accounts.Accounts.GetLocalIdentityAsync());
    }

    [Fact]
    public async Task RestoreCommandRequiresDisplayNameAndRecoveryPhrase()
    {
        await using var accounts = new DeepAccountTestRuntime();
        var viewModel = new OnboardingViewModel(accounts)
        {
            RecoveryPhrase = "phrase"
        };

        Assert.False(viewModel.RestoreCommand.CanExecute(null));
        viewModel.DisplayName = "Nikita";
        Assert.True(viewModel.RestoreCommand.CanExecute(null));
        viewModel.ClearRecoveryPhrase();
        Assert.False(viewModel.RestoreCommand.CanExecute(null));
    }
}
