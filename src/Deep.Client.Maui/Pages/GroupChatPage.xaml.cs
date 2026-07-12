using System.Collections.Specialized;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Services;
using Microsoft.Maui.Storage;

#if ANDROID
using Android.Views;
using AndroidX.RecyclerView.Widget;
using AndroidView = Android.Views.View;
#endif

namespace Deep.Client.Maui.Pages;

public partial class GroupChatPage : ContentPage, IQueryAttributable
{
    private const double VoiceCancelSwipeThreshold = 84;
    private const double VoiceCancelPanelTranslation = 36;
    private static readonly TimeSpan InitialMessageLayoutDelay = TimeSpan.FromMilliseconds(80);
    private IDispatcherTimer? autoRefreshTimer;
    private readonly GroupChatViewModel viewModel;
    private readonly INetworkStatusService networkStatusService;
    private readonly IAttachmentFileTransport attachmentFiles;
    private readonly SyncPollingPolicy syncPollingPolicy;
    private readonly IActiveConversationTracker activeConversationTracker;
    private readonly VoiceMessagePlaybackService voicePlayback = new();
    private CancellationTokenSource? pendingScrollToEnd;
    private CancellationTokenSource? routeLoadCancellation;
    private CancellationTokenSource? pageActivityCancellation;
    private IDisposable? activeConversationLease;
    private ConversationId? pageConversationId;
    private bool isPageActive;
    private IDisposable? keyboardInsetSubscription;
    private IDispatcherTimer? voiceRecordingTimer;
    private IDispatcherTimer? voicePlaybackTimer;
    private IDispatcherTimer? imagePreviewDebounceTimer;
    private bool pendingScrollAnimate;
    private bool pendingScrollForce;
    private bool isLoadingOlderMessages;
    private bool shouldStickToEnd = true;
    private bool didInitialScroll;
    private bool isLoadingImagePreviews;
    private readonly Dictionary<string, GroupChatMessageItem> pendingImagePreviews = new(StringComparer.Ordinal);
    private double expandedPageHeight;
    private double keyboardBottomInset;
    private DateTimeOffset voiceRecordingStartedAt;
    private Point voicePointerStart;
    private bool voicePointerActive;
    private bool voiceCancelBySwipe;
    private Task? voiceGestureStartTask;
    private readonly SemaphoreSlim voiceGestureCompletionGate = new(1, 1);
    private int voicePageExitCancellationRequested;
    private bool refreshingMessages;
    private bool hasLoadedImageMessages;
    private int pendingPreviewFirstVisibleIndex = -1;
    private int pendingPreviewLastVisibleIndex = -1;
    private string? activeVoiceAttachmentId;
    private GroupChatMessageItem? selectedMessage;
    private readonly List<GroupChatMessageItem> messageSearchMatches = [];
    private int messageSearchIndex = -1;
    private AttachmentMetadata? selectedAttachment;
    private GroupChatMessageItem? selectedAttachmentMessage;
    private AttachmentMetadata? imageViewerAttachment;
    private GroupChatMessageItem? imageViewerMessage;
#if ANDROID
    private AndroidView? voiceButtonPlatformView;
#endif

    public GroupChatPage(
        GroupChatViewModel viewModel,
        INetworkStatusService networkStatusService,
        IAttachmentFileTransport attachmentFiles,
        SyncPollingPolicy syncPollingPolicy,
        IActiveConversationTracker activeConversationTracker)
    {
        var constructionStopwatch = System.Diagnostics.Stopwatch.StartNew();
        InitializeComponent();
        CrashDiagnostics.LogInfo("Perf.GroupChat", $"ConstructPage elapsedMs={constructionStopwatch.ElapsedMilliseconds}");
        this.viewModel = viewModel;
        this.networkStatusService = networkStatusService;
        this.attachmentFiles = attachmentFiles;
        this.syncPollingPolicy = syncPollingPolicy;
        this.activeConversationTracker = activeConversationTracker;
        BindingContext = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        isPageActive = true;
        RefreshActiveConversationLease();
        pageActivityCancellation?.Cancel();
        pageActivityCancellation?.Dispose();
        pageActivityCancellation = new CancellationTokenSource();
        SubscribePageEvents();
#if ANDROID
        OnVoiceButtonHandlerChanged(VoiceButton, EventArgs.Empty);
        AndroidVoiceGestureRouter.Touch -= OnAndroidVoiceGesture;
        AndroidVoiceGestureRouter.Touch += OnAndroidVoiceGesture;
#endif
        keyboardInsetSubscription?.Dispose();
        keyboardInsetSubscription = AndroidKeyboardInsets.Observe(OnKeyboardInsetChanged);
        Dispatcher.Dispatch(ApplyAndroidSafeAreaCompensation);
        BackgroundSyncBridge.SyncScheduled -= OnBackgroundSyncScheduled;
        BackgroundSyncBridge.SyncScheduled += OnBackgroundSyncScheduled;
        BackgroundSyncBridge.SyncCompleted -= OnBackgroundSyncScheduled;
        BackgroundSyncBridge.SyncCompleted += OnBackgroundSyncScheduled;
        ApplyComposerPreferences();
        UpdateNetworkUi();
        ConfigureMessageListPlatformView();
        Dispatcher.Dispatch(() => QueueVisibleImagePreviews());
        _ = ConfigureAutoRefreshAsync(pageActivityCancellation.Token);
    }

    protected override void OnDisappearing()
    {
        isPageActive = false;
        activeConversationLease?.Dispose();
        activeConversationLease = null;
        base.OnDisappearing();
        voiceCancelBySwipe = true;
        _ = FinishVoiceRecordingGestureAsync(forceCancel: true);
        UnsubscribePageEvents();
        BackgroundSyncBridge.SyncScheduled -= OnBackgroundSyncScheduled;
        BackgroundSyncBridge.SyncCompleted -= OnBackgroundSyncScheduled;
        keyboardInsetSubscription?.Dispose();
        keyboardInsetSubscription = null;
        keyboardBottomInset = 0;
        ApplyAndroidSafeAreaCompensation();
        autoRefreshTimer?.Stop();
        imagePreviewDebounceTimer?.Stop();
        pageActivityCancellation?.Cancel();
        pageActivityCancellation?.Dispose();
        pageActivityCancellation = null;
        StopRecordingUiTimer();
        StopVoicePlaybackTimer();
        ReleaseDispatcherTimers();
        ClearActiveVoicePlayback();
        voicePlayback.Stop();
        pendingImagePreviews.Clear();
        CancelRouteLoad();
        CancelPendingScrollToEnd();
#if ANDROID
        AndroidVoiceGestureRouter.Touch -= OnAndroidVoiceGesture;
        if (voiceButtonPlatformView is not null)
        {
            voiceButtonPlatformView.Touch -= OnVoiceButtonPlatformTouch;
            voiceButtonPlatformView = null;
        }
#endif
    }

    private void ReleaseDispatcherTimers()
    {
        if (autoRefreshTimer is not null)
        {
            autoRefreshTimer.Stop();
            autoRefreshTimer.Tick -= OnAutoRefreshTick;
            autoRefreshTimer = null;
        }
        if (imagePreviewDebounceTimer is not null)
        {
            imagePreviewDebounceTimer.Stop();
            imagePreviewDebounceTimer.Tick -= OnImagePreviewDebounceTick;
            imagePreviewDebounceTimer = null;
        }
        if (voiceRecordingTimer is not null)
        {
            voiceRecordingTimer.Stop();
            voiceRecordingTimer.Tick -= OnVoiceRecordingTick;
            voiceRecordingTimer = null;
        }
        if (voicePlaybackTimer is not null)
        {
            voicePlaybackTimer.Stop();
            voicePlaybackTimer.Tick -= OnVoicePlaybackTick;
            voicePlaybackTimer = null;
        }
    }

    private void SubscribePageEvents()
    {
        UnsubscribePageEvents();
        networkStatusService.StatusChanged += OnNetworkStatusChanged;
        ContactProfileUpdateBus.ContactChanged += OnContactProfileChanged;
        viewModel.Messages.CollectionChanged += OnMessagesCollectionChanged;
        MessagesCollection.HandlerChanged += OnMessagesCollectionHandlerChanged;
        SizeChanged += OnPageSizeChanged;
#if ANDROID
        VoiceButton.HandlerChanged += OnVoiceButtonHandlerChanged;
#endif
    }

    private void UnsubscribePageEvents()
    {
        networkStatusService.StatusChanged -= OnNetworkStatusChanged;
        ContactProfileUpdateBus.ContactChanged -= OnContactProfileChanged;
        viewModel.Messages.CollectionChanged -= OnMessagesCollectionChanged;
        MessagesCollection.HandlerChanged -= OnMessagesCollectionHandlerChanged;
        SizeChanged -= OnPageSizeChanged;
#if ANDROID
        VoiceButton.HandlerChanged -= OnVoiceButtonHandlerChanged;
#endif
    }

    protected override bool OnBackButtonPressed()
    {
        _ = NavigateBackToConversationsAsync();
        return true;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        pageConversationId = TryGetGroupConversationId(query);
        RefreshActiveConversationLease();
        CancelRouteLoad();
        routeLoadCancellation = new CancellationTokenSource();
        _ = LoadFromRouteAsync(query, routeLoadCancellation.Token);
    }

    private static ConversationId? TryGetGroupConversationId(IDictionary<string, object> query)
    {
        if (!query.TryGetValue("groupId", out var raw) || string.IsNullOrWhiteSpace(raw?.ToString()))
        {
            return null;
        }

        try
        {
            return ConversationId.Parse(raw.ToString()!);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private void RefreshActiveConversationLease()
    {
        activeConversationLease?.Dispose();
        activeConversationLease = isPageActive && pageConversationId is { } conversationId
            ? activeConversationTracker.ActivateConversation(conversationId)
            : null;
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
            QueueVisibleImagePreviews();
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
        cancellationToken.ThrowIfCancellationRequested();
        var hasMessages = await MainThread.InvokeOnMainThreadAsync(() =>
        {
            ConfigureMessageListPlatformView();
            if (viewModel.Messages.Count == 0)
            {
                MessagesCollection.Opacity = 1;
                return false;
            }

            MessagesCollection.Opacity = 0;
            return true;
        });
        if (!hasMessages)
        {
            return;
        }

        try
        {
            await Task.Delay(InitialMessageLayoutDelay, cancellationToken).ConfigureAwait(false);
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                CancelPendingScrollToEnd();
                ScrollMessagesToEnd(animate: false, force: true);
                QueueVisibleImagePreviews();
            });
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                await MainThread.InvokeOnMainThreadAsync(() => MessagesCollection.Opacity = 1);
            }
        }
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        await NavigateBackToConversationsAsync();
    }

    private static Task NavigateBackToConversationsAsync() =>
        Shell.Current.GoToAsync("..", animate: false);

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

    private void OnContactProfileChanged(object? sender, SessionId contactId)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (!viewModel.IsBusy)
            {
                await viewModel.RefreshContactDisplayNamesAsync();
            }
        });
    }

    private void UpdateNetworkUi()
    {
        if (NetworkBanner is not null && NetworkBannerText is not null)
        {
            NetworkBanner.IsVisible = !networkStatusService.IsConnected;
            NetworkBannerText.Text = networkStatusService.ConnectionLabel;
        }
    }

    private void OnGroupMenuClicked(object? sender, EventArgs e)
    {
        ChatMenuOverlay.IsVisible = true;
    }

    private void OnCloseChatMenu(object? sender, TappedEventArgs e) => ChatMenuOverlay.IsVisible = false;

    private void OnSearchMessagesClicked(object? sender, TappedEventArgs e)
    {
        ChatMenuOverlay.IsVisible = false;
        ShowMessageSearch();
    }

    private void OnToggleMembersClicked(object? sender, TappedEventArgs e)
    {
        ChatMenuOverlay.IsVisible = false;
        ToggleMembersPanel();
    }

    private void ToggleMembersPanel()
    {
        MembersPanel.IsVisible = !MembersPanel.IsVisible;
    }

    private void ShowMessageSearch()
    {
        MessageSearchBar.IsVisible = true;
        RefreshMessageSearch(scrollToCurrent: !string.IsNullOrWhiteSpace(MessageSearchEntry.Text));
        MessageSearchEntry.Focus();
    }

    private void OnMessageSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        messageSearchIndex = -1;
        RefreshMessageSearch();
    }

    private void OnMessageSearchSubmitted(object? sender, EventArgs e) =>
        MoveMessageSearch(1);

    private void OnMessageSearchPreviousClicked(object? sender, TappedEventArgs e) =>
        MoveMessageSearch(-1);

    private void OnMessageSearchNextClicked(object? sender, TappedEventArgs e) =>
        MoveMessageSearch(1);

    private void OnCloseMessageSearchClicked(object? sender, TappedEventArgs e)
    {
        MessageSearchBar.IsVisible = false;
        MessageSearchEntry.Unfocus();
        MessageSearchEntry.Text = string.Empty;
        MessageSearchCountLabel.Text = string.Empty;
        messageSearchMatches.Clear();
        messageSearchIndex = -1;
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

    private void OnVoicePointerPressed(object? sender, PointerEventArgs e)
    {
        if (OperatingSystem.IsAndroid())
        {
            return;
        }

        if (voicePointerActive || !viewModel.ShowVoiceButton)
        {
            return;
        }

        BeginVoiceRecordingGesture(e.GetPosition(PageLayout) ?? new Point(0, 0));
    }

    private void OnVoicePointerMoved(object? sender, PointerEventArgs e)
    {
        if (OperatingSystem.IsAndroid())
        {
            return;
        }

        if (!voicePointerActive)
        {
            return;
        }

        var point = e.GetPosition(PageLayout);
        if (point is null)
        {
            return;
        }

        var deltaX = point.Value.X - voicePointerStart.X;
        var cancelProgress = Math.Clamp(-deltaX / VoiceCancelSwipeThreshold, 0, 1);
        voiceCancelBySwipe = cancelProgress >= 1;
        ApplyVoiceRecordingGestureProgress(cancelProgress);
    }

    private async void OnVoicePointerReleased(object? sender, PointerEventArgs e)
    {
        if (OperatingSystem.IsAndroid())
        {
            return;
        }

        await FinishVoiceRecordingGestureAsync();
    }

    private void OnVoicePanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        if (OperatingSystem.IsAndroid())
        {
            return;
        }

        if (!voicePointerActive)
        {
            return;
        }

        if (e.StatusType is GestureStatus.Running or GestureStatus.Completed or GestureStatus.Canceled)
        {
            var cancelProgress = Math.Clamp(-e.TotalX / VoiceCancelSwipeThreshold, 0, 1);
            voiceCancelBySwipe = cancelProgress >= 1;
            ApplyVoiceRecordingGestureProgress(cancelProgress);
        }
    }

    private async Task ConfigureAutoRefreshAsync(CancellationToken cancellationToken)
    {
        var pushDriven = await syncPollingPolicy.IsPushDrivenSyncAvailableAsync();
        if (cancellationToken.IsCancellationRequested || pageActivityCancellation?.Token != cancellationToken)
        {
            return;
        }

        if (pushDriven)
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
            autoRefreshTimer.Interval = TimeSpan.FromSeconds(10);
            autoRefreshTimer.Tick += OnAutoRefreshTick;
        }

        autoRefreshTimer.Start();
    }

    private async void OnAutoRefreshTick(object? sender, EventArgs e)
    {
        if (pageActivityCancellation is not { } pageCancellation)
        {
            return;
        }

        var cancellationToken = pageCancellation.Token;
        if (viewModel.IsBusy || string.IsNullOrWhiteSpace(viewModel.GroupTitle) || refreshingMessages || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await RefreshCurrentGroupAsync(cancellationToken);
    }

    private void OnBackgroundSyncScheduled()
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (pageActivityCancellation is not { } pageCancellation)
            {
                return;
            }

            var cancellationToken = pageCancellation.Token;
            if (!viewModel.IsBusy &&
                !string.IsNullOrWhiteSpace(viewModel.GroupTitle) &&
                !refreshingMessages &&
                !cancellationToken.IsCancellationRequested)
            {
                await RefreshCurrentGroupAsync(cancellationToken);
            }
        });
    }

    private async Task RefreshCurrentGroupAsync(CancellationToken cancellationToken)
    {
        try
        {
            refreshingMessages = true;
            await viewModel.RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            refreshingMessages = false;
        }
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        hasLoadedImageMessages = e.Action switch
        {
            NotifyCollectionChangedAction.Reset => viewModel.Messages.Any(static item => item.IsImageMessage),
            NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Replace =>
                hasLoadedImageMessages || e.NewItems?.OfType<GroupChatMessageItem>().Any(static item => item.IsImageMessage) == true,
            NotifyCollectionChangedAction.Remove => viewModel.Messages.Any(static item => item.IsImageMessage),
            _ => hasLoadedImageMessages
        };

        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null && WasAppendedToEnd(e))
        {
            QueueImagePreviews(e.NewItems.OfType<GroupChatMessageItem>());
        }
        else if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            QueueVisibleImagePreviews();
        }

        if (MessageSearchBar.IsVisible)
        {
            RefreshMessageSearch(scrollToCurrent: false);
        }

        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
        {
            if (!didInitialScroll)
            {
                return;
            }

            if (e.Action == NotifyCollectionChangedAction.Add && !WasAppendedToEnd(e))
            {
                return;
            }

            var hasOutgoing = e.NewItems?
                .OfType<GroupChatMessageItem>()
                .Any(message => message.Direction == MessageDirection.Outgoing) == true;
            var force = hasOutgoing;
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
        if (hasLoadedImageMessages)
        {
            ScheduleVisibleImagePreviews(e.FirstVisibleItemIndex, e.LastVisibleItemIndex);
        }
        if (didInitialScroll && e.FirstVisibleItemIndex <= 2)
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

    private async void OnAttachmentTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not GroupChatMessageItem item)
        {
            return;
        }

        if (item.IsImageMessage)
        {
            await OpenInlineImageAsync(item);
            return;
        }

        ShowAttachmentActionSheet(item, item.Attachments);
    }

    private void QueueVisibleImagePreviews(int firstVisibleIndex = -1, int lastVisibleIndex = -1)
    {
        if (viewModel.Messages.Count == 0)
        {
            return;
        }

        var start = firstVisibleIndex >= 0
            ? Math.Max(0, firstVisibleIndex - 2)
            : Math.Max(0, viewModel.Messages.Count - 12);
        var end = lastVisibleIndex >= start
            ? Math.Min(viewModel.Messages.Count - 1, lastVisibleIndex + 2)
            : viewModel.Messages.Count - 1;
        QueueImagePreviews(viewModel.Messages.Skip(start).Take(end - start + 1));
    }

    private void ScheduleVisibleImagePreviews(int firstVisibleIndex, int lastVisibleIndex)
    {
        pendingPreviewFirstVisibleIndex = firstVisibleIndex;
        pendingPreviewLastVisibleIndex = lastVisibleIndex;
        if (imagePreviewDebounceTimer is null)
        {
            imagePreviewDebounceTimer = Dispatcher.CreateTimer();
            imagePreviewDebounceTimer.Interval = TimeSpan.FromMilliseconds(150);
            imagePreviewDebounceTimer.IsRepeating = false;
            imagePreviewDebounceTimer.Tick += OnImagePreviewDebounceTick;
        }

        if (!imagePreviewDebounceTimer.IsRunning)
        {
            imagePreviewDebounceTimer.Start();
        }
    }

    private void OnImagePreviewDebounceTick(object? sender, EventArgs e) =>
        QueueVisibleImagePreviews(pendingPreviewFirstVisibleIndex, pendingPreviewLastVisibleIndex);

    private void QueueImagePreviews(IEnumerable<GroupChatMessageItem> candidates)
    {
        foreach (var item in candidates)
        {
            var attachment = item.PrimaryImageAttachment;
            if (attachment is not null && !item.HasImagePreview)
            {
                pendingImagePreviews[attachment.AttachmentId] = item;
            }
        }

        if (!isLoadingImagePreviews && pendingImagePreviews.Count > 0)
        {
            _ = DrainImagePreviewQueueAsync();
        }
    }

    private async Task DrainImagePreviewQueueAsync()
    {
        if (isLoadingImagePreviews)
        {
            return;
        }

        try
        {
            isLoadingImagePreviews = true;
            while (pendingImagePreviews.Count > 0)
            {
                var queued = pendingImagePreviews.First();
                pendingImagePreviews.Remove(queued.Key);
                var item = queued.Value;
                var attachment = item.PrimaryImageAttachment;
                if (attachment is null || item.HasImagePreview)
                {
                    continue;
                }

                try
                {
                    var file = AttachmentOpenService.TryGetCachedFile(attachment);
                    if (file is null && attachmentFiles.IsEnabled && attachment.RemoteUri is not null)
                    {
                        var cancellationToken = pageActivityCancellation?.Token ?? CancellationToken.None;
                        file = await AttachmentOpenService.DownloadToCacheAsync(
                            attachment,
                            attachmentFiles,
                            cancellationToken);
                    }

                    if (file is not null)
                    {
                        item.SetImagePreviewPath(file.Path);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    CrashDiagnostics.LogException("GroupChatPage.ImagePreview", ex);
                }
            }
        }
        finally
        {
            isLoadingImagePreviews = false;
        }
    }

    private void OnMessagesCollectionHandlerChanged(object? sender, EventArgs e) =>
        ConfigureMessageListPlatformView();

    private void ConfigureMessageListPlatformView()
    {
#if ANDROID
        if (MessagesCollection.Handler?.PlatformView is RecyclerView recyclerView)
        {
            recyclerView.SetItemAnimator(null);
            recyclerView.HasFixedSize = true;
            recyclerView.SetItemViewCacheSize(12);
            if (recyclerView.GetLayoutManager() is LinearLayoutManager layoutManager)
            {
                layoutManager.StackFromEnd = true;
                layoutManager.InitialPrefetchItemCount = 8;
            }

        }
#endif
    }

    private async Task OpenInlineImageAsync(GroupChatMessageItem item)
    {
        var attachment = item.PrimaryImageAttachment;
        if (attachment is null)
        {
            return;
        }

        try
        {
            var file = AttachmentOpenService.TryGetCachedFile(attachment)
                ?? await AttachmentOpenService.DownloadToCacheAsync(attachment, attachmentFiles);
            item.SetImagePreviewPath(file.Path);
            ImageViewerOverlay.GetRequiredView<Label>("Title").Text = item.HasMultipleImages
                ? $"1 из {item.Attachments.Count}"
                : "Фото";
            ImageViewerOverlay.GetRequiredView<Image>("Image").Source = ImageSource.FromFile(file.Path);
            imageViewerAttachment = attachment;
            imageViewerMessage = item;
            ImageViewerOverlay.IsVisible = true;
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Фото", ex.Message, "OK");
        }
    }

    private void OnCloseImageViewer(object? sender, TappedEventArgs e)
    {
        ImageViewerOverlay.IsVisible = false;
        ImageViewerOverlay.GetRequiredView<Image>("Image").Source = null;
        imageViewerAttachment = null;
        imageViewerMessage = null;
    }

    private async void OnVoiceMessageTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not GroupChatMessageItem item || item.Attachments.Count == 0)
        {
            return;
        }

        try
        {
            var attachment = item.Attachments[0];
            if (string.Equals(activeVoiceAttachmentId, attachment.AttachmentId, StringComparison.Ordinal)
                && voicePlayback.Snapshot.IsPlaying)
            {
                voicePlayback.Stop();
                ApplyVoicePlaybackSnapshot(VoicePlaybackSnapshot.Stopped);
                return;
            }

            ClearActiveVoicePlayback();
            activeVoiceAttachmentId = attachment.AttachmentId;
            item.SetVoicePlayback(isPlaying: true, progress: 0, position: TimeSpan.Zero);

            await voicePlayback.ToggleAsync(attachment, attachmentFiles);
            ApplyVoicePlaybackSnapshot(voicePlayback.Snapshot);
            EnsureVoicePlaybackTimer();
        }
        catch (Exception ex)
        {
            ClearActiveVoicePlayback();
            await DisplayAlertAsync("Голосовое сообщение", ex.Message, "OK");
        }
    }

    private void ShowAttachmentActionSheet(GroupChatMessageItem? message, IReadOnlyList<AttachmentMetadata> attachments)
    {
        selectedAttachment = attachments.Count == 0 ? null : attachments[0];
        selectedAttachmentMessage = message;
        if (selectedAttachment is null)
        {
            return;
        }

        AttachmentActionOverlay.GetRequiredView<Label>("Title").Text = selectedAttachment.FileName;
        AttachmentActionOverlay.GetRequiredView<Label>("Subtitle").Text = DescribeAttachment(selectedAttachment, attachments.Count);
        AttachmentActionOverlay.GetRequiredView<Grid>("ReplyRow").IsVisible = message is not null;
        AttachmentActionOverlay.GetRequiredView<Grid>("DeleteRow").IsVisible = message is not null;
        AttachmentActionOverlay.GetRequiredView<Grid>("SaveRow").IsVisible = true;
        AttachmentActionOverlay.IsVisible = true;
    }

    private void OnCloseAttachmentActionSheet(object? sender, TappedEventArgs e)
    {
        AttachmentActionOverlay.IsVisible = false;
        selectedAttachment = null;
        selectedAttachmentMessage = null;
    }

    private async void OnOpenAttachmentClicked(object? sender, TappedEventArgs e)
    {
        if (selectedAttachment is not { } attachment)
        {
            return;
        }

        AttachmentActionOverlay.IsVisible = false;
        selectedAttachment = null;
        selectedAttachmentMessage = null;
        await AttachmentOpenService.OpenAsync(this, [attachment], attachmentFiles);
    }

    private async void OnSaveAttachmentClicked(object? sender, TappedEventArgs e)
    {
        if (selectedAttachment is not { } attachment)
        {
            return;
        }

        AttachmentActionOverlay.IsVisible = false;
        selectedAttachment = null;
        selectedAttachmentMessage = null;
        try
        {
            await AttachmentOpenService.SaveAsync(attachment, attachmentFiles);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Вложение", ex.Message, "OK");
        }
    }

    private async void OnShareAttachmentClicked(object? sender, TappedEventArgs e)
    {
        if (selectedAttachment is not { } attachment)
        {
            return;
        }

        AttachmentActionOverlay.IsVisible = false;
        selectedAttachment = null;
        selectedAttachmentMessage = null;
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
        selectedAttachmentMessage = null;
        await Clipboard.Default.SetTextAsync(attachment.FileName);
    }

    private void OnReplyFromAttachmentClicked(object? sender, TappedEventArgs e)
    {
        if (selectedAttachmentMessage is not { } message)
        {
            return;
        }

        AttachmentActionOverlay.IsVisible = false;
        selectedAttachment = null;
        selectedAttachmentMessage = null;
        viewModel.BeginReply(message);
        DraftEntry.Focus();
    }

    private async void OnDeleteFromAttachmentClicked(object? sender, TappedEventArgs e)
    {
        if (selectedAttachmentMessage is not { } message)
        {
            return;
        }

        AttachmentActionOverlay.IsVisible = false;
        selectedAttachment = null;
        selectedAttachmentMessage = null;
        await DeleteMessageWithConfirmationAsync(message);
    }

    private void OnImageViewerMoreClicked(object? sender, TappedEventArgs e)
    {
        if (imageViewerAttachment is not { } attachment)
        {
            return;
        }

        var message = imageViewerMessage;
        ShowAttachmentActionSheet(message, [attachment]);
    }

    private void ApplyAndroidSafeAreaCompensation()
    {
        expandedPageHeight = Math.Max(expandedPageHeight, Height);
        var keyboardVisibleByResize = expandedPageHeight - Height > 100;
        var keyboardPadding = keyboardVisibleByResize ? 0 : Math.Max(0, keyboardBottomInset);
        PageLayout.Margin = Thickness.Zero;
        PageLayout.Padding = new Thickness(0, 0, 0, keyboardPadding);
    }

    private void OnPageSizeChanged(object? sender, EventArgs e) =>
        ApplyAndroidSafeAreaCompensation();

    private void OnKeyboardInsetChanged(double bottomInset)
    {
        keyboardBottomInset = bottomInset;
        MainThread.BeginInvokeOnMainThread(ApplyAndroidSafeAreaCompensation);
    }

    private void OnMessageContextRequested(object? sender, EventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not GroupChatMessageItem message)
        {
            return;
        }

        ShowMessageMenu(message);
    }

    private void ShowMessageMenu(GroupChatMessageItem item)
    {
        selectedMessage = item;
        var attachment = PrimaryActionAttachment(item);
        var hasAttachment = attachment is not null;
        var hasText = item.HasVisibleBody;
        MessageMenuOverlay.GetRequiredView<Grid>("OpenMediaRow").IsVisible = item.IsImageMessage;
        MessageMenuOverlay.GetRequiredView<Grid>("SaveAttachmentRow").IsVisible = hasAttachment;
        MessageMenuOverlay.GetRequiredView<Grid>("ShareAttachmentRow").IsVisible = hasAttachment;
        MessageMenuOverlay.GetRequiredView<Grid>("CopyAttachmentNameRow").IsVisible = hasAttachment;
        MessageMenuOverlay.GetRequiredView<Grid>("CopyTextRow").IsVisible = hasText;
        MessageMenuOverlay.IsVisible = true;
    }

    private void OnCloseMessageMenu(object? sender, TappedEventArgs e)
    {
        MessageMenuOverlay.IsVisible = false;
        selectedMessage = null;
    }

    private async void OnOpenSelectedMessageAttachment(object? sender, TappedEventArgs e)
    {
        if (selectedMessage is not { } message)
        {
            return;
        }

        MessageMenuOverlay.IsVisible = false;
        selectedMessage = null;
        if (message.IsImageMessage)
        {
            await OpenInlineImageAsync(message);
            return;
        }

        await AttachmentOpenService.OpenAsync(this, message.Attachments, attachmentFiles);
    }

    private async void OnSaveSelectedMessageAttachment(object? sender, TappedEventArgs e)
    {
        if (selectedMessage is not { } message || PrimaryActionAttachment(message) is not { } attachment)
        {
            return;
        }

        MessageMenuOverlay.IsVisible = false;
        selectedMessage = null;
        try
        {
            await AttachmentOpenService.SaveAsync(attachment, attachmentFiles);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Вложение", ex.Message, "OK");
        }
    }

    private async void OnShareSelectedMessageAttachment(object? sender, TappedEventArgs e)
    {
        if (selectedMessage is not { } message || PrimaryActionAttachment(message) is not { } attachment)
        {
            return;
        }

        MessageMenuOverlay.IsVisible = false;
        selectedMessage = null;
        try
        {
            await AttachmentOpenService.ShareAsync(attachment, attachmentFiles);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Вложение", ex.Message, "OK");
        }
    }

    private async void OnCopySelectedAttachmentName(object? sender, TappedEventArgs e)
    {
        if (selectedMessage is not { } message || PrimaryActionAttachment(message) is not { } attachment)
        {
            return;
        }

        await Clipboard.Default.SetTextAsync(attachment.FileName);
        MessageMenuOverlay.IsVisible = false;
        selectedMessage = null;
    }

    private async void OnReactionClicked(object? sender, EventArgs e)
    {
        if (selectedMessage is null || sender is not Button { CommandParameter: string emoji })
        {
            return;
        }

        var messageId = selectedMessage.Id;
        MessageMenuOverlay.IsVisible = false;
        selectedMessage = null;
        await viewModel.ToggleReactionAsync(messageId, emoji);
    }

    private async void OnReactionChipTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is MessageReactionChip chip)
        {
            await viewModel.ToggleReactionAsync(chip.MessageId, chip.Emoji);
        }
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

    private async void OnDeleteSelectedMessage(object? sender, TappedEventArgs e)
    {
        if (selectedMessage is not { } message)
        {
            return;
        }

        MessageMenuOverlay.IsVisible = false;
        selectedMessage = null;
        await DeleteMessageWithConfirmationAsync(message);
    }

    private async Task DeleteMessageWithConfirmationAsync(GroupChatMessageItem message)
    {
        if (!await DisplayAlertAsync("Удалить сообщение?", "Сообщение будет удалено с этого устройства.", "Удалить", "Отмена"))
        {
            return;
        }

        await viewModel.DeleteMessageAsync(message);
    }

    private static AttachmentMetadata? PrimaryActionAttachment(GroupChatMessageItem message) =>
        message.PrimaryImageAttachment ?? (message.Attachments.Count == 0 ? null : message.Attachments[0]);

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

    private async Task StartVoiceRecordingGestureAsync()
    {
        await viewModel.StartVoiceRecordingAsync();
        if (viewModel.IsRecordingVoice)
        {
            StartRecordingUiTimer();
        }
    }

    private void BeginVoiceRecordingGesture(Point startPoint)
    {
        if (voicePointerActive || !viewModel.ShowVoiceButton)
        {
            return;
        }

        voicePointerActive = true;
        voiceCancelBySwipe = false;
        voicePointerStart = startPoint;
        ResetVoiceRecordingGestureUi();
        voiceGestureStartTask = StartVoiceRecordingGestureAsync();
    }

    private void UpdateVoiceRecordingGesture(Point currentPoint)
    {
        if (!voicePointerActive)
        {
            return;
        }

        var deltaX = currentPoint.X - voicePointerStart.X;
        var cancelProgress = Math.Clamp(-deltaX / VoiceCancelSwipeThreshold, 0, 1);
        voiceCancelBySwipe = cancelProgress >= 1;
        ApplyVoiceRecordingGestureProgress(cancelProgress);
    }

    private async Task FinishVoiceRecordingGestureAsync(bool forceCancel = false)
    {
        if (forceCancel)
        {
            Interlocked.Exchange(ref voicePageExitCancellationRequested, 1);
        }

        await voiceGestureCompletionGate.WaitAsync();
        try
        {
            if (!voicePointerActive &&
                voiceGestureStartTask is null &&
                !forceCancel &&
                !viewModel.IsRecordingVoice)
            {
                return;
            }

            voicePointerActive = false;
            var startTask = voiceGestureStartTask;
            voiceGestureStartTask = null;
            if (startTask is not null)
            {
                await startTask;
            }

            StopRecordingUiTimer();
            ResetVoiceRecordingGestureUi();
            if (!viewModel.IsRecordingVoice)
            {
                return;
            }

            if (voiceCancelBySwipe || Volatile.Read(ref voicePageExitCancellationRequested) != 0)
            {
                await viewModel.CancelVoiceRecordingAsync();
            }
            else
            {
                await viewModel.StopVoiceRecordingAndSendAsync();
            }
        }
        catch (Exception exception)
        {
            CrashDiagnostics.LogException("GroupChat.VoiceGestureFinish", exception);
        }
        finally
        {
            voiceCancelBySwipe = false;
            if (!viewModel.IsRecordingVoice)
            {
                Interlocked.Exchange(ref voicePageExitCancellationRequested, 0);
            }

            voiceGestureCompletionGate.Release();
        }
    }

    private void ApplyVoiceRecordingGestureProgress(double progress)
    {
        VoiceRecordingPanel.TranslationX = -VoiceCancelPanelTranslation * progress;
        VoiceCancelPill.Opacity = 1 - (0.55 * progress);
        VoiceSlideHintLabel.Text = progress >= 1
            ? "Отпустите, чтобы отменить"
            : "Сдвиньте влево для отмены";
        VoiceSlideHintLabel.TextColor = progress >= 1
            ? ResourceColor("DangerColor", Colors.IndianRed)
            : ResourceColor("TextSecondary", Colors.Gray);
    }

    private void ResetVoiceRecordingGestureUi()
    {
        VoiceRecordingPanel.TranslationX = 0;
        VoiceCancelPill.Opacity = 1;
        VoiceSlideHintLabel.Text = "Сдвиньте влево для отмены";
        VoiceSlideHintLabel.TextColor = ResourceColor("TextSecondary", Colors.Gray);
    }

    private void EnsureVoicePlaybackTimer()
    {
        if (!voicePlayback.Snapshot.IsPlaying)
        {
            StopVoicePlaybackTimer();
            return;
        }

        voicePlaybackTimer ??= Dispatcher.CreateTimer();
        voicePlaybackTimer.Interval = TimeSpan.FromMilliseconds(200);
        voicePlaybackTimer.Tick -= OnVoicePlaybackTick;
        voicePlaybackTimer.Tick += OnVoicePlaybackTick;
        voicePlaybackTimer.Start();
    }

    private void StopVoicePlaybackTimer()
    {
        voicePlaybackTimer?.Stop();
    }

    private void OnVoicePlaybackTick(object? sender, EventArgs e) =>
        ApplyVoicePlaybackSnapshot(voicePlayback.Snapshot);

    private void ApplyVoicePlaybackSnapshot(VoicePlaybackSnapshot snapshot)
    {
        if (!string.Equals(activeVoiceAttachmentId, snapshot.AttachmentId, StringComparison.Ordinal))
        {
            ClearVoicePlayback(activeVoiceAttachmentId);
        }

        if (snapshot.AttachmentId is null)
        {
            activeVoiceAttachmentId = null;
            StopVoicePlaybackTimer();
            return;
        }

        activeVoiceAttachmentId = snapshot.AttachmentId;
        var item = FindVoiceMessageItem(snapshot.AttachmentId);
        item?.SetVoicePlayback(snapshot.IsPlaying, snapshot.Progress, snapshot.Position);

        if (!snapshot.IsPlaying)
        {
            ClearVoicePlayback(snapshot.AttachmentId);
            activeVoiceAttachmentId = null;
            StopVoicePlaybackTimer();
        }
    }

    private GroupChatMessageItem? FindVoiceMessageItem(string attachmentId) =>
        viewModel.Messages.FirstOrDefault(message =>
            string.Equals(message.VoiceAttachmentId, attachmentId, StringComparison.Ordinal));

    private void ClearActiveVoicePlayback()
    {
        ClearVoicePlayback(activeVoiceAttachmentId);
        activeVoiceAttachmentId = null;
    }

    private void ClearVoicePlayback(string? attachmentId)
    {
        if (attachmentId is null)
        {
            return;
        }

        FindVoiceMessageItem(attachmentId)?.ClearVoicePlayback();
    }

    private static Color ResourceColor(string key, Color fallback) =>
        Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color color
            ? color
            : fallback;

#if ANDROID
    private void OnAndroidVoiceGesture(object? sender, AndroidVoiceGestureEventArgs e)
    {
        if (!voicePointerActive)
        {
            return;
        }

        if (e.Action == MotionEventActions.Move)
        {
            UpdateVoiceRecordingGesture(new Point(e.RawX, e.RawY));
            return;
        }

        if (e.Action == MotionEventActions.Up)
        {
            UpdateVoiceRecordingGesture(new Point(e.RawX, e.RawY));
            _ = FinishVoiceRecordingGestureAsync();
            return;
        }

        if (e.Action == MotionEventActions.Cancel)
        {
            voiceCancelBySwipe = true;
            ApplyVoiceRecordingGestureProgress(1);
            _ = FinishVoiceRecordingGestureAsync();
        }
    }

    private void OnVoiceButtonHandlerChanged(object? sender, EventArgs e)
    {
        if (voiceButtonPlatformView is not null)
        {
            voiceButtonPlatformView.Touch -= OnVoiceButtonPlatformTouch;
        }

        voiceButtonPlatformView = VoiceButton.Handler?.PlatformView as AndroidView;
        if (voiceButtonPlatformView is not null)
        {
            voiceButtonPlatformView.Clickable = true;
            voiceButtonPlatformView.LongClickable = false;
            voiceButtonPlatformView.Touch += OnVoiceButtonPlatformTouch;
        }
    }

    private void OnVoiceButtonPlatformTouch(object? sender, AndroidView.TouchEventArgs e)
    {
        var motion = e.Event;
        if (motion is null)
        {
            return;
        }

        e.Handled = true;
        switch (motion.ActionMasked)
        {
            case MotionEventActions.Down:
                voiceButtonPlatformView?.Parent?.RequestDisallowInterceptTouchEvent(true);
                BeginVoiceRecordingGesture(new Point(motion.RawX, motion.RawY));
                break;

            case MotionEventActions.Move:
                UpdateVoiceRecordingGesture(new Point(motion.RawX, motion.RawY));
                break;

            case MotionEventActions.Up:
                UpdateVoiceRecordingGesture(new Point(motion.RawX, motion.RawY));
                voiceButtonPlatformView?.Parent?.RequestDisallowInterceptTouchEvent(false);
                _ = FinishVoiceRecordingGestureAsync();
                break;

            case MotionEventActions.Cancel:
                voiceCancelBySwipe = true;
                ApplyVoiceRecordingGestureProgress(1);
                voiceButtonPlatformView?.Parent?.RequestDisallowInterceptTouchEvent(false);
                _ = FinishVoiceRecordingGestureAsync();
                break;
        }
    }
#endif

    private static string DescribeAttachment(AttachmentMetadata attachment, int totalCount)
    {
        var kind = attachment.ContentType switch
        {
            var value when !attachment.IsDocument && value.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => "Фото",
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
        var cancellationToken = scrollRequest.Token;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(80), cancellationToken).ConfigureAwait(false);
            if (pendingScrollToEnd != scrollRequest || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var animate = pendingScrollAnimate;
            var force = pendingScrollForce;
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (pendingScrollToEnd == scrollRequest && !cancellationToken.IsCancellationRequested)
                {
                    ScrollMessagesToEnd(animate, force);
                }
            });

        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (pendingScrollToEnd == scrollRequest)
            {
                pendingScrollToEnd = null;
                pendingScrollAnimate = false;
                pendingScrollForce = false;
            }

            scrollRequest.Dispose();
        }
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

    private void RefreshMessageSearch(bool scrollToCurrent = true)
    {
        var current = messageSearchIndex >= 0 && messageSearchIndex < messageSearchMatches.Count
            ? messageSearchMatches[messageSearchIndex]
            : null;
        var query = MessageSearchEntry.Text?.Trim();
        messageSearchMatches.Clear();

        if (string.IsNullOrWhiteSpace(query))
        {
            messageSearchIndex = -1;
            MessageSearchCountLabel.Text = string.Empty;
            return;
        }

        foreach (var message in viewModel.Messages)
        {
            if (MessageMatchesSearch(message, query))
            {
                messageSearchMatches.Add(message);
            }
        }

        if (messageSearchMatches.Count == 0)
        {
            messageSearchIndex = -1;
            MessageSearchCountLabel.Text = "0/0";
            return;
        }

        var preservedIndex = current is null ? -1 : messageSearchMatches.IndexOf(current);
        messageSearchIndex = preservedIndex >= 0 ? preservedIndex : messageSearchMatches.Count - 1;
        UpdateMessageSearchCount();

        if (scrollToCurrent)
        {
            ScrollToMessageSearchMatch();
        }
    }

    private void MoveMessageSearch(int delta)
    {
        if (messageSearchMatches.Count == 0)
        {
            RefreshMessageSearch();
        }

        if (messageSearchMatches.Count == 0)
        {
            return;
        }

        messageSearchIndex = (messageSearchIndex + delta + messageSearchMatches.Count) % messageSearchMatches.Count;
        UpdateMessageSearchCount();
        ScrollToMessageSearchMatch();
    }

    private void UpdateMessageSearchCount()
    {
        MessageSearchCountLabel.Text = messageSearchIndex >= 0
            ? $"{messageSearchIndex + 1}/{messageSearchMatches.Count}"
            : "0/0";
    }

    private void ScrollToMessageSearchMatch()
    {
        if (messageSearchIndex < 0 || messageSearchIndex >= messageSearchMatches.Count)
        {
            return;
        }

        CancelPendingScrollToEnd();
        shouldStickToEnd = false;
        MessagesCollection.ScrollTo(messageSearchMatches[messageSearchIndex], position: ScrollToPosition.Center, animate: true);
    }

    private static bool MessageMatchesSearch(GroupChatMessageItem message, string query) =>
        ContainsSearchText(message.Body, query)
        || ContainsSearchText(message.SenderLabel, query)
        || ContainsSearchText(message.ReplyPreview, query)
        || ContainsSearchText(message.AttachmentTitle, query)
        || ContainsSearchText(message.AttachmentSubtitle, query)
        || ContainsSearchText(message.AttachmentSummary, query);

    private static bool ContainsSearchText(string? value, string query) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private bool WasAppendedToEnd(NotifyCollectionChangedEventArgs e)
    {
        var addedCount = e.NewItems?.Count ?? 0;
        return e.NewStartingIndex < 0 || e.NewStartingIndex >= viewModel.Messages.Count - addedCount;
    }
}
