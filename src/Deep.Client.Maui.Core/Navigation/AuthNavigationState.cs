using System.ComponentModel;
using System.Runtime.CompilerServices;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Domain;

namespace Deep.Client.Maui.Core.Navigation;

public sealed class AuthNavigationState(IDeepAccountRuntimeAccessor accountRuntime) : INotifyPropertyChanged
{
    private DeepAccount? account;
    private bool isAuthenticated;
    private bool isInitialized;

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? AuthenticationChanged;

    public DeepAccount? Account
    {
        get => account;
        private set => SetProperty(ref account, value);
    }

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

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var accounts = await accountRuntime.GetAccountsAsync(cancellationToken).ConfigureAwait(false);
        var identity = await accounts.GetLocalIdentityAsync(cancellationToken).ConfigureAwait(false);
        Account = identity?.Account;
        IsAuthenticated = Account is not null;
        IsInitialized = true;
    }

    public void MarkSignedOut()
    {
        Account = null;
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
