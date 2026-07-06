using System.Collections.Specialized;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Services;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui.Pages;

public partial class ChatPage : ContentPage, IQueryAttributable
{
    private IDispatcherTimer? autoReceiveTimer;
    private readonly ChatViewModel viewModel;
    private readonly INetworkStatusService networkStatusService;
    private readonly IAttachmentFileTransport attachmentFiles;
    private readonly CallSessionCoordinator callCoordinator;
    private readonly SyncPollingPolicy syncPollingPolicy;
    private CancellationTokenSource? pendingScrollToEnd;
    private CancellationTokenSource? routeLoadCancellation;
    private bool pendingScrollAnimate;
    private bool pendingScrollForce;
    private bool isLoadingOlderMessages;
    private bool shouldStickToEnd = true;
    private bool didInitialScroll;
    private double expandedPageHeight;
    private ChatMessageItem? selectedMessage;

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

        networkStatusService.StatusChanged += OnNetworkStatusChanged;
        viewModel.Messages.CollectionChanged += OnMessagesCollectionChanged;
        SizeChanged += OnPageSizeChanged;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Dispatcher.Dispatch(ApplyAndroidSafeAreaCompensation);
        BackgroundSyncBridge.SyncScheduled -= OnBackgroundSyncScheduled;
        BackgroundSyncBridge.SyncScheduled += OnBackgroundSyncScheduled;
        ApplyComposerPreferences();
        UpdateNetworkUi();
        _ = ConfigureAutoReceiveAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        BackgroundSyncBridge.SyncScheduled -= OnBackgroundSyncScheduled;
        autoReceiveTimer?.Stop();
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

            await viewModel.OpenFromRouteAsync(sessionId, displayName, cancellationToken);
            await RevealInitialMessagesAsync(cancellationToken);
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

    private void OnInfoClicked(object? sender, EventArgs e)
    {
        ChatMenuOverlay.IsVisible = true;
    }

    private void OnCloseChatMenu(object? sender, TappedEventArgs e) => ChatMenuOverlay.IsVisible = false;

    private async void OnCopyConversationIdClicked(object? sender, TappedEventArgs e)
    {
        ChatMenuOverlay.IsVisible = false;
        if (viewModel.Conversation is { } conversation)
        {
            await Clipboard.Default.SetTextAsync(conversation.Id.Value);
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
            autoReceiveTimer.Interval = TimeSpan.FromSeconds(3);
            autoReceiveTimer.Tick += OnAutoReceiveTick;
        }

        autoReceiveTimer.Start();
    }

    private async void OnAutoReceiveTick(object? sender, EventArgs e)
    {
        if (viewModel.IsBusy || viewModel.Conversation is null)
        {
            return;
        }

        await viewModel.ReceiveAsync();
    }

    private void OnBackgroundSyncScheduled()
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (!viewModel.IsBusy && viewModel.Conversation is not null)
            {
                await viewModel.ReceiveAsync();
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
        if ((sender as BindableObject)?.BindingContext is not ChatMessageItem item)
        {
            return;
        }

        await AttachmentOpenService.OpenAsync(this, item.Attachments, attachmentFiles);
    }

    private void ApplyAndroidSafeAreaCompensation()
    {
        expandedPageHeight = Math.Max(expandedPageHeight, Height);
        var keyboardVisible = expandedPageHeight - Height > 100;
        var statusBarHeight = keyboardVisible ? 0 : AndroidSafeArea.GetStatusBarHeight();
        PageLayout.Margin = new Thickness(0, 0, 0, statusBarHeight);
    }

    private void OnPageSizeChanged(object? sender, EventArgs e) =>
        ApplyAndroidSafeAreaCompensation();

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
        if ((sender as BindableObject)?.BindingContext is not ChatMessageItem item)
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
