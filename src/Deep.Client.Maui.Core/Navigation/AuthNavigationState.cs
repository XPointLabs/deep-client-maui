using System.ComponentModel;
using System.Runtime.CompilerServices;
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

        var account = await runtime.Accounts.GetActiveAccountAsync(cancellationToken);
        IsAuthenticated = account is not null;
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
