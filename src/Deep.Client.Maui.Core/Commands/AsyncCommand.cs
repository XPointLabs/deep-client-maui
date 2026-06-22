using System.Windows.Input;

namespace Deep.Client.Maui.Core.Commands;

public sealed class AsyncCommand(Func<CancellationToken, Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool isExecuting;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !isExecuting && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        isExecuting = true;
        RaiseCanExecuteChanged();

        try
        {
            await execute(CancellationToken.None);
        }
        finally
        {
            isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public Task ExecuteAsync(CancellationToken cancellationToken = default) => execute(cancellationToken);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
