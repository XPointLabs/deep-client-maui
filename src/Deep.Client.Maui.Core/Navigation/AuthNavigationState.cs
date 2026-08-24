using System.ComponentModel;
using System.Runtime.CompilerServices;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.Core.Navigation;

public sealed class AuthNavigationState(ClientRuntime runtime) : INotifyPropertyChanged
{
    private bool isAuthenticated;
    private bool isInitialized;

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? AuthenticationChanged;

    public bool IsAuthenticated
    {
        get => isAuthenticated;
        private set
        {
            if (SetProperty(ref isAuthenticated, value))
            {
                AuthenticationChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool IsInitialized
    {
        get => isInitialized;
        private set => SetProperty(ref isInitialized, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (IsInitialized)
        {
            return;
        }

        var account = await runtime.Accounts
            .GetActiveAccountAsync(cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
        {
            IsAuthenticated = false;
            IsInitialized = true;
            return;
        }

        var recoveryPhrase = await runtime.Accounts
            .GetRecoveryPhraseAsync(cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(recoveryPhrase))
        {
            throw new ProtectedIdentityResetRequiredException(
                ProtectedIdentityResetRequiredReason.Missing,
                "The active account does not have protected identity material. Reset local data before retrying.");
        }

        if (!SessionAccountService.IsCanonicalRecoveryPhrase(recoveryPhrase))
        {
            throw new ProtectedIdentityResetRequiredException(
                ProtectedIdentityResetRequiredReason.Incompatible,
                "The protected identity material is not compatible with the current account format. Reset local data before retrying.");
        }

        if (!await runtime.Accounts
                .HasUsableActiveIdentityAsync(cancellationToken)
                .ConfigureAwait(false))
        {
            throw new ProtectedIdentityResetRequiredException(
                ProtectedIdentityResetRequiredReason.AccountMismatch,
                "The protected identity material does not match the active account. Reset local data before retrying.");
        }

        IsAuthenticated = true;
        IsInitialized = true;
    }

    public void MarkAuthenticated()
    {
        IsAuthenticated = true;
        IsInitialized = true;
    }

    public void MarkSignedOut()
    {
        IsAuthenticated = false;
        IsInitialized = true;
    }

    private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return false;
        }

        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
