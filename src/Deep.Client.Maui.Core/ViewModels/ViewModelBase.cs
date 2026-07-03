using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Deep.Client.Maui.Core.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    private bool isBusy;
    private string? errorMessage;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsBusy
    {
        get => isBusy;
        protected set => SetProperty(ref isBusy, value);
    }

    public string? ErrorMessage
    {
        get => errorMessage;
        protected set
        {
            if (SetProperty(ref errorMessage, value))
            {
                RaisePropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return false;
        }

        storage = value;
        RaisePropertyChanged(propertyName);
        return true;
    }

    protected void RaisePropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected async Task RunBusyAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            ErrorMessage = null;
            await action(cancellationToken);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
