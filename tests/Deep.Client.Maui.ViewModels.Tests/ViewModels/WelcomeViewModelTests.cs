using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Domain;

namespace Deep.Client.Maui.ViewModels.Tests.ViewModels;

public sealed class WelcomeViewModelTests
{
    [Fact]
    public async Task CreateCommitsAccountAndAuthenticatesWithOneButtonAction()
    {
        await using var accounts = new DeepAccountTestRuntime();
        var navigation = new AuthNavigationState(accounts);
        var admission = new RecordingAdmissionCoordinator(accounts);
        var viewModel = new WelcomeViewModel(accounts, navigation, admission)
        {
            DisplayName = "Alice"
        };

        await viewModel.CreateAccountAsync();

        Assert.NotNull(await accounts.Accounts.GetLocalIdentityAsync());
        Assert.NotNull(viewModel.Account);
        Assert.Equal(DeepAccountActivationState.ActiveLocal, viewModel.Account!.ActivationState);
        Assert.Equal("Alice", viewModel.DisplayName);
        Assert.True(navigation.IsAuthenticated);
        Assert.True(await accounts.Accounts.HasRetainedRecoveryPhraseAsync());
        Assert.Equal(1, admission.Calls);
    }

    [Fact]
    public async Task BlankDisplayNameCannotCreateAccount()
    {
        await using var accounts = new DeepAccountTestRuntime();
        var viewModel = new WelcomeViewModel(
            accounts,
            new AuthNavigationState(accounts));

        Assert.False(viewModel.CreateAccountCommand.CanExecute(null));
        viewModel.DisplayName = "Alice";
        Assert.True(viewModel.CreateAccountCommand.CanExecute(null));
    }

    [Fact]
    public async Task FailedCreateKeepsTheUserSignedOutAndSurfacesTheError()
    {
        await using var accounts = new DeepAccountTestRuntime();
        _ = await accounts.CreateAsync("Existing");
        var navigation = new AuthNavigationState(accounts);
        var viewModel = new WelcomeViewModel(accounts, navigation)
        {
            DisplayName = "Alice"
        };

        await viewModel.CreateAccountAsync();

        Assert.False(navigation.IsAuthenticated);
        Assert.Null(viewModel.Account);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.ErrorMessage));
    }

    private sealed class RecordingAdmissionCoordinator(
        IDeepAccountRuntimeAccessor accounts)
        : IDeepAccountDirectoryAdmissionCoordinator
    {
        internal int Calls { get; private set; }

        public async Task EnsureCurrentAccountAdmittedAsync(
            CancellationToken cancellationToken = default)
        {
            Calls++;
            await accounts.EnsureLocalIdentityActivatedAsync(cancellationToken);
        }
    }
}
