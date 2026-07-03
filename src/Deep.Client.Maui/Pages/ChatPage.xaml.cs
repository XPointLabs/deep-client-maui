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
    private CancellationTokenSource? pendingScrollToEnd;
    private CancellationTokenSource? routeLoadCancellation;
    private bool pendingScrollAnimate;
    private bool pendingScrollForce;
    private bool isLoadingOlderMessages;
    private bool shouldStickToEnd = true;
    private bool didInitialScroll;
    private double expandedPageHeight;

    public ChatPage(
        ChatViewModel viewModel,
        INetworkStatusService networkStatusService,
        IAttachmentFileTransport attachmentFiles,
        CallSessionCoordinator callCoordinator)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        this.networkStatusService = networkStatusService;
        this.attachmentFiles = attachmentFiles;
        this.callCoordinator = callCoordinator;
        BindingContext = viewModel;

        networkStatusService.StatusChanged += OnNetworkStatusChanged;
        viewModel.Messages.CollectionChanged += OnMessagesCollectionChanged;
        SizeChanged += OnPageSizeChanged;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Dispatcher.Dispatch(ApplyAndroidSafeAreaCompensation);
        ApplyComposerPreferences();
        UpdateNetworkUi();
        EnsureAutoReceive();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
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
            QueueScrollToEnd(animate: false, force: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            CrashDiagnostics.LogException("ChatPage.LoadFromRouteAsync", ex);
        }
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        await NavigateBackToConversationsAsync();
    }

    private static Task NavigateBackToConversationsAsync() =>
        Shell.Current.GoToAsync($"//{ShellRouteCatalog.Conversations}", animate: false);

    private async void OnInfoClicked(object? sender, EventArgs e)
    {
        var conversation = viewModel.Conversation;
        await DisplayActionSheetAsync(
            conversation?.DisplayName ?? "Диалог",
            "Отмена",
            null,
            conversation?.Id.Value ?? "ID аккаунта недоступен");
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
                QueueScrollToEnd(e.Action == NotifyCollectionChangedAction.Add, force);
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

    private async void OnMessageTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not ChatMessageItem item)
        {
            return;
        }

        var selected = await DisplayActionSheetAsync(
            "Сообщение",
            "Отмена",
            null,
            "Ответить",
            "👍",
            "❤️",
            "😂",
            "😮",
            "😢",
            "Скопировать текст");
        switch (selected)
        {
            case "Ответить":
                viewModel.BeginReply(item);
                DraftEntry.Focus();
                break;
            case "Скопировать текст":
                await Clipboard.Default.SetTextAsync(item.Body);
                break;
            case "👍":
            case "❤️":
            case "😂":
            case "😮":
            case "😢":
                await viewModel.ToggleReactionAsync(item, selected);
                break;
        }
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
