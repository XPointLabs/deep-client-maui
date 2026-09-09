using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Domain;

namespace Deep.Client.Maui.ViewModels.Tests.ViewModels;

public sealed class WelcomeViewModelTests
{
    [Fact]
    public async Task PrepareRevealsTwentyFourWordsWithoutMutatingLocalStore()
    {
        await using var accounts = new DeepAccountTestRuntime();
        var navigation = new AuthNavigationState(accounts);
        using var viewModel = new WelcomeViewModel(accounts, navigation)
        {
            DisplayName = "Alice"
        };

        await viewModel.PrepareAccountAsync();

        Assert.True(viewModel.IsConfirmationRequired);
        Assert.Equal(24, viewModel.GeneratedRecoveryPhrase.Split(' ').Length);
        Assert.Null(await accounts.Accounts.GetLocalIdentityAsync());
        Assert.False(navigation.IsAuthenticated);
    }

    [Fact]
    public async Task ExactConfirmationCommitsBeforeNavigationBecomesAuthenticated()
    {
        await using var accounts = new DeepAccountTestRuntime();
        var navigation = new AuthNavigationState(accounts);
        using var viewModel = new WelcomeViewModel(accounts, navigation)
        {
            DisplayName = "Alice"
        };
        await viewModel.PrepareAccountAsync();
        viewModel.RecoveryPhraseConfirmation = viewModel.GeneratedRecoveryPhrase;

        await viewModel.ConfirmAccountAsync();

        Assert.NotNull(await accounts.Accounts.GetLocalIdentityAsync());
        Assert.NotNull(viewModel.Account);
        Assert.Equal(DeepAccountActivationState.ActiveLocal, viewModel.Account!.ActivationState);
        Assert.True(navigation.IsAuthenticated);
        Assert.Empty(viewModel.GeneratedRecoveryPhrase);
        Assert.Empty(viewModel.RecoveryPhraseConfirmation);
        Assert.False(viewModel.IsConfirmationRequired);
    }

    [Fact]
    public async Task WrongConfirmationRejectsCommitAndDiscardsPreparedSecrets()
    {
        await using var accounts = new DeepAccountTestRuntime();
        var navigation = new AuthNavigationState(accounts);
        using var viewModel = new WelcomeViewModel(accounts, navigation)
        {
            DisplayName = "Alice"
        };
        await viewModel.PrepareAccountAsync();
        viewModel.RecoveryPhraseConfirmation = "wrong confirmation";

        await viewModel.ConfirmAccountAsync();

        Assert.Null(await accounts.Accounts.GetLocalIdentityAsync());
        Assert.False(navigation.IsAuthenticated);
        Assert.False(viewModel.IsConfirmationRequired);
        Assert.Empty(viewModel.GeneratedRecoveryPhrase);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.ErrorMessage));
        Assert.Empty(viewModel.RecoveryPhraseConfirmation);
    }

    [Fact]
    public async Task FailedCommitDiscardsConsumedDraftAndAllowsFreshPrepare()
    {
        await using var accounts = new DeepAccountTestRuntime();
        var navigation = new AuthNavigationState(accounts);
        using var viewModel = new WelcomeViewModel(accounts, navigation)
        {
            DisplayName = "Alice"
        };
        await viewModel.PrepareAccountAsync();
        var confirmation = viewModel.GeneratedRecoveryPhrase;
        _ = await accounts.CreateAsync("Existing");
        viewModel.RecoveryPhraseConfirmation = confirmation;

        await viewModel.ConfirmAccountAsync();

        Assert.False(viewModel.IsConfirmationRequired);
        Assert.Empty(viewModel.GeneratedRecoveryPhrase);
        Assert.Empty(viewModel.RecoveryPhraseConfirmation);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.ErrorMessage));

        await accounts.Accounts.ResetLocalAccountAsync();
        await viewModel.PrepareAccountAsync();
        Assert.True(viewModel.IsConfirmationRequired);
        Assert.Equal(24, viewModel.GeneratedRecoveryPhrase.Split(' ').Length);
    }

    [Fact]
    public async Task CancelDisposesDraftAndClearsPhraseUi()
    {
        await using var accounts = new DeepAccountTestRuntime();
        using var viewModel = new WelcomeViewModel(
            accounts,
            new AuthNavigationState(accounts))
        {
            DisplayName = "Alice"
        };
        await viewModel.PrepareAccountAsync();

        viewModel.DiscardPreparedAccount();

        Assert.False(viewModel.IsConfirmationRequired);
        Assert.Empty(viewModel.GeneratedRecoveryPhrase);
        Assert.Empty(viewModel.RecoveryPhraseConfirmation);
        Assert.Null(await accounts.Accounts.GetLocalIdentityAsync());
    }
}
