using Deep.Client.Maui.Core.Commands;
using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Domain;

namespace Deep.Client.Maui.Core.ViewModels;

public sealed class WelcomeViewModel : ViewModelBase
{
    private readonly IDeepAccountRuntimeAccessor accountRuntime;
    private readonly AuthNavigationState authNavigationState;
    private DeepAccount? account;
    private string displayName = string.Empty;

    public WelcomeViewModel(
        IDeepAccountRuntimeAccessor accountRuntime,
        AuthNavigationState authNavigationState)
    {
        this.accountRuntime = accountRuntime;
        this.authNavigationState = authNavigationState;
        CreateAccountCommand = new AsyncCommand(CreateAccountAsync, CanCreateAccount);
    }

    public string DisplayName
    {
        get => displayName;
        set
        {
            if (SetProperty(ref displayName, value))
            {
                CreateAccountCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public DeepAccount? Account
    {
        get => account;
        private set => SetProperty(ref account, value);
    }

    public AsyncCommand CreateAccountCommand { get; }

    public Task CreateAccountAsync(CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            var accounts = await accountRuntime.GetAccountsAsync(ct).ConfigureAwait(false);
            var result = await accounts.CreateAsync(DisplayName, ct).ConfigureAwait(false);
            Account = result.Identity.Account;
            DisplayName = Account.DisplayName;
            await authNavigationState.RefreshAsync(ct).ConfigureAwait(false);
        }, cancellationToken);

    private bool CanCreateAccount() => !string.IsNullOrWhiteSpace(DisplayName);
}
