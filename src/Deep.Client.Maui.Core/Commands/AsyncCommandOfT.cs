using System.Windows.Input;

namespace Deep.Client.Maui.Core.Commands;

public sealed class AsyncCommand<T>(
    Func<T, CancellationToken, Task> execute,
    Func<T, bool>? canExecute = null) : ICommand
    where T : class
{
    private int isExecuting;

    public event EventHandler? CanExecuteChanged;
    public event EventHandler<Exception>? ExecutionFailed;

    public bool CanExecute(object? parameter) =>
        parameter is T value
        && Volatile.Read(ref isExecuting) == 0
        && (canExecute?.Invoke(value) ?? true);

    public async void Execute(object? parameter)
    {
        if (parameter is not T value)
        {
            return;
        }

        try
        {
            await ExecuteAsync(value, CancellationToken.None);
        }
        catch (Exception exception)
        {
            ExecutionFailed?.Invoke(this, exception);
        }
    }

    public async Task ExecuteAsync(T parameter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        if (!(canExecute?.Invoke(parameter) ?? true)
            || Interlocked.CompareExchange(ref isExecuting, 1, 0) != 0)
        {
            return;
        }

        RaiseCanExecuteChanged();
        try
        {
            await execute(parameter, cancellationToken);
        }
        finally
        {
            Volatile.Write(ref isExecuting, 0);
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
