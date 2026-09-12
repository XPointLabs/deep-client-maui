using Deep.Client.Maui.Core.Commands;
using System.Text;
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
    private string retainedRecoveryPhrase = string.Empty;
    private string recoveryPhraseStatus = "Проверка защищённой копии…";
    private bool hasRetainedRecoveryPhrase;
    private bool isRecoveryPhraseRevealed;

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
        RevealRecoveryPhraseCommand = new AsyncCommand(
            RevealRecoveryPhraseAsync,
            () => HasRetainedRecoveryPhrase && !IsRecoveryPhraseRevealed);
        HideRecoveryPhraseCommand = new AsyncCommand(
            _ =>
            {
                ClearRecoveryPhraseFromUi();
                return Task.CompletedTask;
            },
            () => IsRecoveryPhraseRevealed);
        DeleteRecoveryPhraseCommand = new AsyncCommand(
            DeleteRecoveryPhraseAsync,
            () => HasRetainedRecoveryPhrase);

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

    public string RetainedRecoveryPhrase
    {
        get => retainedRecoveryPhrase;
        private set => SetProperty(ref retainedRecoveryPhrase, value);
    }

    public string RecoveryPhraseStatus
    {
        get => recoveryPhraseStatus;
        private set => SetProperty(ref recoveryPhraseStatus, value);
    }

    public bool HasRetainedRecoveryPhrase
    {
        get => hasRetainedRecoveryPhrase;
        private set
        {
            if (SetProperty(ref hasRetainedRecoveryPhrase, value))
            {
                RevealRecoveryPhraseCommand.RaiseCanExecuteChanged();
                DeleteRecoveryPhraseCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsRecoveryPhraseRevealed
    {
        get => isRecoveryPhraseRevealed;
        private set
        {
            if (SetProperty(ref isRecoveryPhraseRevealed, value))
            {
                RevealRecoveryPhraseCommand.RaiseCanExecuteChanged();
                HideRecoveryPhraseCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AsyncCommand RefreshCommand { get; }

    public AsyncCommand LogoutCommand { get; }

    public AsyncCommand RevealRecoveryPhraseCommand { get; }

    public AsyncCommand HideRecoveryPhraseCommand { get; }

    public AsyncCommand DeleteRecoveryPhraseCommand { get; }

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
            HasRetainedRecoveryPhrase = identity is not null
                && await accounts.HasRetainedRecoveryPhraseAsync(ct).ConfigureAwait(false);
            RecoveryPhraseStatus = HasRetainedRecoveryPhrase
                ? "Защищённая копия сохранена на этом устройстве"
                : "Копия удалена. Для восстановления понадобится сохранённая вами фраза.";
            ClearRecoveryPhraseFromUi();
            ConnectionStatus = networkStatusService.ConnectionLabel;
            RaisePropertyChanged(nameof(IsNetworkConnected));
            LogoutCommand.RaiseCanExecuteChanged();
        }, cancellationToken);

    public Task RevealRecoveryPhraseAsync(CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            var accounts = await accountRuntime.GetAccountsAsync(ct).ConfigureAwait(false);
            string? revealed = null;
            var found = await accounts.RevealRetainedRecoveryPhraseAsync(
                    bytes => revealed = Encoding.UTF8.GetString(bytes),
                    ct)
                .ConfigureAwait(false);
            if (!found)
            {
                HasRetainedRecoveryPhrase = false;
                RecoveryPhraseStatus = "Копия удалена. Для восстановления понадобится сохранённая вами фраза.";
                ClearRecoveryPhraseFromUi();
                return;
            }

            RetainedRecoveryPhrase = revealed!;
            IsRecoveryPhraseRevealed = true;
        }, cancellationToken);

    public Task DeleteRecoveryPhraseAsync(CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            var accounts = await accountRuntime.GetAccountsAsync(ct).ConfigureAwait(false);
            await accounts.DeleteRetainedRecoveryPhraseAsync(ct).ConfigureAwait(false);
            ClearRecoveryPhraseFromUi();
            HasRetainedRecoveryPhrase = false;
            RecoveryPhraseStatus = "Копия удалена. Для восстановления понадобится сохранённая вами фраза.";
        }, cancellationToken);

    public void ClearRecoveryPhraseFromUi()
    {
        RetainedRecoveryPhrase = string.Empty;
        IsRecoveryPhraseRevealed = false;
    }

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
            ClearRecoveryPhraseFromUi();
            HasRetainedRecoveryPhrase = false;
            RecoveryPhraseStatus = "Нет аккаунта";
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
