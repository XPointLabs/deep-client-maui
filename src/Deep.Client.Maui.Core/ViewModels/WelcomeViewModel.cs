using Deep.Client.Maui.Core.Commands;
using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.Core.ViewModels;

public sealed class WelcomeViewModel : ViewModelBase
{
    private readonly ClientRuntime runtime;
    private readonly AuthNavigationState authNavigationState;
    private string displayName = string.Empty;

    public WelcomeViewModel(ClientRuntime runtime, AuthNavigationState authNavigationState)
    {
        this.runtime = runtime;
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

    public AsyncCommand CreateAccountCommand { get; }

    public Task CreateAccountAsync(CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            var name = DisplayName.Trim();
            await runtime.Accounts.RegisterAsync(name, ct);
            authNavigationState.MarkAuthenticated();
        }, cancellationToken);

    private bool CanCreateAccount() => !string.IsNullOrWhiteSpace(DisplayName);
}
