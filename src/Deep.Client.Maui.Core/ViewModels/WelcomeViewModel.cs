using System.Security.Cryptography;
using System.Text;
using Deep.Client.Maui.Core.Commands;
using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Domain;

namespace Deep.Client.Maui.Core.ViewModels;

public sealed class WelcomeViewModel : ViewModelBase, IDisposable
{
    private readonly IDeepAccountRuntimeAccessor accountRuntime;
    private readonly AuthNavigationState authNavigationState;
    private DeepPreparedAccountCreationDraft? preparedDraft;
    private DeepAccount? account;
    private string displayName = string.Empty;
    private string generatedRecoveryPhrase = string.Empty;
    private string recoveryPhraseConfirmation = string.Empty;
    private bool isConfirmationRequired;

    public WelcomeViewModel(
        IDeepAccountRuntimeAccessor accountRuntime,
        AuthNavigationState authNavigationState)
    {
        this.accountRuntime = accountRuntime;
        this.authNavigationState = authNavigationState;
        PrepareAccountCommand = new AsyncCommand(PrepareAccountAsync, CanPrepareAccount);
        ConfirmAccountCommand = new AsyncCommand(ConfirmAccountAsync, CanConfirmAccount);
        CancelPreparationCommand = new AsyncCommand(
            _ =>
            {
                DiscardPreparedAccount();
                return Task.CompletedTask;
            },
            () => IsConfirmationRequired);
    }

    public string DisplayName
    {
        get => displayName;
        set
        {
            if (SetProperty(ref displayName, value))
            {
                PrepareAccountCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string GeneratedRecoveryPhrase
    {
        get => generatedRecoveryPhrase;
        private set => SetProperty(ref generatedRecoveryPhrase, value);
    }

    public string RecoveryPhraseConfirmation
    {
        get => recoveryPhraseConfirmation;
        set
        {
            if (SetProperty(ref recoveryPhraseConfirmation, value))
            {
                ConfirmAccountCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsConfirmationRequired
    {
        get => isConfirmationRequired;
        private set
        {
            if (SetProperty(ref isConfirmationRequired, value))
            {
                PrepareAccountCommand.RaiseCanExecuteChanged();
                ConfirmAccountCommand.RaiseCanExecuteChanged();
                CancelPreparationCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public DeepAccount? Account
    {
        get => account;
        private set => SetProperty(ref account, value);
    }

    public AsyncCommand PrepareAccountCommand { get; }

    public AsyncCommand ConfirmAccountCommand { get; }

    public AsyncCommand CancelPreparationCommand { get; }

    public Task PrepareAccountAsync(CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            var accounts = await accountRuntime.GetAccountsAsync(ct).ConfigureAwait(false);
            DeepPreparedAccountCreationDraft? draft = accounts.PrepareCreate(DisplayName);
            string? revealed = null;
            try
            {
                draft.RevealCanonicalPhraseOnce(
                    phraseUtf8 => revealed = Encoding.UTF8.GetString(phraseUtf8));
                ReplacePreparedDraft(draft, revealed!);
                draft = null;
            }
            finally
            {
                draft?.Dispose();
            }
        }, cancellationToken);

    public Task ConfirmAccountAsync(CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            var draft = preparedDraft
                ?? throw new InvalidOperationException(
                    "Prepare and reveal the recovery phrase before confirming the account.");
            byte[]? confirmationUtf8 = null;
            try
            {
                confirmationUtf8 = Encoding.UTF8.GetBytes(RecoveryPhraseConfirmation);
                using var confirmation = DeepOwnedRecoveryPhraseUtf8.CopyFrom(confirmationUtf8);
                var accounts = await accountRuntime.GetAccountsAsync(ct).ConfigureAwait(false);
                var result = await accounts
                    .CommitPreparedAsync(draft, confirmation, ct)
                    .ConfigureAwait(false);
                preparedDraft = null;
                Account = result.Identity.Account;
                ClearPhraseUi();
                await authNavigationState.RefreshAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                if (ReferenceEquals(preparedDraft, draft))
                {
                    preparedDraft = null;
                }
                draft.Dispose();
                ClearPhraseUi();
                throw;
            }
            finally
            {
                if (confirmationUtf8 is not null)
                {
                    CryptographicOperations.ZeroMemory(confirmationUtf8);
                }
                RecoveryPhraseConfirmation = string.Empty;
            }
        }, cancellationToken);

    public void DiscardPreparedAccount()
    {
        var draft = preparedDraft;
        preparedDraft = null;
        draft?.Dispose();
        ClearPhraseUi();
    }

    public void Dispose() => DiscardPreparedAccount();

    private void ReplacePreparedDraft(
        DeepPreparedAccountCreationDraft draft,
        string revealedRecoveryPhrase)
    {
        preparedDraft?.Dispose();
        preparedDraft = draft;
        GeneratedRecoveryPhrase = revealedRecoveryPhrase;
        RecoveryPhraseConfirmation = string.Empty;
        IsConfirmationRequired = true;
    }

    private void ClearPhraseUi()
    {
        GeneratedRecoveryPhrase = string.Empty;
        RecoveryPhraseConfirmation = string.Empty;
        IsConfirmationRequired = false;
    }

    private bool CanPrepareAccount() =>
        !IsConfirmationRequired && !string.IsNullOrWhiteSpace(DisplayName);

    private bool CanConfirmAccount() =>
        IsConfirmationRequired && !string.IsNullOrWhiteSpace(RecoveryPhraseConfirmation);
}
