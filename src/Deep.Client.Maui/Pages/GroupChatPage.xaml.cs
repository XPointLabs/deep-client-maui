using System.Collections.Specialized;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Services;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui.Pages;

public partial class GroupChatPage : ContentPage, IQueryAttributable
{
    private IDispatcherTimer? autoRefreshTimer;
    private readonly GroupChatViewModel viewModel;
    private readonly INetworkStatusService networkStatusService;
    private readonly IAttachmentFileTransport attachmentFiles;
    private readonly SyncPollingPolicy syncPollingPolicy;
    private readonly VoiceMessagePlaybackService voicePlayback = new();
    private CancellationTokenSource? pendingScrollToEnd;
    private CancellationTokenSource? routeLoadCancellation;
    private IDisposable? keyboardInsetSubscription;
    private IDispatcherTimer? voiceRecordingTimer;
    private bool pendingScrollAnimate;
    private bool pendingScrollForce;
    private bool isLoadingOlderMessages;
    private bool shouldStickToEnd = true;
    private bool didInitialScroll;
    private double expandedPageHeight;
    private double keyboardBottomInset;
    private DateTimeOffset voiceRecordingStartedAt;
    private GroupChatMessageItem? selectedMessage;
    private AttachmentMetadata? selectedAttachment;

    public GroupChatPage(
        GroupChatViewModel viewModel,
        INetworkStatusService networkStatusService,
        IAttachmentFileTransport attachmentFiles,
        SyncPollingPolicy syncPollingPolicy)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        this.networkStatusService = networkStatusService;
        this.attachmentFiles = attachmentFiles;
        this.syncPollingPolicy = syncPollingPolicy;
        BindingContext = viewModel;

        networkStatusService.StatusChanged += OnNetworkStatusChanged;
        viewModel.Messages.CollectionChanged += OnMessagesCollectionChanged;
        SizeChanged += OnPageSizeChanged;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        keyboardInsetSubscription?.Dispose();
        keyboardInsetSubscription = AndroidKeyboardInsets.Observe(OnKeyboardInsetChanged);
        Dispatcher.Dispatch(ApplyAndroidSafeAreaCompensation);
        BackgroundSyncBridge.SyncScheduled -= OnBackgroundSyncScheduled;
        BackgroundSyncBridge.SyncScheduled += OnBackgroundSyncScheduled;
        ApplyComposerPreferences();
        UpdateNetworkUi();
        _ = ConfigureAutoRefreshAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        BackgroundSyncBridge.SyncScheduled -= OnBackgroundSyncScheduled;
        keyboardInsetSubscription?.Dispose();
        keyboardInsetSubscription = null;
        keyboardBottomInset = 0;
        ApplyAndroidSafeAreaCompensation();
        autoRefreshTimer?.Stop();
        StopRecordingUiTimer();
        voicePlayback.Stop();
        CancelRouteLoad();
        CancelPendingScrollToEnd();
    }

    protected override bool OnBackButtonPressed()
    {
        _ = NavigateBackToConversationsAsync();
        return true;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        CancelRouteLoad();
        routeLoadCancellation = new CancellationTokenSource();
        _ = LoadFromRouteAsync(query, routeLoadCancellation.Token);
    }

    private async Task LoadFromRouteAsync(IDictionary<string, object> query, CancellationToken cancellationToken)
    {
        try
        {
            if (!query.TryGetValue("groupId", out var groupIdRaw))
            {
                return;
            }

            var groupId = groupIdRaw?.ToString();
            if (string.IsNullOrWhiteSpace(groupId))
            {
                return;
            }

            var displayName = query.TryGetValue("displayName", out var displayNameRaw)
                ? displayNameRaw?.ToString()
                : null;

            shouldStickToEnd = true;
            didInitialScroll = false;

            await viewModel.OpenFromRouteAsync(groupId, displayName, cancellationToken);
            await RevealInitialMessagesAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            CrashDiagnostics.LogException("GroupChatPage.LoadFromRouteAsync", ex);
        }
    }

    private async Task RevealInitialMessagesAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(32, cancellationToken).ConfigureAwait(false);
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            ScrollMessagesToEnd(animate: false, force: true);
            MessagesCollection.Opacity = 1;
        });
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        await NavigateBackToConversationsAsync();
    }

    private static Task NavigateBackToConversationsAsync() =>
        Shell.Current.GoToAsync($"//{ShellRouteCatalog.Conversations}", animate: false);

    private async void OnMemberSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not GroupMemberItem member)
        {
            return;
        }

        if (!viewModel.CanManageMembers)
        {
            if (sender is CollectionView collection)
            {
                collection.SelectedItem = null;
            }

            await DisplayAlertAsync("Участники группы", "Управлять участниками могут только администраторы.", "OK");
            return;
        }

        viewModel.SelectedMember = member;

        var actions = new List<string>();
        if (member.Role == GroupMemberRole.Admin)
        {
            actions.Add("Понизить");
        }
        else
        {
            actions.Add("Повысить");
        }

        if (member.IsPendingRemoval)
        {
            actions.Add("Отменить ожидание");
        }
        else
        {
            actions.Add("Пометить на удаление");
        }

        actions.Add("Удалить");

        var choice = await DisplayActionSheetAsync($"Участник {member.SessionId.Value}", "Отмена", null, actions.ToArray());
        switch (choice)
        {
            case "Повысить":
                await viewModel.PromoteMemberAsync();
                break;
            case "Понизить":
                await viewModel.DemoteMemberAsync();
                break;
            case "Пометить на удаление":
                await viewModel.MarkPendingRemovalAsync();
                break;
            case "Отменить ожидание":
                await viewModel.UndoPendingRemovalAsync();
                break;
            case "Удалить":
                await viewModel.RemoveMemberAsync();
                break;
        }

        if (sender is CollectionView members)
        {
            members.SelectedItem = null;
        }
    }

    private void OnNetworkStatusChanged(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(UpdateNetworkUi);
    }

    private void UpdateNetworkUi()
    {
        if (NetworkBanner is not null && NetworkBannerText is not null)
        {
            NetworkBanner.IsVisible = !networkStatusService.IsConnected;
            NetworkBannerText.Text = networkStatusService.ConnectionLabel;
        }
    }

    private void OnMembersClicked(object? sender, EventArgs e)
    {
        MembersPanel.IsVisible = !MembersPanel.IsVisible;
    }

    private void OnAttachClicked(object? sender, TappedEventArgs e)
    {
        AttachmentSheetOverlay.IsVisible = true;
    }

    private void OnCloseAttachmentSheet(object? sender, TappedEventArgs e)
    {
        AttachmentSheetOverlay.IsVisible = false;
    }

    private async void OnPickPhotoClicked(object? sender, TappedEventArgs e) =>
        await PickAttachmentAsync(AttachmentPickKind.Photo);

    private async void OnPickVideoClicked(object? sender, TappedEventArgs e) =>
        await PickAttachmentAsync(AttachmentPickKind.Video);

    private async void OnPickFileClicked(object? sender, TappedEventArgs e) =>
        await PickAttachmentAsync(AttachmentPickKind.File);

    private async Task PickAttachmentAsync(AttachmentPickKind kind)
    {
        AttachmentSheetOverlay.IsVisible = false;
        await viewModel.PickAttachmentsAsync(kind);
    }

    private async void OnSendClicked(object? sender, TappedEventArgs e)
    {
        await viewModel.SendAsync();
    }

    private async void OnVoiceClicked(object? sender, TappedEventArgs e)
    {
        if (viewModel.IsRecordingVoice)
        {
            StopRecordingUiTimer();
            await viewModel.StopVoiceRecordingAndSendAsync();
            return;
        }

        await viewModel.StartVoiceRecordingAsync();
        if (viewModel.IsRecordingVoice)
        {
            StartRecordingUiTimer();
        }
    }

    private async void OnCancelVoiceClicked(object? sender, EventArgs e)
    {
        StopRecordingUiTimer();
        await viewModel.CancelVoiceRecordingAsync();
    }

    private async Task ConfigureAutoRefreshAsync()
    {
        if (await syncPollingPolicy.IsPushDrivenSyncAvailableAsync())
        {
            autoRefreshTimer?.Stop();
            return;
        }

        EnsureAutoRefresh();
    }

    private void EnsureAutoRefresh()
    {
        if (autoRefreshTimer is null)
        {
            autoRefreshTimer = Dispatcher.CreateTimer();
            autoRefreshTimer.Interval = TimeSpan.FromSeconds(4);
            autoRefreshTimer.Tick += OnAutoRefreshTick;
        }

        autoRefreshTimer.Start();
    }

    private async void OnAutoRefreshTick(object? sender, EventArgs e)
    {
        if (viewModel.IsBusy || string.IsNullOrWhiteSpace(viewModel.GroupTitle))
        {
            return;
        }

        await viewModel.RefreshAsync();
    }

    private void OnBackgroundSyncScheduled()
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (!viewModel.IsBusy && !string.IsNullOrWhiteSpace(viewModel.GroupTitle))
            {
                await viewModel.RefreshAsync();
            }
        });
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
        {
            if (e.Action == NotifyCollectionChangedAction.Add && !WasAppendedToEnd(e))
            {
                return;
            }

            var hasOutgoing = e.NewItems?
                .OfType<GroupChatMessageItem>()
                .Any(message => message.Direction == MessageDirection.Outgoing) == true;
            var force = !didInitialScroll || hasOutgoing;
            if (force || shouldStickToEnd)
            {
                QueueScrollToEnd(false, force);
            }
        }
    }

    private void OnMessagesScrolled(object? sender, ItemsViewScrolledEventArgs e)
    {
        if (viewModel.Messages.Count == 0)
        {
            shouldStickToEnd = true;
            return;
        }

        shouldStickToEnd = e.LastVisibleItemIndex >= viewModel.Messages.Count - 2;
        if (e.FirstVisibleItemIndex <= 2)
        {
            _ = LoadOlderMessagesAsync();
        }
    }

    private async Task LoadOlderMessagesAsync()
    {
        if (isLoadingOlderMessages)
        {
            return;
        }

        try
        {
            isLoadingOlderMessages = true;
            await viewModel.LoadOlderMessagesAsync();
        }
        finally
        {
            isLoadingOlderMessages = false;
        }
    }

    private void OnAttachmentTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not GroupChatMessageItem item)
        {
            return;
        }

        ShowAttachmentActionSheet(item.Attachments);
    }

    private async void OnVoiceMessageTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not GroupChatMessageItem item || item.Attachments.Count == 0)
        {
            return;
        }

        try
        {
            await voicePlayback.ToggleAsync(item.Attachments[0], attachmentFiles);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Голосовое сообщение", ex.Message, "OK");
        }
    }

    private void ShowAttachmentActionSheet(IReadOnlyList<AttachmentMetadata> attachments)
    {
        selectedAttachment = attachments.Count == 0 ? null : attachments[0];
        if (selectedAttachment is null)
        {
            return;
        }

        AttachmentActionTitle.Text = selectedAttachment.FileName;
        AttachmentActionSubtitle.Text = DescribeAttachment(selectedAttachment, attachments.Count);
        AttachmentActionOverlay.IsVisible = true;
    }

    private void OnCloseAttachmentActionSheet(object? sender, TappedEventArgs e)
    {
        AttachmentActionOverlay.IsVisible = false;
        selectedAttachment = null;
    }

    private async void OnOpenAttachmentClicked(object? sender, TappedEventArgs e)
    {
        if (selectedAttachment is not { } attachment)
        {
            return;
        }

        AttachmentActionOverlay.IsVisible = false;
        selectedAttachment = null;
        await AttachmentOpenService.OpenAsync(this, [attachment], attachmentFiles);
    }

    private async void OnShareAttachmentClicked(object? sender, TappedEventArgs e)
    {
        if (selectedAttachment is not { } attachment)
        {
            return;
        }

        AttachmentActionOverlay.IsVisible = false;
        selectedAttachment = null;
        try
        {
            await AttachmentOpenService.ShareAsync(attachment, attachmentFiles);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Вложение", ex.Message, "OK");
        }
    }

    private async void OnCopyAttachmentNameClicked(object? sender, TappedEventArgs e)
    {
        if (selectedAttachment is not { } attachment)
        {
            return;
        }

        AttachmentActionOverlay.IsVisible = false;
        selectedAttachment = null;
        await Clipboard.Default.SetTextAsync(attachment.FileName);
    }

    private void ApplyAndroidSafeAreaCompensation()
    {
        expandedPageHeight = Math.Max(expandedPageHeight, Height);
        var keyboardVisibleByResize = expandedPageHeight - Height > 100;
        var keyboardPadding = keyboardVisibleByResize ? 0 : keyboardBottomInset;
        var keyboardVisible = keyboardVisibleByResize || keyboardPadding > 0;
        var statusBarHeight = keyboardVisible ? 0 : AndroidSafeArea.GetStatusBarHeight();
        PageLayout.Margin = new Thickness(0, 0, 0, statusBarHeight);
        PageLayout.Padding = new Thickness(0, 0, 0, keyboardPadding);
    }

    private void OnPageSizeChanged(object? sender, EventArgs e) =>
        ApplyAndroidSafeAreaCompensation();

    private void OnKeyboardInsetChanged(double bottomInset)
    {
        keyboardBottomInset = bottomInset;
        MainThread.BeginInvokeOnMainThread(ApplyAndroidSafeAreaCompensation);
    }

    private void OnMessageTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not GroupChatMessageItem item)
        {
            return;
        }

        selectedMessage = item;
        MessageMenuOverlay.IsVisible = true;
    }

    private void OnCloseMessageMenu(object? sender, TappedEventArgs e)
    {
        MessageMenuOverlay.IsVisible = false;
        selectedMessage = null;
    }

    private async void OnReactionClicked(object? sender, EventArgs e)
    {
        if (selectedMessage is null || sender is not Button { CommandParameter: string emoji })
        {
            return;
        }

        var message = selectedMessage;
        MessageMenuOverlay.IsVisible = false;
        selectedMessage = null;
        await viewModel.ToggleReactionAsync(message, emoji);
    }

    private void OnReplySelectedMessage(object? sender, TappedEventArgs e)
    {
        if (selectedMessage is null)
        {
            return;
        }

        viewModel.BeginReply(selectedMessage);
        MessageMenuOverlay.IsVisible = false;
        selectedMessage = null;
        DraftEntry.Focus();
    }

    private async void OnCopySelectedMessage(object? sender, TappedEventArgs e)
    {
        if (selectedMessage is not null)
        {
            await Clipboard.Default.SetTextAsync(selectedMessage.Body);
        }

        MessageMenuOverlay.IsVisible = false;
        selectedMessage = null;
    }

    private void ApplyComposerPreferences()
    {
        DraftEntry.Keyboard = Preferences.Default.Get(ClientSettingKeys.PrivacyIncognitoKeyboard, true)
            ? Keyboard.Create(KeyboardFlags.None)
            : Keyboard.Default;
    }

    private void StartRecordingUiTimer()
    {
        voiceRecordingStartedAt = DateTimeOffset.UtcNow;
        UpdateRecordingElapsed();
        voiceRecordingTimer ??= Dispatcher.CreateTimer();
        voiceRecordingTimer.Interval = TimeSpan.FromSeconds(1);
        voiceRecordingTimer.Tick -= OnVoiceRecordingTick;
        voiceRecordingTimer.Tick += OnVoiceRecordingTick;
        voiceRecordingTimer.Start();
    }

    private void StopRecordingUiTimer()
    {
        voiceRecordingTimer?.Stop();
        VoiceElapsedLabel.Text = "00:00";
    }

    private void OnVoiceRecordingTick(object? sender, EventArgs e) => UpdateRecordingElapsed();

    private void UpdateRecordingElapsed()
    {
        var elapsed = DateTimeOffset.UtcNow - voiceRecordingStartedAt;
        var seconds = Math.Max(0, (int)elapsed.TotalSeconds);
        VoiceElapsedLabel.Text = $"{seconds / 60:00}:{seconds % 60:00}";
    }

    private static string DescribeAttachment(AttachmentMetadata attachment, int totalCount)
    {
        var kind = attachment.ContentType switch
        {
            var value when value.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => "Фото",
            var value when value.StartsWith("video/", StringComparison.OrdinalIgnoreCase) => "Видео",
            var value when value.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) => "Аудио",
            "application/pdf" => "PDF",
            _ => "Файл"
        };
        var count = totalCount > 1 ? $" из {totalCount}" : string.Empty;
        return $"{kind}{count} · {FormatSize(attachment.SizeBytes)}";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} Б";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024d:0.#} КБ";
        }

        return $"{bytes / 1024d / 1024d:0.#} МБ";
    }

    private void QueueScrollToEnd(bool animate, bool force = false)
    {
        pendingScrollAnimate |= animate;
        pendingScrollForce |= force;
        pendingScrollToEnd?.Cancel();
        pendingScrollToEnd?.Dispose();

        var scrollRequest = new CancellationTokenSource();
        pendingScrollToEnd = scrollRequest;
        _ = ScrollMessagesToEndAsync(scrollRequest);
    }

    private async Task ScrollMessagesToEndAsync(CancellationTokenSource scrollRequest)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(80), scrollRequest.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var animate = pendingScrollAnimate;
        var force = pendingScrollForce;
        if (pendingScrollToEnd != scrollRequest || scrollRequest.IsCancellationRequested)
        {
            return;
        }

        pendingScrollToEnd = null;
        pendingScrollAnimate = false;
        pendingScrollForce = false;
        scrollRequest.Dispose();

        await MainThread.InvokeOnMainThreadAsync(() => ScrollMessagesToEnd(animate, force));
    }

    private void CancelPendingScrollToEnd()
    {
        pendingScrollToEnd?.Cancel();
        pendingScrollToEnd?.Dispose();
        pendingScrollToEnd = null;
        pendingScrollAnimate = false;
        pendingScrollForce = false;
    }

    private void CancelRouteLoad()
    {
        routeLoadCancellation?.Cancel();
        routeLoadCancellation?.Dispose();
        routeLoadCancellation = null;
    }

    private void ScrollMessagesToEnd(bool animate, bool force)
    {
        if (viewModel.Messages.Count == 0 || MessagesCollection is null)
        {
            return;
        }

        if (!force && didInitialScroll && !shouldStickToEnd)
        {
            return;
        }

        MessagesCollection.ScrollTo(viewModel.Messages[^1], position: ScrollToPosition.End, animate: animate);
        didInitialScroll = true;
        shouldStickToEnd = true;
    }

    private bool WasAppendedToEnd(NotifyCollectionChangedEventArgs e)
    {
        var addedCount = e.NewItems?.Count ?? 0;
        return e.NewStartingIndex < 0 || e.NewStartingIndex >= viewModel.Messages.Count - addedCount;
    }
}
