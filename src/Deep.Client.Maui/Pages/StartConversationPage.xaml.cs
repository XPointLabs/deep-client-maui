using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.State;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using QRCoder;

namespace Deep.Client.Maui.Pages;

public partial class StartConversationPage : ContentPage
{
    private readonly IContactInvitationProvider invitationProvider;
    private string invitation = string.Empty;
    private byte[]? invitationQrBytes;

    public StartConversationPage(IContactInvitationProvider invitationProvider)
    {
        InitializeComponent();
        this.invitationProvider = invitationProvider;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        try
        {
            invitation = await invitationProvider.GetInvitationAsync() ?? string.Empty;
            AccountIdLabel.Text = string.IsNullOrWhiteSpace(invitation) ? "-" : invitation;
#if DEEP_PHYSICAL_E2E
            // Physical UAT exchanges the rendered CMI1 instead of reading app storage or
            // reusing a pre-provisioned Session ID. The runner hashes this value before it
            // writes sanitized evidence; no invitation is logged or persisted by automation.
            AccountIdLabel.IsVisible = !string.IsNullOrWhiteSpace(invitation);
            AccountIdLabel.Opacity = 0.01;
            AccountIdLabel.HeightRequest = 1;
#else
            AccountIdLabel.IsVisible = false;
#endif
            invitationQrBytes = string.IsNullOrWhiteSpace(invitation)
                ? null
                : PngByteQRCodeHelper.GetQRCode(
                    invitation,
                    QRCodeGenerator.ECCLevel.Q,
                    12);
            AccountQrImage.IsVisible = invitationQrBytes is not null;
            AccountQrImage.Source = invitationQrBytes is null
                ? null
                : ImageSource.FromStream(() =>
                    new MemoryStream(invitationQrBytes, writable: false));
        }
        catch
        {
            invitation = string.Empty;
            invitationQrBytes = null;
            AccountIdLabel.Text = "-";
            AccountIdLabel.IsVisible = false;
            AccountQrImage.IsVisible = false;
            AccountQrImage.Source = null;
            await DisplayAlertAsync(
                "Приглашение недоступно",
                "Не удалось подготовить защищённое приглашение. Проверьте подключение и повторите попытку.",
                "OK");
        }
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
        Shell.Current.GoToAsync("..", animate: false);

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
        if (!string.IsNullOrWhiteSpace(invitation))
        {
            await Clipboard.Default.SetTextAsync(invitation);
        }
    }

    private async Task ShareAccountIdAsync()
    {
        if (string.IsNullOrWhiteSpace(invitation))
        {
            return;
        }

        await Share.Default.RequestAsync(new ShareTextRequest
        {
            Title = "Пригласить друга",
            Text = $"Добавьте меня в Deep: {invitation}"
        });
    }
}
