using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.State;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui.Pages;

public partial class SettingsPage : ContentPage
{
    private const string AvatarFileName = "profile-avatar.jpg";
    private readonly SettingsViewModel viewModel;
    private readonly ClientRuntime runtime;

    public SettingsPage(SettingsViewModel viewModel, ClientRuntime runtime)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        this.runtime = runtime;
        BindingContext = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        viewModel.Activate();
        await viewModel.LoadAsync();
        UpdateProfileAvatarUi();
        SessionIdLabel.Text = FormatSessionIdForDisplay(viewModel.SessionId);
        VersionLabel.Text = $"Deep {AppInfo.Current.VersionString}";
    }

    protected override void OnDisappearing()
    {
        viewModel.Deactivate();
        base.OnDisappearing();
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
        Shell.Current.GoToAsync("..", animate: false);

    private static Task NavigateToSettingsSectionAsync(string section) =>
        Shell.Current.GoToAsync($"{ShellRouteCatalog.SettingsDetail}?section={Uri.EscapeDataString(section)}");

    private async void OnCopySessionIdClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(viewModel.SessionId) || viewModel.SessionId == "-")
        {
            return;
        }

        await Clipboard.Default.SetTextAsync(viewModel.SessionId);
    }

    private async void OnShareClicked(object? sender, EventArgs e)
    {
        await ShareSessionIdAsync();
    }

    private async void OnInviteClicked(object? sender, EventArgs e)
    {
        await ShareSessionIdAsync();
    }

    private async Task ShareSessionIdAsync()
    {
        if (string.IsNullOrWhiteSpace(viewModel.SessionId) || viewModel.SessionId == "-")
        {
            return;
        }

        await Share.Default.RequestAsync(new ShareTextRequest
        {
            Title = "Пригласить друга",
            Text = $"Добавьте меня в Deep: {viewModel.SessionId}"
        });
    }

    private async void OnEditNameClicked(object? sender, EventArgs e)
    {
        var currentName = viewModel.AccountDisplayName == "Нет аккаунта" ? string.Empty : viewModel.AccountDisplayName;
        var updated = await DisplayPromptAsync(
            "Имя профиля",
            "Выберите имя, которое будет показываться на этом устройстве.",
            accept: "Сохранить",
            cancel: "Отмена",
            initialValue: currentName,
            maxLength: 48);

        if (string.IsNullOrWhiteSpace(updated))
        {
            return;
        }

        await viewModel.UpdateDisplayNameAsync(updated.Trim());
    }

    private async void OnQrClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync(ShellRouteCatalog.StartConversation);
    }

    private async void OnChangePhotoClicked(object? sender, EventArgs e)
    {
        await PickProfilePhotoAsync();
    }

    private async Task PickProfilePhotoAsync()
    {
        try
        {
            var photo = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Выберите фото профиля",
                FileTypes = FilePickerFileType.Images
            });

            if (photo is null)
            {
                return;
            }

            await ProfileAvatarSync.SaveAndPublishAsync(photo, GetAvatarPath(), runtime);
            UpdateProfileAvatarUi();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Фото профиля", $"Не удалось обновить фото: {ex.Message}", "OK");
        }
    }

    private void UpdateProfileAvatarUi()
    {
        if (ProfileAvatarImage is null || ProfileInitialLabel is null)
        {
            return;
        }

        var avatarPath = GetAvatarPath();
        var hasAvatar = File.Exists(avatarPath);
        ProfileAvatarImage.IsVisible = hasAvatar;
        ProfileInitialLabel.IsVisible = !hasAvatar;
        ProfileAvatarImage.Source = hasAvatar ? ImageSource.FromFile(avatarPath) : null;
    }

    private static string GetAvatarPath() => Path.Combine(MauiProgram.ResolveAppDataDirectory(), AvatarFileName);

    private static string FormatSessionIdForDisplay(string? sessionId)
    {
        var value = sessionId?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value == "-")
        {
            return "-";
        }

        return value;
    }

    private async void OnPathClicked(object? sender, EventArgs e)
    {
        await NavigateToSettingsSectionAsync("path");
    }

    private async void OnOfflineUpdateClicked(object? sender, EventArgs e)
    {
        await NavigateToSettingsSectionAsync("offline-update");
    }

    private async void OnPrivacyClicked(object? sender, EventArgs e)
    {
        await NavigateToSettingsSectionAsync("privacy");
    }

    private async void OnNotificationsClicked(object? sender, EventArgs e)
    {
        await NavigateToSettingsSectionAsync("notifications");
    }

    private async void OnConversationsSettingsClicked(object? sender, EventArgs e)
    {
        await NavigateToSettingsSectionAsync("conversations");
    }

    private async void OnAppearanceClicked(object? sender, EventArgs e)
    {
        await NavigateToSettingsSectionAsync("appearance");
    }

    private async void OnMessageRequestsClicked(object? sender, EventArgs e)
    {
        await NavigateToSettingsSectionAsync("message-requests");
    }

    private async void OnRecoveryPhraseClicked(object? sender, EventArgs e)
    {
        var recoveryPhrase = await viewModel.GetRecoveryPhraseAsync();
        if (string.IsNullOrWhiteSpace(recoveryPhrase))
        {
            await DisplayAlertAsync("Фраза восстановления", "Для этого аккаунта не сохранена фраза восстановления.", "OK");
            return;
        }

        var action = await DisplayActionSheetAsync("Фраза восстановления", "Отмена", null, "Показать", "Скопировать");
        if (action == "Скопировать")
        {
            await CopySensitiveTextAsync(recoveryPhrase);
        }
        else if (action == "Показать")
        {
            await DisplayAlertAsync("Фраза восстановления", recoveryPhrase, "OK");
        }
    }

    private static async Task CopySensitiveTextAsync(string value)
    {
        await Clipboard.Default.SetTextAsync(value);
        _ = ClearClipboardIfUnchangedAsync(value);
    }

    private static async Task ClearClipboardIfUnchangedAsync(string value)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1));
            var current = await Clipboard.Default.GetTextAsync();
            if (string.Equals(current, value, StringComparison.Ordinal))
            {
                await Clipboard.Default.SetTextAsync(string.Empty);
            }
        }
        catch
        {
            // Clipboard access is best-effort and must not affect settings UX.
        }
    }

    private async void OnHelpClicked(object? sender, EventArgs e)
    {
        await NavigateToSettingsSectionAsync("help");
    }

    private async void OnLogoutClicked(object? sender, EventArgs e)
    {
        var confirmed = await DisplayAlertAsync("Очистить данные", "Выйти из аккаунта Deep на этом устройстве?", "Очистить данные", "Отмена");
        if (!confirmed)
        {
            return;
        }

        await viewModel.LogoutAsync();

        if (viewModel.HasError)
        {
            await DisplayAlertAsync("Очистить данные", viewModel.ErrorMessage!, "OK");
        }
    }
}
