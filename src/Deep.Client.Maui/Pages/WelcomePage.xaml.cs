using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.ViewModels;

namespace Deep.Client.Maui.Pages;

public partial class WelcomePage : ContentPage
{
    public WelcomePage(WelcomeViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    private async void OnRestoreClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync(ShellRouteCatalog.Restore);
    }
}
