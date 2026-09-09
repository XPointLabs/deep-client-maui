using System.Security.Cryptography;
using System.Text;
using Deep.Client.Maui.Core.Commands;
using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Domain;

namespace Deep.Client.Maui.Core.ViewModels;

public sealed class OnboardingViewModel : ViewModelBase
{
    private readonly IDeepAccountRuntimeAccessor accountRuntime;
    private readonly AuthNavigationState? authNavigationState;
    private string displayName = string.Empty;
    private string recoveryPhrase = string.Empty;
    private bool isRestored;

    public OnboardingViewModel(
        IDeepAccountRuntimeAccessor accountRuntime,
        AuthNavigationState? authNavigationState = null)
    {
        this.accountRuntime = accountRuntime;
        this.authNavigationState = authNavigationState;
        RestoreCommand = new AsyncCommand(RestoreAsync, CanRestore);
    }

    public string DisplayName
    {
        get => displayName;
        set
        {
            if (SetProperty(ref displayName, value))
            {
                RestoreCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string RecoveryPhrase
    {
        get => recoveryPhrase;
        set
        {
            if (SetProperty(ref recoveryPhrase, value))
            {
                RestoreCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsRestored
    {
        get => isRestored;
        private set => SetProperty(ref isRestored, value);
    }

    public DeepAccount? Account { get; private set; }

    public AsyncCommand RestoreCommand { get; }

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        var phraseForAttempt = RecoveryPhrase;
        byte[]? phraseUtf8 = null;
        try
        {
            await RunBusyAsync(async ct =>
            {
                phraseUtf8 = Encoding.UTF8.GetBytes(phraseForAttempt);
                using var ownedPhrase = DeepOwnedRecoveryPhraseUtf8.CopyFrom(phraseUtf8);
                var accounts = await accountRuntime.GetAccountsAsync(ct).ConfigureAwait(false);
                var result = await accounts
                    .RestoreAsNewDeviceAsync(ownedPhrase, DisplayName, ct)
                    .ConfigureAwait(false);
                Account = result.Identity.Account;
                DisplayName = Account.DisplayName;
                IsRestored = true;
                if (authNavigationState is not null)
                {
                    await authNavigationState.RefreshAsync(ct).ConfigureAwait(false);
                }
            }, cancellationToken);
        }
        finally
        {
            if (phraseUtf8 is not null)
            {
                CryptographicOperations.ZeroMemory(phraseUtf8);
            }
            ClearRecoveryPhrase();
        }
    }

    public void ClearRecoveryPhrase() => RecoveryPhrase = string.Empty;

    private bool CanRestore() =>
        !string.IsNullOrWhiteSpace(DisplayName)
        && !string.IsNullOrWhiteSpace(RecoveryPhrase);
}
