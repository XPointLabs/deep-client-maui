using Deep.Client.Maui.Core.Commands;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Core.ViewModels;

public sealed class NotificationRegistrationViewModel : ViewModelBase
{
    private readonly IPushRegistrationCoordinator pushRegistrationCoordinator;
    private string? token;
    private string? provider;

    public NotificationRegistrationViewModel(IPushRegistrationCoordinator pushRegistrationCoordinator)
    {
        this.pushRegistrationCoordinator = pushRegistrationCoordinator;
        RegisterCommand = new AsyncCommand(RegisterAsync);
    }

    public string? Token
    {
        get => token;
        private set => SetProperty(ref token, value);
    }

    public string? Provider
    {
        get => provider;
        private set => SetProperty(ref provider, value);
    }

    public AsyncCommand RegisterCommand { get; }

    public Task RegisterAsync(CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            var registration = await pushRegistrationCoordinator.RegisterAsync(ct);
            Token = registration?.Token;
            Provider = registration?.Provider;
        }, cancellationToken);
}
