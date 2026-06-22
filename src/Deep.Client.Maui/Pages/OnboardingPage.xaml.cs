using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.ViewModels;

namespace Deep.Client.Maui.Pages;

public partial class OnboardingPage : ContentPage
{
    public OnboardingPage(OnboardingViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync($"//{ShellRouteCatalog.Onboarding}");
    }
}
