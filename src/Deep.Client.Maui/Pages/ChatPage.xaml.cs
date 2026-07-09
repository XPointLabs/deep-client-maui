using System.Collections.Specialized;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.Presentation;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Services;
using Microsoft.Maui.Storage;

#if ANDROID
using Android.Views;
using AndroidView = Android.Views.View;
#endif

namespace Deep.Client.Maui.Pages;

public partial class ChatPage : ContentPage, IQueryAttributable
{
    private const double VoiceCancelSwipeThreshold = 84;
    private const double VoiceCancelPanelTranslation = 36;
    private const double MessageLongPressMoveThreshold = 14;
    private static readonly TimeSpan MessageLongPressDelay = TimeSpan.FromMilliseconds(420);
    private IDispatcherTimer? autoReceiveTimer;
    private readonly ChatViewModel viewModel;
    private readonly INetworkStatusService networkStatusService;
    private readonly IAttachmentFileTransport attachmentFiles;
    private readonly CallSessionCoordinator callCoordinator;
    private readonly SyncPollingPolicy syncPollingPolicy;
    private readonly VoiceMessagePlaybackService voicePlayback = new();
    private CancellationTokenSource? pendingScrollToEnd;
    private CancellationTokenSource? routeLoadCancellation;
    private CancellationTokenSource? pageActivityCancellation;
    private IDisposable? keyboardInsetSubscription;
    private IDispatcherTimer? voiceRecordingTimer;
    private IDispatcherTimer? voicePlaybackTimer;
    private bool pendingScrollAnimate;
    private bool pendingScrollForce;
    private bool isLoadingOlderMessages;
    private bool shouldStickToEnd = true;
    private bool didInitialScroll;
    private bool isLoadingImagePreviews;
    private bool imagePreviewReloadRequested;
    private readonly HashSet<string> loadingImagePreviewAttachmentIds = new(StringComparer.Ordinal);
    private double expandedPageHeight;
    private double keyboardBottomInset;
    private DateTimeOffset voiceRecordingStartedAt;
    private Point voicePointerStart;
    private bool voicePointerActive;
    private bool voiceCancelBySwipe;
    private Task? voiceGestureStartTask;
    private IDispatcherTimer? messageLongPressTimer;
    private Point messagePointerStart;
    private ChatMessageItem? pendingLongPressMessage;
    private bool suppressNextAttachmentTap;
    private bool receivingMessages;
    private string? activeVoiceAttachmentId;
    private ChatMessageItem? selectedMessage;
    private readonly List<ChatMessageItem> messageSearchMatches = [];
    private int messageSearchIndex = -1;
    private AttachmentMetadata? selectedAttachment;
    private ChatMessageItem? selectedAttachmentMessage;
    private AttachmentMetadata? imageViewerAttachment;
    private ChatMessageItem? imageViewerMessage;
#if ANDROID
    private AndroidView? voiceButtonPlatformView;
#endif

    public ChatPage(
        ChatViewModel viewModel,
        INetworkStatusService networkStatusService,
        IAttachmentFileTransport attachmentFiles,
        CallSessionCoordinator callCoordinator,
        SyncPollingPolicy syncPollingPolicy)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        this.networkStatusService = networkStatusService;
        this.attachmentFiles = attachmentFiles;
        this.callCoordinator = callCoordinator;
        this.syncPollingPolicy = syncPollingPolicy;
        BindingContext = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
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
        ApplyComposerPreferences();
        UpdateNetworkUi();

        _ = EnsureImagePreviewsAsync();
        _ = ConfigureAutoReceiveAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        UnsubscribePageEvents();
        BackgroundSyncBridge.SyncScheduled -= OnBackgroundSyncScheduled;
        keyboardInsetSubscription?.Dispose();
        keyboardInsetSubscription = null;
        keyboardBottomInset = 0;
        ApplyAndroidSafeAreaCompensation();
        autoReceiveTimer?.Stop();
        pageActivityCancellation?.Cancel();
        pageActivityCancellation?.Dispose();
        pageActivityCancellation = null;
        StopRecordingUiTimer();
        StopVoicePlaybackTimer();
        ClearActiveVoicePlayback();
        voicePlayback.Stop();
        CancelRouteLoad();
        CancelPendingScrollToEnd();
#if ANDROID
        AndroidVoiceGestureRouter.Touch -= OnAndroidVoiceGesture;
#endif
    }

    private void SubscribePageEvents()
    {
        UnsubscribePageEvents();
        networkStatusService.StatusChanged += OnNetworkStatusChanged;
        ContactProfileUpdateBus.ContactChanged += OnContactProfileChanged;
        viewModel.Messages.CollectionChanged += OnMessagesCollectionChanged;
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
        CancelRouteLoad();
        routeLoadCancellation = new CancellationTokenSource();
        _ = LoadFromRouteAsync(query, routeLoadCancellation.Token);
    }

    private async Task LoadFromRouteAsync(IDictionary<string, object> query, CancellationToken cancellationToken)
    {
        try
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

            var displayName = query.TryGetValue("displayName", out var displayNameRaw)
                ? displayNameRaw?.ToString()
                : null;

            shouldStickToEnd = true;
            didInitialScroll = false;

            var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var stepStopwatch = System.Diagnostics.Stopwatch.StartNew();
            viewModel.PrepareRoute(SessionId.Parse(sessionId), displayName);
            UpdateHeaderAvatarUi();
            MessagesCollection.Opacity = 1;
            if (viewModel.Messages.Count > 0)
            {
                QueueScrollToEnd(animate: false, force: true);
            }

            CrashDiagnostics.LogInfo(
                "Perf.Chat",
                $"PrepareRoute messages={viewModel.Messages.Count} elapsedMs={stepStopwatch.ElapsedMilliseconds}");

            stepStopwatch.Restart();
            await viewModel.OpenFromRouteAsync(sessionId, displayName, cancellationToken);
            UpdateHeaderAvatarUi();
            _ = EnsureImagePreviewsAsync();
            CrashDiagnostics.LogInfo(
                "Perf.Chat",
                $"OpenFromRoute messages={viewModel.Messages.Count} elapsedMs={stepStopwatch.ElapsedMilliseconds}");

            stepStopwatch.Restart();
            await RevealInitialMessagesAsync(cancellationToken);
            CrashDiagnostics.LogInfo(
                "Perf.Chat",
                $"Reveal messages={viewModel.Messages.Count} elapsedMs={stepStopwatch.ElapsedMilliseconds} totalMs={totalStopwatch.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            CrashDiagnostics.LogException("ChatPage.LoadFromRouteAsync", ex);
        }
    }

    private async Task RevealInitialMessagesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            MessagesCollection.Opacity = 1;
            QueueScrollToEnd(animate: false, force: true);
        });
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        await NavigateBackToConversationsAsync();
    }

    private static Task NavigateBackToConversationsAsync() =>
        Shell.Current.GoToAsync($"//{ShellRouteCatalog.Conversations}", animate: false);

    private void OnInfoClicked(object? sender, EventArgs e)
    {
        ChatMenuOverlay.IsVisible = true;
    }

    private async void OnContactHeaderTapped(object? sender, TappedEventArgs e) =>
        await OpenContactProfileAsync();

    private void OnCloseChatMenu(object? sender, TappedEventArgs e) => ChatMenuOverlay.IsVisible = false;

    private void OnSearchMessagesClicked(object? sender, TappedEventArgs e)
    {
        ChatMenuOverlay.IsVisible = false;
        ShowMessageSearch();
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
        if (voicePointerActive || !viewModel.ShowVoiceButton)
        {
            return;
        }

        BeginVoiceRecordingGesture(e.GetPosition(PageLayout) ?? new Point(0, 0));
    }

    private void OnVoicePointerMoved(object? sender, PointerEventArgs e)
    {
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
        await FinishVoiceRecordingGestureAsync();
    }

    private void OnVoicePanUpdated(object? sender, PanUpdatedEventArgs e)
    {
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

    private async void OnContactInfoClicked(object? sender, TappedEventArgs e)
    {
        ChatMenuOverlay.IsVisible = false;
        await OpenContactProfileAsync();
    }

    private async Task OpenContactProfileAsync()
    {
        if (viewModel.Conversation is { } conversation)
        {
            await Shell.Current.GoToAsync(
                ShellRouteCatalog.ContactProfile,
                new ShellNavigationQueryParameters
                {
                    ["sessionId"] = conversation.Id.Value,
                    ["displayName"] = conversation.DisplayName
                });
        }
    }

    private async Task RefreshConversationChromeAsync()
    {
        try
        {
            await viewModel.RefreshConversationMetadataAsync();
            UpdateHeaderAvatarUi();
        }
        catch (Exception ex)
        {
            CrashDiagnostics.LogException("ChatPage.RefreshConversationChrome", ex);
        }
    }

    private void UpdateHeaderAvatarUi()
    {
        var conversation = viewModel.Conversation;
        var counterpart = viewModel.Counterpart;
        if (conversation is null || counterpart is null)
        {
            return;
        }

        HeaderAvatarInitial.Text = DeepDisplayName.AvatarInitial(viewModel.ConversationTitle, conversation.Id.Value);
        var avatarPath = ContactAvatarStore.GetAvatarPath(counterpart.Value, viewModel.IsSelfConversation);
        var hasAvatar = File.Exists(avatarPath);
        HeaderAvatarImage.Source = null;
        HeaderAvatarImage.IsVisible = hasAvatar;
        HeaderAvatarInitial.IsVisible = !hasAvatar;
        if (hasAvatar)
        {
            HeaderAvatarImage.Source = ImageSource.FromFile(avatarPath);
        }
    }

    private async void OnClearConversationClicked(object? sender, TappedEventArgs e)
    {
        ChatMenuOverlay.IsVisible = false;
        if (!await DisplayAlertAsync("Очистить историю?", "Сообщения будут удалены с этого устройства.", "Очистить", "Отмена"))
        {
            return;
        }

        await viewModel.ClearConversationAsync();
    }

    private async void OnDeleteConversationClicked(object? sender, TappedEventArgs e)
    {
        ChatMenuOverlay.IsVisible = false;
        if (!await DisplayAlertAsync("Удалить чат?", "История будет удалена, а диалог скрыт до нового сообщения.", "Удалить", "Отмена"))
        {
            return;
        }

        await viewModel.DeleteConversationAsync();
        await NavigateBackToConversationsAsync();
    }

    private void OnNetworkStatusChanged(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(UpdateNetworkUi);
    }

    private void OnContactProfileChanged(object? sender, SessionId contactId)
    {
        if (viewModel.Counterpart is { } counterpart && counterpart == contactId)
        {
            MainThread.BeginInvokeOnMainThread(() => _ = RefreshConversationChromeAsync());
        }
    }

    private void UpdateNetworkUi()
    {
        if (NetworkBanner is not null && NetworkBannerText is not null)
        {
            NetworkBanner.IsVisible = !networkStatusService.IsConnected;
            NetworkBannerText.Text = networkStatusService.ConnectionLabel;
        }
    }

    private async Task ConfigureAutoReceiveAsync()
    {
        if (await syncPollingPolicy.IsPushDrivenSyncAvailableAsync())
        {
            autoReceiveTimer?.Stop();
            return;
        }

        EnsureAutoReceive();
    }

    private void EnsureAutoReceive()
    {
        if (autoReceiveTimer is null)
        {
            autoReceiveTimer = Dispatcher.CreateTimer();
            autoReceiveTimer.Interval = TimeSpan.FromSeconds(10);
            autoReceiveTimer.Tick += OnAutoReceiveTick;
        }

        autoReceiveTimer.Start();
    }

    private async void OnAutoReceiveTick(object? sender, EventArgs e)
    {
        if (pageActivityCancellation is not { } pageCancellation)
        {
            return;
        }

        var cancellationToken = pageCancellation.Token;
        if (viewModel.IsBusy || viewModel.Conversation is null || receivingMessages || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await ReceiveCurrentConversationAsync(cancellationToken);
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
            if (!viewModel.IsBusy && viewModel.Conversation is not null && !receivingMessages && !cancellationToken.IsCancellationRequested)
            {
                await ReceiveCurrentConversationAsync(cancellationToken);
            }
        });
    }

    private async Task ReceiveCurrentConversationAsync(CancellationToken cancellationToken)
    {
        try
        {
            receivingMessages = true;
            await viewModel.ReceiveAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            receivingMessages = false;
        }
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            _ = EnsureImagePreviewsAsync(e.NewItems.OfType<ChatMessageItem>());
        }
        else if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _ = EnsureImagePreviewsAsync();
        }

        if (MessageSearchBar.IsVisible)
        {
            RefreshMessageSearch(scrollToCurrent: false);
        }

        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
        {
            if (e.Action == NotifyCollectionChangedAction.Add && !WasAppendedToEnd(e))
            {
                return;
            }

            var hasOutgoing = e.NewItems?
                .OfType<ChatMessageItem>()
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

    private async void OnAttachmentTapped(object? sender, TappedEventArgs e)
    {
        CancelMessageLongPress();
        if (suppressNextAttachmentTap)
        {
            suppressNextAttachmentTap = false;
            return;
        }

        if ((sender as BindableObject)?.BindingContext is not ChatMessageItem item)
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

    private async Task EnsureImagePreviewsAsync(IEnumerable<ChatMessageItem>? candidates = null)
    {
        if (isLoadingImagePreviews)
        {
            imagePreviewReloadRequested = true;
            return;
        }

        try
        {
            isLoadingImagePreviews = true;
            var messages = (candidates ?? viewModel.Messages)
                .Where(static message => message.IsImageMessage && !message.HasImagePreview)
                .ToArray();

            foreach (var item in messages)
            {
                var attachment = item.PrimaryImageAttachment;
                if (attachment is null || item.HasImagePreview)
                {
                    continue;
                }

                if (!loadingImagePreviewAttachmentIds.Add(attachment.AttachmentId))
                {
                    imagePreviewReloadRequested = true;
                    continue;
                }

                try
                {
                    var file = AttachmentOpenService.TryGetCachedFile(attachment);
                    if (file is null && attachmentFiles.IsEnabled && attachment.RemoteUri is not null)
                    {
                        file = await AttachmentOpenService.DownloadToCacheAsync(attachment, attachmentFiles)
                            .ConfigureAwait(false);
                    }

                    if (file is not null)
                    {
                        await MainThread.InvokeOnMainThreadAsync(() => item.SetImagePreviewPath(file.Path));
                    }
                }
                catch (Exception ex)
                {
                    CrashDiagnostics.LogException("ChatPage.ImagePreview", ex);
                }
                finally
                {
                    loadingImagePreviewAttachmentIds.Remove(attachment.AttachmentId);
                }
            }
        }
        finally
        {
            isLoadingImagePreviews = false;
            if (imagePreviewReloadRequested)
            {
                imagePreviewReloadRequested = false;
                _ = EnsureImagePreviewsAsync();
            }
        }
    }

    private async Task OpenInlineImageAsync(ChatMessageItem item)
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
            ImageViewerTitle.Text = item.HasMultipleImages
                ? $"1 из {item.Attachments.Count}"
                : "Фото";
            ImageViewerImage.Source = ImageSource.FromFile(file.Path);
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
        ImageViewerImage.Source = null;
        imageViewerAttachment = null;
        imageViewerMessage = null;
    }

    private async void OnVoiceMessageTapped(object? sender, TappedEventArgs e)
    {
        CancelMessageLongPress();
        if ((sender as BindableObject)?.BindingContext is not ChatMessageItem item || item.Attachments.Count == 0)
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

    private void ShowAttachmentActionSheet(ChatMessageItem? message, IReadOnlyList<AttachmentMetadata> attachments)
    {
        selectedAttachment = attachments.Count == 0 ? null : attachments[0];
        selectedAttachmentMessage = message;
        if (selectedAttachment is null)
        {
            return;
        }

        AttachmentActionTitle.Text = selectedAttachment.FileName;
        AttachmentActionSubtitle.Text = DescribeAttachment(selectedAttachment, attachments.Count);
        AttachmentActionReplyRow.IsVisible = message is not null;
        AttachmentActionDeleteRow.IsVisible = message is not null;
        AttachmentActionSaveRow.IsVisible = true;
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

    private async void OnAudioCallClicked(object? sender, EventArgs e) =>
        await OpenCallAsync(isVideo: false);

    private async void OnVideoCallClicked(object? sender, EventArgs e) =>
        await OpenCallAsync(isVideo: true);

    private async Task OpenCallAsync(bool isVideo)
    {
        var conversation = viewModel.Conversation;
        if (conversation is null || conversation.Kind != ConversationKind.OneToOne)
        {
            return;
        }

        try
        {
            var remote = SessionId.Parse(conversation.Id.Value);
            var descriptor = await callCoordinator.CreateOutgoingAsync(
                remote,
                conversation.Id.Value,
                conversation.DisplayName,
                isVideo);
            await Shell.Current.GoToAsync(
                ShellRouteCatalog.Call,
                new ShellNavigationQueryParameters { ["call"] = descriptor });
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException)
        {
            CrashDiagnostics.LogException("ChatPage.OpenCall", exception);
            await DisplayAlertAsync("Звонок Deep", exception.Message, "Закрыть");
        }
    }

    private void OnMessageTapped(object? sender, TappedEventArgs e)
    {
        CancelMessageLongPress();
    }

    private void OnMessagePointerPressed(object? sender, PointerEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not ChatMessageItem item)
        {
            return;
        }

        pendingLongPressMessage = item;
        messagePointerStart = e.GetPosition(PageLayout) ?? new Point(0, 0);
        messageLongPressTimer ??= Dispatcher.CreateTimer();
        messageLongPressTimer.Interval = MessageLongPressDelay;
        messageLongPressTimer.Tick -= OnMessageLongPressTimerTick;
        messageLongPressTimer.Tick += OnMessageLongPressTimerTick;
        messageLongPressTimer.Start();
    }

    private void OnMessagePointerMoved(object? sender, PointerEventArgs e)
    {
        if (pendingLongPressMessage is null)
        {
            return;
        }

        var point = e.GetPosition(PageLayout);
        if (point is null)
        {
            return;
        }

        var dx = point.Value.X - messagePointerStart.X;
        var dy = point.Value.Y - messagePointerStart.Y;
        if (Math.Sqrt(dx * dx + dy * dy) > MessageLongPressMoveThreshold)
        {
            CancelMessageLongPress();
        }
    }

    private void OnMessagePointerReleased(object? sender, PointerEventArgs e) =>
        CancelMessageLongPress();

    private void OnMessageLongPressTimerTick(object? sender, EventArgs e)
    {
        messageLongPressTimer?.Stop();
        if (pendingLongPressMessage is not { } message)
        {
            return;
        }

        pendingLongPressMessage = null;
        suppressNextAttachmentTap = message.HasAttachments;
        ShowMessageMenu(message);
    }

    private void CancelMessageLongPress()
    {
        messageLongPressTimer?.Stop();
        pendingLongPressMessage = null;
    }

    private void ShowMessageMenu(ChatMessageItem item)
    {
        selectedMessage = item;
        var attachment = PrimaryActionAttachment(item);
        var hasAttachment = attachment is not null;
        var hasText = item.HasVisibleBody;
        MessageMenuOpenMediaRow.IsVisible = item.IsImageMessage;
        MessageMenuSaveAttachmentRow.IsVisible = hasAttachment;
        MessageMenuShareAttachmentRow.IsVisible = hasAttachment;
        MessageMenuCopyAttachmentNameRow.IsVisible = hasAttachment;
        MessageMenuCopyTextRow.IsVisible = hasText;
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

    private async Task DeleteMessageWithConfirmationAsync(ChatMessageItem message)
    {
        if (!await DisplayAlertAsync("Удалить сообщение?", "Сообщение будет удалено с этого устройства.", "Удалить", "Отмена"))
        {
            return;
        }

        await viewModel.DeleteMessageAsync(message);
    }

    private static AttachmentMetadata? PrimaryActionAttachment(ChatMessageItem message) =>
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

    private async Task FinishVoiceRecordingGestureAsync()
    {
        if (!voicePointerActive && voiceGestureStartTask is null)
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
            voiceCancelBySwipe = false;
            return;
        }

        if (voiceCancelBySwipe)
        {
            await viewModel.CancelVoiceRecordingAsync();
        }
        else
        {
            await viewModel.StopVoiceRecordingAndSendAsync();
        }

        voiceCancelBySwipe = false;
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

    private ChatMessageItem? FindVoiceMessageItem(string attachmentId) =>
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

        if (e.Action is MotionEventActions.Move or MotionEventActions.Up or MotionEventActions.Cancel)
        {
            UpdateVoiceRecordingGesture(new Point(e.RawX, e.RawY));
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

        e.Handled = false;
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
                UpdateVoiceRecordingGesture(new Point(motion.RawX, motion.RawY));
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

    private static bool MessageMatchesSearch(ChatMessageItem message, string query) =>
        ContainsSearchText(message.Body, query)
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
