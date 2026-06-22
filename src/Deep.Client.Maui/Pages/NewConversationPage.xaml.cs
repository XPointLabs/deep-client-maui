using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;

namespace Deep.Client.Maui.Pages;

public partial class NewConversationPage : ContentPage
{
    private readonly ConversationsViewModel viewModel;

    public NewConversationPage(ConversationsViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        BindingContext = viewModel;
    }

    protected override bool OnBackButtonPressed()
    {
        _ = NavigateBackToConversationsAsync();
        return true;
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        await NavigateBackToConversationsAsync();
    }

    private static Task NavigateBackToConversationsAsync() =>
        Shell.Current.GoToAsync($"//{ShellRouteCatalog.Conversations}");

    private async void OnStartClicked(object? sender, EventArgs e)
    {
        var conversation = await viewModel.StartConversationFromComposerAsync();
        if (conversation is null)
        {
            await DisplayAlertAsync("Новое сообщение", "Введите корректный ID аккаунта.", "OK");
            return;
        }

        KeyboardDismissal.Dismiss(SessionIdEntry);
        KeyboardDismissal.Dismiss(DisplayNameEntry);
        await Task.Delay(150);

        var route = $"{ShellRouteCatalog.Chat}?sessionId={Uri.EscapeDataString(conversation.Id.Value)}&displayName={Uri.EscapeDataString(conversation.DisplayName)}";
        await Shell.Current.GoToAsync(route);
    }
}
