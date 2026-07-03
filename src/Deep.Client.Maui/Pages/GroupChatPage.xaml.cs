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
    private CancellationTokenSource? pendingScrollToEnd;
    private CancellationTokenSource? routeLoadCancellation;
    private bool pendingScrollAnimate;
    private bool pendingScrollForce;
    private bool isLoadingOlderMessages;
    private bool shouldStickToEnd = true;
    private bool didInitialScroll;
    private double expandedPageHeight;

    public GroupChatPage(
        GroupChatViewModel viewModel,
        INetworkStatusService networkStatusService,
        IAttachmentFileTransport attachmentFiles)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        this.networkStatusService = networkStatusService;
        this.attachmentFiles = attachmentFiles;
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
        EnsureAutoRefresh();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        autoRefreshTimer?.Stop();
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

    private async void OnAttachmentTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not GroupChatMessageItem item)
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

    private async void OnMessageTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not GroupChatMessageItem item)
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
