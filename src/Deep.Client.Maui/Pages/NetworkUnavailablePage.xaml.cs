using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Shared.Domain;

namespace Deep.Client.Maui.Pages;

public partial class NetworkUnavailablePage : ContentPage
{
    private readonly AuthNavigationState authNavigationState;

    public NetworkUnavailablePage(AuthNavigationState authNavigationState)
    {
        InitializeComponent();
        this.authNavigationState = authNavigationState;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (authNavigationState.Account?.ActivationState
            == DeepAccountActivationState.RestorePendingActivation)
        {
            StatusTitle.Text = "Аккаунт восстановлен локально";
            StatusMessage.Text =
                "Новое устройство ожидает сетевой активации. Сетевой runtime этой версии ещё недоступен.";
            return;
        }

        StatusTitle.Text = "Аккаунт создан локально";
        StatusMessage.Text =
            "Deep ID доступен без подключения. Можно сохранить ожидающий контакт локально, но беседа появится только после будущей проверки Contact Resolver.";
    }

    private async void OnAddContactClicked(object? sender, EventArgs e) =>
        await Shell.Current.GoToAsync(ShellRouteCatalog.NewConversation);

    private async void OnMyDeepIdClicked(object? sender, EventArgs e) =>
        await Shell.Current.GoToAsync(ShellRouteCatalog.StartConversation);
}
