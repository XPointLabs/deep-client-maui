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
    private string accountDisplayName = "Нет аккаунта";
    private string sessionId = "-";
    private string connectionStatus = "Неизвестно";
    private bool wipeLocalDataOnLogout;

    public SettingsViewModel(
        ClientRuntime runtime,
        AuthNavigationState authNavigationState,
        INetworkStatusService networkStatusService)
    {
        this.runtime = runtime;
        this.authNavigationState = authNavigationState;
        this.networkStatusService = networkStatusService;

        RefreshCommand = new AsyncCommand(LoadAsync);
        LogoutCommand = new AsyncCommand(LogoutAsync, () => authNavigationState.IsAuthenticated);

        connectionStatus = networkStatusService.ConnectionLabel;
        networkStatusService.StatusChanged += OnNetworkStatusChanged;
        authNavigationState.AuthenticationChanged += OnAuthenticationChanged;
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

    public bool WipeLocalDataOnLogout
    {
        get => wipeLocalDataOnLogout;
        set => SetProperty(ref wipeLocalDataOnLogout, value);
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

    public Task LogoutAsync(bool wipeLocalData, CancellationToken cancellationToken = default)
    {
        return RunBusyAsync(async ct =>
        {
            await runtime.Accounts.SignOutAsync(ct);
            authNavigationState.MarkSignedOut();

            AccountDisplayName = "Нет аккаунта";
            SessionId = "-";
            LogoutCommand.RaiseCanExecuteChanged();
        }, cancellationToken);
    }

    private Task LogoutAsync(CancellationToken cancellationToken) =>
        LogoutAsync(WipeLocalDataOnLogout, cancellationToken);

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
