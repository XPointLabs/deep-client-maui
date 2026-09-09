using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.ViewModels;

namespace Deep.Client.Maui.Pages;

public partial class WelcomePage : ContentPage
{
    private readonly WelcomeViewModel viewModel;

    public WelcomePage(WelcomeViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        BindingContext = viewModel;
    }

    protected override void OnDisappearing()
    {
        viewModel.DiscardPreparedAccount();
        base.OnDisappearing();
    }

    private async void OnRestoreClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync(ShellRouteCatalog.Restore);
    }
}
