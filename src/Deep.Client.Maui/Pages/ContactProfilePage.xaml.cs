using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.Presentation;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.State;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui.Pages;

public partial class ContactProfilePage : ContentPage, IQueryAttributable
{
    private readonly ClientRuntime runtime;
    private readonly CallSessionCoordinator callCoordinator;
    private SessionId? contactId;
    private string? routeDisplayName;
    private bool isBlocked;
    private bool isSelf;

    public ContactProfilePage(ClientRuntime runtime, CallSessionCoordinator callCoordinator)
    {
        InitializeComponent();
        this.runtime = runtime;
        this.callCoordinator = callCoordinator;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (!query.TryGetValue("sessionId", out var sessionIdRaw))
        {
            return;
        }

        var sessionId = sessionIdRaw?.ToString();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        contactId = SessionId.Parse(sessionId);
        routeDisplayName = query.TryGetValue("displayName", out var displayNameRaw)
            ? displayNameRaw?.ToString()
            : null;
        _ = LoadAsync();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (contactId is null)
        {
            return;
        }

        try
        {
            var account = await runtime.Accounts.GetActiveAccountAsync();
            var contact = await runtime.Conversations.GetContactAsync(contactId.Value);
            isSelf = account?.SessionId == contactId.Value;
            isBlocked = contact?.IsBlocked == true;

            var storedLocalName = isSelf ? account?.DisplayName : contact?.DisplayName;
            var titleName = FirstNonEmpty(storedLocalName, routeDisplayName);
            var displayName = DeepDisplayName.ContactTitleOrFallback(contactId.Value, titleName);
            var localName = DeepDisplayName.LocalNameOrEmpty(contactId.Value, storedLocalName);

            DisplayNameLabel.Text = displayName;
            NameValueLabel.Text = string.IsNullOrWhiteSpace(localName) ? "Не задано" : localName;
            AvatarLabel.Text = DeepDisplayName.AvatarInitial(displayName, contactId.Value.Value);
            AccountIdLabel.Text = contactId.Value.Value;
            StatusLabel.Text = isSelf
                ? "Ваш аккаунт Deep"
                : contact is { IsApproved: false, IsBlocked: false }
                    ? "Запрос сообщений"
                    : isBlocked
                        ? "Заблокирован"
                        : "Контакт Deep";

            MessageButton.IsEnabled = !isSelf;
            AudioCallButton.IsEnabled = !isSelf && !isBlocked;
            VideoCallButton.IsEnabled = !isSelf && !isBlocked;
            BlockRow.IsVisible = !isSelf;
            BlockLabel.Text = isBlocked ? "Разблокировать" : "Заблокировать";
            BlockLabel.TextColor = GetResourceColor(isBlocked ? "PrimaryColor" : "DangerColor");
            BlockIcon.Stroke = BlockLabel.TextColor;

            UpdateAvatarUi();
        }
        catch (Exception ex)
        {
            CrashDiagnostics.LogException("ContactProfilePage.LoadAsync", ex);
            await DisplayAlertAsync("Профиль", ex.Message, "OK");
        }
    }

    private async void OnBackClicked(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync("..");

    private async void OnMessageClicked(object? sender, EventArgs e)
    {
        await OpenChatAsync();
    }

    private async void OnAudioCallClicked(object? sender, EventArgs e) =>
        await OpenCallAsync(isVideo: false);

    private async void OnVideoCallClicked(object? sender, EventArgs e) =>
        await OpenCallAsync(isVideo: true);

    private async Task OpenChatAsync()
    {
        if (contactId is null || isSelf)
        {
            return;
        }

        var route = $"{ShellRouteCatalog.Chat}?sessionId={Uri.EscapeDataString(contactId.Value.Value)}&displayName={Uri.EscapeDataString(DisplayNameLabel.Text)}";
        await Shell.Current.GoToAsync(route);
    }

    private async Task OpenCallAsync(bool isVideo)
    {
        if (contactId is null || isSelf || isBlocked)
        {
            return;
        }

        try
        {
            var conversationId = ConversationId.ForOneToOne(contactId.Value).Value;
            var descriptor = await callCoordinator.CreateOutgoingAsync(
                contactId.Value,
                conversationId,
                DisplayNameLabel.Text,
                isVideo);
            await Shell.Current.GoToAsync(
                ShellRouteCatalog.Call,
                new ShellNavigationQueryParameters { ["call"] = descriptor });
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException)
        {
            CrashDiagnostics.LogException("ContactProfilePage.OpenCall", exception);
            await DisplayAlertAsync("Звонок Deep", exception.Message, "Закрыть");
        }
    }

    private async void OnCopyIdTapped(object? sender, TappedEventArgs e)
    {
        if (contactId is not null)
        {
            await Clipboard.Default.SetTextAsync(contactId.Value.Value);
        }
    }

    private void OnEditNameClicked(object? sender, EventArgs e) =>
        ShowEditNameOverlay();

    private void OnEditNameClicked(object? sender, TappedEventArgs e) =>
        ShowEditNameOverlay();

    private void ShowEditNameOverlay()
    {
        if (contactId is null)
        {
            return;
        }

        EditNameTitleLabel.Text = isSelf ? "Имя профиля" : "Имя";
        EditNameEntry.Text = NameValueLabel.Text == "Не задано" ? string.Empty : NameValueLabel.Text;
        EditNameOverlay.IsVisible = true;
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(120), () =>
        {
            EditNameEntry.Focus();
            EditNameEntry.CursorPosition = 0;
            EditNameEntry.SelectionLength = EditNameEntry.Text?.Length ?? 0;
        });
    }

    private void OnCancelEditNameClicked(object? sender, EventArgs e)
    {
        EditNameEntry.Unfocus();
        EditNameOverlay.IsVisible = false;
    }

    private void OnCancelEditNameClicked(object? sender, TappedEventArgs e)
    {
        EditNameEntry.Unfocus();
        EditNameOverlay.IsVisible = false;
    }

    private async void OnSaveEditNameClicked(object? sender, EventArgs e)
    {
        await SaveEditedNameAsync();
    }

    private async void OnEditNameEntryCompleted(object? sender, EventArgs e)
    {
        await SaveEditedNameAsync();
    }

    private async Task SaveEditedNameAsync()
    {
        if (contactId is null)
        {
            return;
        }

        var updated = EditNameEntry.Text;

        if (isSelf && string.IsNullOrWhiteSpace(updated))
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(updated) ? null : updated.Trim();
        if (isSelf && name is not null)
        {
            await runtime.Accounts.UpdateDisplayNameAsync(name);
        }

        var updatedContact = await runtime.Conversations.UpdateContactDisplayNameAsync(contactId.Value, name);
        routeDisplayName = updatedContact.DisplayName;
        EditNameEntry.Unfocus();
        EditNameOverlay.IsVisible = false;
        ContactProfileUpdateBus.NotifyContactChanged(contactId.Value);
        await LoadAsync();
    }

    private async void OnChangePhotoClicked(object? sender, TappedEventArgs e) =>
        await PickProfilePhotoAsync();

    private async Task PickProfilePhotoAsync()
    {
        if (contactId is null)
        {
            return;
        }

        try
        {
            var photo = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = isSelf ? "Выберите фото профиля" : "Выберите фото контакта",
                FileTypes = FilePickerFileType.Images
            });

            if (photo is null)
            {
                return;
            }

            if (isSelf)
            {
                await ProfileAvatarSync.SaveAndPublishAsync(photo, ContactAvatarStore.GetProfileAvatarPath(), runtime);
            }
            else
            {
                await ContactAvatarStore.SaveLocalContactAvatarAsync(photo, contactId.Value);
            }

            ContactProfileUpdateBus.NotifyContactChanged(contactId.Value);
            UpdateAvatarUi();
        }
        catch (Exception ex)
        {
            CrashDiagnostics.LogException("ContactProfilePage.PickProfilePhoto", ex);
            await DisplayAlertAsync("Фото контакта", $"Не удалось обновить фото: {ex.Message}", "OK");
        }
    }

    private async void OnRemovePhotoClicked(object? sender, TappedEventArgs e)
    {
        if (contactId is null || isSelf)
        {
            return;
        }

        var confirmed = await DisplayAlertAsync(
            "Удалить фото?",
            "Локальное фото контакта будет удалено с этого устройства.",
            "Удалить",
            "Отмена");
        if (!confirmed)
        {
            return;
        }

        ContactAvatarStore.DeleteAvatar(contactId.Value, isSelf: false);
        ContactProfileUpdateBus.NotifyContactChanged(contactId.Value);
        UpdateAvatarUi();
    }

    private async void OnClearHistoryClicked(object? sender, TappedEventArgs e)
    {
        if (contactId is null)
        {
            return;
        }

        if (!await DisplayAlertAsync("Очистить историю?", "Сообщения будут удалены с этого устройства.", "Очистить", "Отмена"))
        {
            return;
        }

        await runtime.Messages.ClearConversationMessagesAsync(ConversationId.ForOneToOne(contactId.Value));
    }

    private async void OnBlockClicked(object? sender, TappedEventArgs e)
    {
        if (contactId is null || isSelf)
        {
            return;
        }

        await runtime.Conversations.SetContactBlockedAsync(contactId.Value, !isBlocked);
        ContactProfileUpdateBus.NotifyContactChanged(contactId.Value);
        await LoadAsync();
    }

    private void UpdateAvatarUi()
    {
        if (contactId is null)
        {
            return;
        }

        var avatarPath = ContactAvatarStore.GetAvatarPath(contactId.Value, isSelf);
        var hasAvatar = File.Exists(avatarPath);
        AvatarImage.Source = null;
        AvatarImage.IsVisible = hasAvatar;
        AvatarLabel.IsVisible = !hasAvatar;
        if (hasAvatar)
        {
            AvatarImage.Source = ImageSource.FromFile(avatarPath);
        }

        RemovePhotoRow.IsVisible = hasAvatar && !isSelf;
        RemovePhotoDivider.IsVisible = hasAvatar && !isSelf;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string ShortId(string value) => value.Length <= 14
        ? value
        : $"{value[..8]}...{value[^4..]}";

    private static Color GetResourceColor(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color color
            ? color
            : Colors.White;
}
