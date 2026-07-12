using Deep.Client.Maui.Core.Commands;
using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.Presentation;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.Core.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly ClientRuntime runtime;
    private readonly AuthNavigationState authNavigationState;
    private readonly INetworkStatusService networkStatusService;
    private readonly IAccountLogoutCoordinator accountLogoutCoordinator;
    private string accountDisplayName = "Нет аккаунта";
    private string sessionId = "-";
    private string connectionStatus = "Неизвестно";

    public SettingsViewModel(
        ClientRuntime runtime,
        AuthNavigationState authNavigationState,
        INetworkStatusService networkStatusService,
        IAccountLogoutCoordinator accountLogoutCoordinator)
    {
        this.runtime = runtime;
        this.authNavigationState = authNavigationState;
        this.networkStatusService = networkStatusService;
        this.accountLogoutCoordinator = accountLogoutCoordinator;

        RefreshCommand = new AsyncCommand(LoadAsync);
        LogoutCommand = new AsyncCommand(LogoutAsync, () => authNavigationState.IsAuthenticated);

        connectionStatus = networkStatusService.ConnectionLabel;
    }

    public string AccountDisplayName
    {
        get => accountDisplayName;
        private set
        {
            if (SetProperty(ref accountDisplayName, value))
            {
                RaisePropertyChanged(nameof(AccountInitial));
            }
        }
    }

    public string AccountInitial => AccountDisplayName == "Нет аккаунта"
        ? "D"
        : DeepDisplayName.AvatarInitial(AccountDisplayName, SessionId);

    public string SessionId
    {
        get => sessionId;
        private set
        {
            if (SetProperty(ref sessionId, value))
            {
                RaisePropertyChanged(nameof(AccountInitial));
            }
        }
    }

    public string ConnectionStatus
    {
        get => connectionStatus;
        private set => SetProperty(ref connectionStatus, value);
    }

    public bool IsNetworkConnected => networkStatusService.IsConnected;

    public AsyncCommand RefreshCommand { get; }

    public AsyncCommand LogoutCommand { get; }

    public void Activate()
    {
        Deactivate();
        networkStatusService.StatusChanged += OnNetworkStatusChanged;
        authNavigationState.AuthenticationChanged += OnAuthenticationChanged;
        ConnectionStatus = networkStatusService.ConnectionLabel;
        RaisePropertyChanged(nameof(IsNetworkConnected));
        LogoutCommand.RaiseCanExecuteChanged();
    }

    public void Deactivate()
    {
        networkStatusService.StatusChanged -= OnNetworkStatusChanged;
        authNavigationState.AuthenticationChanged -= OnAuthenticationChanged;
    }

    public Task LoadAsync(CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            var account = await runtime.Accounts.GetActiveAccountAsync(ct);
            AccountDisplayName = account?.DisplayName ?? "Нет аккаунта";
            SessionId = account?.SessionId.Value ?? "-";
            ConnectionStatus = networkStatusService.ConnectionLabel;
            RaisePropertyChanged(nameof(IsNetworkConnected));
            LogoutCommand.RaiseCanExecuteChanged();
        }, cancellationToken);

    public Task<string?> GetRecoveryPhraseAsync(CancellationToken cancellationToken = default) =>
        runtime.Accounts.GetRecoveryPhraseAsync(cancellationToken);

    public Task UpdateDisplayNameAsync(string displayName, CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            var updated = await runtime.Accounts.UpdateDisplayNameAsync(displayName, ct);
            AccountDisplayName = updated.DisplayName;
        }, cancellationToken);

    public Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        return RunBusyAsync(async ct =>
        {
            await accountLogoutCoordinator.LogoutAsync(ct);
            authNavigationState.MarkSignedOut();

            AccountDisplayName = "Нет аккаунта";
            SessionId = "-";
            LogoutCommand.RaiseCanExecuteChanged();
        }, cancellationToken);
    }

    private void OnNetworkStatusChanged(object? sender, EventArgs e)
    {
        ConnectionStatus = networkStatusService.ConnectionLabel;
        RaisePropertyChanged(nameof(IsNetworkConnected));
    }

    private void OnAuthenticationChanged(object? sender, EventArgs e)
    {
        LogoutCommand.RaiseCanExecuteChanged();
    }

}
