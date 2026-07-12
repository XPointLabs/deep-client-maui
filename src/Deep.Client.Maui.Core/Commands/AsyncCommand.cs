using System.Windows.Input;

namespace Deep.Client.Maui.Core.Commands;

public sealed class AsyncCommand(Func<CancellationToken, Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private int isExecuting;

    public event EventHandler? CanExecuteChanged;
    public event EventHandler<Exception>? ExecutionFailed;

    public bool CanExecute(object? parameter) => Volatile.Read(ref isExecuting) == 0 && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        try
        {
            await ExecuteAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            ExecutionFailed?.Invoke(this, exception);
        }
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (!(canExecute?.Invoke() ?? true)
            || Interlocked.CompareExchange(ref isExecuting, 1, 0) != 0)
        {
            return;
        }

        RaiseCanExecuteChanged();
        try
        {
            await execute(cancellationToken);
        }
        finally
        {
            Volatile.Write(ref isExecuting, 0);
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
