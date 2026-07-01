using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Shared.State;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using QRCoder;

namespace Deep.Client.Maui.Pages;

public partial class StartConversationPage : ContentPage
{
    private readonly ClientRuntime runtime;
    private string accountId = string.Empty;
    private byte[]? accountQrBytes;

    public StartConversationPage(ClientRuntime runtime)
    {
        InitializeComponent();
        this.runtime = runtime;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        var account = await runtime.Accounts.GetActiveAccountAsync();
        accountId = account?.SessionId.Value ?? string.Empty;
        AccountIdLabel.Text = string.IsNullOrWhiteSpace(accountId) ? "-" : accountId;
        accountQrBytes = string.IsNullOrWhiteSpace(accountId)
            ? null
            : PngByteQRCodeHelper.GetQRCode(accountId, QRCodeGenerator.ECCLevel.Q, 12);
        AccountQrImage.IsVisible = accountQrBytes is not null;
        AccountQrImage.Source = accountQrBytes is null
            ? null
            : ImageSource.FromStream(() => new MemoryStream(accountQrBytes, writable: false));
    }

    protected override bool OnBackButtonPressed()
    {
        _ = NavigateBackToConversationsAsync();
        return true;
    }

    private async void OnCloseClicked(object? sender, EventArgs e)
    {
        await NavigateBackToConversationsAsync();
    }

    private static Task NavigateBackToConversationsAsync() =>
        Shell.Current.GoToAsync($"//{ShellRouteCatalog.Conversations}");

    private async void OnNewMessageClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync(ShellRouteCatalog.NewConversation);
    }

    private async void OnCreateGroupClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync(ShellRouteCatalog.Groups);
    }

    private async void OnInviteFriendClicked(object? sender, EventArgs e)
    {
        await ShareAccountIdAsync();
    }

    private async void OnShareClicked(object? sender, EventArgs e)
    {
        await ShareAccountIdAsync();
    }

    private async void OnCopyClicked(object? sender, EventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            await Clipboard.Default.SetTextAsync(accountId);
        }
    }

    private async Task ShareAccountIdAsync()
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return;
        }

        await Share.Default.RequestAsync(new ShareTextRequest
        {
            Title = "Пригласить друга",
            Text = $"Добавьте меня в Deep: {accountId}"
        });
    }
}
