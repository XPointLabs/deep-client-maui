using Deep.Client.Maui.Core.Commands;
using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.Presentation;
using Deep.Client.Maui.Core.Services;

namespace Deep.Client.Maui.Core.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly IDeepAccountRuntimeAccessor accountRuntime;
    private readonly AuthNavigationState authNavigationState;
    private readonly INetworkStatusService networkStatusService;
    private readonly IAccountLogoutCoordinator accountLogoutCoordinator;
    private string accountDisplayName = "Нет аккаунта";
    private string deepId = "-";
    private string connectionStatus = "Неизвестно";

    public SettingsViewModel(
        IDeepAccountRuntimeAccessor accountRuntime,
        AuthNavigationState authNavigationState,
        INetworkStatusService networkStatusService,
        IAccountLogoutCoordinator accountLogoutCoordinator)
    {
        this.accountRuntime = accountRuntime;
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
        : DeepDisplayName.AvatarInitial(AccountDisplayName, DeepId);

    public string DeepId
    {
        get => deepId;
        private set
        {
            if (SetProperty(ref deepId, value))
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
            var accounts = await accountRuntime.GetAccountsAsync(ct);
            var identity = await accounts.GetLocalIdentityAsync(ct);
            AccountDisplayName = identity?.Account.DisplayName ?? "Нет аккаунта";
            DeepId = identity?.Account.PermanentId.CanonicalText ?? "-";
            ConnectionStatus = networkStatusService.ConnectionLabel;
            RaisePropertyChanged(nameof(IsNetworkConnected));
            LogoutCommand.RaiseCanExecuteChanged();
        }, cancellationToken);

    public Task UpdateDisplayNameAsync(string displayName, CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            var accounts = await accountRuntime.GetAccountsAsync(ct);
            var updated = await accounts.UpdateDisplayNameAsync(displayName, ct);
            AccountDisplayName = updated.DisplayName;
            await authNavigationState.RefreshAsync(ct);
        }, cancellationToken);

    public Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        return RunBusyAsync(async ct =>
        {
            await accountLogoutCoordinator.LogoutAsync(ct);
            authNavigationState.MarkSignedOut();

            AccountDisplayName = "Нет аккаунта";
            DeepId = "-";
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
