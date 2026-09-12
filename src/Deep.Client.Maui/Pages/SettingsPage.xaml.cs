using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Maui.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui.Pages;

public partial class SettingsPage : ContentPage
{
    private const string AvatarFileName = "profile-avatar.jpg";
    private readonly SettingsViewModel viewModel;

    public SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        BindingContext = viewModel;
        RecoveryPhraseEditor.HandlerChanged += OnRecoveryPhraseHandlerChanged;
        HardenRecoveryPhraseDisplay();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        viewModel.Activate();
        await viewModel.LoadAsync();
        UpdateProfileAvatarUi();
        DeepIdLabel.Text = FormatDeepIdForDisplay(viewModel.DeepId);
        VersionLabel.Text = $"Deep {AppInfo.Current.VersionString}";
    }

    protected override void OnDisappearing()
    {
        viewModel.ClearRecoveryPhraseFromUi();
        viewModel.Deactivate();
        base.OnDisappearing();
    }

    private async void OnCopyRecoveryPhraseClicked(object? sender, EventArgs e)
    {
        if (!viewModel.IsRecoveryPhraseRevealed
            || string.IsNullOrWhiteSpace(viewModel.RetainedRecoveryPhrase))
        {
            return;
        }

        await Clipboard.Default.SetTextAsync(viewModel.RetainedRecoveryPhrase);
        await DisplayAlertAsync(
            "Фраза скопирована",
            "Буфер обмена может быть доступен другим приложениям. Сохраните фразу и очистите буфер обмена.",
            "OK");
    }

    private async void OnDeleteRecoveryPhraseClicked(object? sender, EventArgs e)
    {
        var confirmed = await DisplayAlertAsync(
            "Удалить фразу с устройства?",
            "После удаления приложение не сможет показать её снова. Аккаунт продолжит работать, но для восстановления понадобится ранее сохранённая копия.",
            "Удалить",
            "Отмена");
        if (!confirmed)
        {
            return;
        }

        await viewModel.DeleteRecoveryPhraseAsync();
        if (viewModel.HasError)
        {
            await DisplayAlertAsync("Фраза восстановления", viewModel.ErrorMessage!, "OK");
        }
    }

    private void OnRecoveryPhraseHandlerChanged(object? sender, EventArgs e) =>
        HardenRecoveryPhraseDisplay();

    private void HardenRecoveryPhraseDisplay()
    {
#if ANDROID
        if (RecoveryPhraseEditor.Handler?.PlatformView is Android.Widget.EditText editor)
        {
            editor.ImportantForAutofill = Android.Views.ImportantForAutofill.NoExcludeDescendants;
            editor.ImportantForAccessibility = Android.Views.ImportantForAccessibility.NoHideDescendants;
            editor.SetAutofillHints([]);
        }
#elif WINDOWS
        if (RecoveryPhraseEditor.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox editor)
        {
            editor.IsSpellCheckEnabled = false;
            editor.IsTextPredictionEnabled = false;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(
                editor,
                Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        }
#endif
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

    private async void OnCopyDeepIdClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(viewModel.DeepId) || viewModel.DeepId == "-")
        {
            return;
        }

        await Clipboard.Default.SetTextAsync(viewModel.DeepId);
    }

    private async void OnShareClicked(object? sender, EventArgs e)
    {
        await ShareDeepIdAsync();
    }

    private async void OnInviteClicked(object? sender, EventArgs e)
    {
        await ShareDeepIdAsync();
    }

    private async Task ShareDeepIdAsync()
    {
        if (string.IsNullOrWhiteSpace(viewModel.DeepId) || viewModel.DeepId == "-")
        {
            return;
        }

        await Share.Default.RequestAsync(new ShareTextRequest
        {
            Title = "Пригласить друга",
            Text = $"Добавьте меня в Deep: {viewModel.DeepId}"
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

            await ProfileAvatarSync.SaveLocalAsync(photo, GetAvatarPath());
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

    private static string FormatDeepIdForDisplay(string? deepId)
    {
        var value = deepId?.Trim();
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
