using System.Collections.Specialized;
using System.ComponentModel;
using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui.Pages;

public partial class DesktopWorkspacePage : ContentPage, IConversationActivationTarget, IActiveComposerProvider
{
    private const double ContextMenuEstimatedHeight = 390;
    private const double ContextMenuWidth = 248;
    private const double VoiceCancelSwipeThreshold = 84;
    private const double VoiceCancelPanelTranslation = 36;
    private readonly DesktopWorkspaceViewModel viewModel;
    private readonly ClientRuntime runtime;
    private readonly IAttachmentFileTransport attachmentFiles;
    private readonly CallSessionCoordinator callCoordinator;
    private readonly IActiveConversationTracker activeConversationTracker;
    private readonly PushRegistrationLifecycleCoordinator pushRegistrationLifecycle;
    private readonly SyncPollingPolicy syncPollingPolicy;
    private readonly BackgroundSyncSchedulingCoordinator backgroundSyncScheduling;
#if DEBUG && DEEP_PHYSICAL_E2E
    private readonly PhysicalMailboxRouteUsageTracker physicalRouteUsageTracker;
    private Label? physicalRouteNodeMarker;
#endif
    private IncomingCallPollingBackoff incomingCallPolling = new();
    private readonly VoiceMessagePlaybackService voicePlayback = new();
    private readonly SemaphoreSlim voiceGestureCompletionGate = new(1, 1);
    private readonly List<object> messageSearchMatches = [];
    private readonly object syncPumpGate = new();
    private CancellationTokenSource? pageActivityCancellation;
    private IDispatcherTimer? syncTimer;
    private IDispatcherTimer? incomingCallTimer;
    private IDispatcherTimer? voiceRecordingTimer;
    private IDispatcherTimer? voicePlaybackTimer;
    private IDisposable? activeConversationLease;
    private ChatMessageItem? selectedDirectMessage;
    private GroupChatMessageItem? selectedGroupMessage;
    private ChatViewModel? subscribedDirectChat;
    private GroupChatViewModel? subscribedGroupChat;
    private double conversationListWidth = DesktopWorkspaceViewModel.DefaultConversationListWidth;
    private bool splitterPointerActive;
    private Task? syncPump;
    private int syncRequested;
    private bool checkingCalls;
    private bool isPageActive;
    private int messageSearchIndex = -1;
    private DateTimeOffset voiceRecordingStartedAt;
    private Point voicePointerStart;
    private Task? voiceGestureStartTask;
    private DesktopConversationDetailKind voiceRecordingKind;
    private ChatViewModel? voiceDirectTarget;
    private GroupChatViewModel? voiceGroupTarget;
    private ConversationId? voiceRecordingConversationId;
    private string? activeVoiceAttachmentId;
    private bool voicePointerActive;
    private bool voiceCancelBySwipe;
    private bool conversationSelectionInProgress;
    private int voicePageExitCancellationRequested;

    public DesktopWorkspacePage(
        DesktopWorkspaceViewModel viewModel,
        ClientRuntime runtime,
        IAttachmentFileTransport attachmentFiles,
        CallSessionCoordinator callCoordinator,
        IActiveConversationTracker activeConversationTracker,
        PushRegistrationLifecycleCoordinator pushRegistrationLifecycle,
        SyncPollingPolicy syncPollingPolicy,
        BackgroundSyncSchedulingCoordinator backgroundSyncScheduling
#if DEBUG && DEEP_PHYSICAL_E2E
        , IMailboxDispatchRouteUsageObserver physicalRouteUsageObserver
#endif
        )
    {
        InitializeComponent();
        this.viewModel = viewModel;
        this.runtime = runtime;
        this.attachmentFiles = attachmentFiles;
        this.callCoordinator = callCoordinator;
        this.activeConversationTracker = activeConversationTracker;
        this.pushRegistrationLifecycle = pushRegistrationLifecycle;
        this.syncPollingPolicy = syncPollingPolicy;
        this.backgroundSyncScheduling = backgroundSyncScheduling;
#if DEBUG && DEEP_PHYSICAL_E2E
        physicalRouteUsageTracker = physicalRouteUsageObserver as
            PhysicalMailboxRouteUsageTracker ?? throw new InvalidOperationException(
                "Physical E2E requires its mailbox route usage tracker.");
        CreatePhysicalRouteNodeMarker();
#endif
        BindingContext = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        isPageActive = true;
        ResetMessageSearchUi();
        Interlocked.Exchange(ref voicePageExitCancellationRequested, 0);
        pageActivityCancellation?.Cancel();
        pageActivityCancellation?.Dispose();
        pageActivityCancellation = new CancellationTokenSource();
        incomingCallPolling = new IncomingCallPollingBackoff();
        var cancellationToken = pageActivityCancellation.Token;

        SubscribeEvents();
        viewModel.UpdateWindowWidth(Width);
        ApplyLayoutColumns();
        RefreshActiveConversationLease();

        try
        {
            var active = await runtime.Accounts.GetActiveAccountAsync(cancellationToken);
            if (active is not null)
            {
                AttachmentOpenService.SetAccountScope(active.SessionId.Value);
            }

            await viewModel.InitializeAsync(cancellationToken);
#if DEBUG && DEEP_PHYSICAL_E2E
            UpdatePhysicalRouteNodeMarker();
#endif
            await QueueActiveImagePreviewsAsync(cancellationToken);
            RequestSynchronization();
            _ = InitializeRealtimeServicesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            CrashDiagnostics.LogException("DesktopWorkspacePage.OnAppearing", ex);
        }
    }

    protected override void OnDisappearing()
    {
        isPageActive = false;
        activeConversationLease?.Dispose();
        activeConversationLease = null;
        UnsubscribeEvents();
        syncTimer?.Stop();
        incomingCallTimer?.Stop();
        StopRecordingUiTimer();
        StopVoicePlaybackTimer();
        voicePlayback.Stop();
        ClearActiveVoicePlayback();
        Interlocked.Exchange(ref voicePageExitCancellationRequested, 1);
        _ = FinishVoiceRecordingGestureAsync(forceCancel: true);
        pageActivityCancellation?.Cancel();
        pageActivityCancellation?.Dispose();
        pageActivityCancellation = null;
        ResetMessageSearchUi();
        HideTransientMenus();
        base.OnDisappearing();
    }

    public async Task<bool> ActivateConversationAsync(
        ConversationId conversationId,
        CancellationToken cancellationToken = default)
    {
        var previousConversationId = viewModel.SelectedConversation?.Id;
        await FinishVoiceRecordingGestureAsync(forceCancel: true);
        var activated = await viewModel.ActivateConversationAsync(conversationId, cancellationToken);
        if (!activated)
        {
            return false;
        }

        ApplyLayoutColumns();
        RefreshActiveConversationLease();
        if (previousConversationId != conversationId)
        {
            ResetMessageSearchUi();
        }
        HideTransientMenus();
        await QueueActiveImagePreviewsAsync(cancellationToken);
        ScrollActiveMessagesToEnd();
        return true;
    }

    public bool TryGetActiveComposer(out ChatViewModel? chat, out GroupChatViewModel? group)
    {
        chat = viewModel.IsDetailPaneVisible
            && viewModel.IsDirectDetail
            && viewModel.DirectChat.Conversation is not null
            ? viewModel.DirectChat
            : null;
        group = viewModel.IsDetailPaneVisible
            && viewModel.IsGroupDetail
            && viewModel.GroupChat.GroupTitle.Length > 0
            ? viewModel.GroupChat
            : null;
        return chat is not null || group is not null;
    }

    private void SubscribeEvents()
    {
        UnsubscribeEvents();
        viewModel.PropertyChanged += OnWorkspacePropertyChanged;
        RebindMessageCollectionSubscriptions();
        ContactProfileUpdateBus.ContactChanged += OnContactProfileChanged;
        BackgroundSyncBridge.SyncScheduled += OnBackgroundSyncScheduled;
        BackgroundSyncBridge.SyncCompleted += OnBackgroundSyncScheduled;
#if DEBUG && DEEP_PHYSICAL_E2E
        physicalRouteUsageTracker.Changed += OnPhysicalRouteUsageChanged;
#endif
    }

    private void UnsubscribeEvents()
    {
        viewModel.PropertyChanged -= OnWorkspacePropertyChanged;
        if (subscribedDirectChat is not null)
        {
            subscribedDirectChat.Messages.CollectionChanged -= OnDirectMessagesChanged;
            subscribedDirectChat = null;
        }

        if (subscribedGroupChat is not null)
        {
            subscribedGroupChat.Messages.CollectionChanged -= OnGroupMessagesChanged;
            subscribedGroupChat = null;
        }

        ContactProfileUpdateBus.ContactChanged -= OnContactProfileChanged;
        BackgroundSyncBridge.SyncScheduled -= OnBackgroundSyncScheduled;
        BackgroundSyncBridge.SyncCompleted -= OnBackgroundSyncScheduled;
#if DEBUG && DEEP_PHYSICAL_E2E
        physicalRouteUsageTracker.Changed -= OnPhysicalRouteUsageChanged;
#endif
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DesktopWorkspaceViewModel.DirectChat)
            or nameof(DesktopWorkspaceViewModel.GroupChat))
        {
            RebindMessageCollectionSubscriptions();
        }

        if (e.PropertyName is nameof(DesktopWorkspaceViewModel.SelectedConversation)
            or nameof(DesktopWorkspaceViewModel.DetailKind)
            or nameof(DesktopWorkspaceViewModel.IsDetailPaneVisible))
        {
            RefreshActiveConversationLease();
#if DEBUG && DEEP_PHYSICAL_E2E
            UpdatePhysicalRouteNodeMarker();
#endif
        }

        if (e.PropertyName == nameof(DesktopWorkspaceViewModel.ConversationList))
        {
            ResetMessageSearchUi();
        }
    }

    private void RebindMessageCollectionSubscriptions()
    {
        if (!ReferenceEquals(subscribedDirectChat, viewModel.DirectChat))
        {
            if (subscribedDirectChat is not null)
            {
                subscribedDirectChat.Messages.CollectionChanged -= OnDirectMessagesChanged;
            }

            subscribedDirectChat = viewModel.DirectChat;
            subscribedDirectChat.Messages.CollectionChanged += OnDirectMessagesChanged;
        }

        if (!ReferenceEquals(subscribedGroupChat, viewModel.GroupChat))
        {
            if (subscribedGroupChat is not null)
            {
                subscribedGroupChat.Messages.CollectionChanged -= OnGroupMessagesChanged;
            }

            subscribedGroupChat = viewModel.GroupChat;
            subscribedGroupChat.Messages.CollectionChanged += OnGroupMessagesChanged;
        }
    }

#if DEBUG && DEEP_PHYSICAL_E2E
    private void CreatePhysicalRouteNodeMarker()
    {
        physicalRouteNodeMarker = new Label
        {
            AutomationId = "PhysicalE2E.RouteNodeMarker",
            IsVisible = false,
            FontSize = 9,
            TextColor = Colors.Gray,
            BackgroundColor = Colors.Transparent,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Start,
            InputTransparent = true,
            ZIndex = 100
        };
        DetailContent.Children.Add(physicalRouteNodeMarker);
    }

    private void UpdatePhysicalRouteNodeMarker()
    {
        if (physicalRouteNodeMarker is null) return;
        var routerId = viewModel.IsDirectDetail &&
            viewModel.SelectedConversation is { } selected
                ? physicalRouteUsageTracker.GetCurrentRouterId(selected.Id)
                : null;
        physicalRouteNodeMarker.Text = routerId ?? string.Empty;
        physicalRouteNodeMarker.IsVisible = viewModel.IsDirectDetail &&
            !string.IsNullOrWhiteSpace(routerId);
    }

    private void OnPhysicalRouteUsageChanged(
        object? sender,
        ConversationId conversationId)
    {
        if (!isPageActive)
            return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!isPageActive ||
                !viewModel.IsDirectDetail ||
                viewModel.SelectedConversation?.Id != conversationId)
            {
                return;
            }
            UpdatePhysicalRouteNodeMarker();
        });
    }
#endif

    private void OnWorkspaceSizeChanged(object? sender, EventArgs e)
    {
        viewModel.UpdateWindowWidth(WorkspaceRoot.Width);
        ApplyLayoutColumns();
        RefreshActiveConversationLease();
    }

    private void OnContactProfileChanged(object? sender, SessionId contactId)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (pageActivityCancellation is not { } activity || activity.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await viewModel.RefreshLocalConversationListAsync(activity.Token);
                if (viewModel.IsDirectDetail && viewModel.DirectChat.Counterpart == contactId)
                {
                    await viewModel.DirectChat.RefreshConversationMetadataAsync(activity.Token);
                }

                if (viewModel.IsGroupDetail)
                {
                    await viewModel.GroupChat.RefreshContactDisplayNamesAsync(activity.Token);
                }
            }
            catch (OperationCanceledException) when (activity.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                CrashDiagnostics.LogException("DesktopWorkspacePage.ContactRefresh", ex);
            }
        });
    }

    private void ApplyLayoutColumns()
    {
        if (viewModel.IsSplitView)
        {
            var maximumForWindow = MaximumConversationListWidthForWindow();
            conversationListWidth = Math.Clamp(
                conversationListWidth,
                DesktopWorkspaceViewModel.MinimumConversationListWidth,
                maximumForWindow);
            WorkspaceRoot.ColumnDefinitions[0].Width = new GridLength(
                conversationListWidth);
            WorkspaceRoot.ColumnDefinitions[1].Width = new GridLength(9);
            WorkspaceRoot.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(DetailPane, 2);
            return;
        }

        WorkspaceRoot.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        WorkspaceRoot.ColumnDefinitions[1].Width = new GridLength(0);
        WorkspaceRoot.ColumnDefinitions[2].Width = new GridLength(0);
        Grid.SetColumn(DetailPane, 0);
    }

    private async void OnConversationSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (conversationSelectionInProgress
            || e.CurrentSelection.FirstOrDefault() is not ConversationListItem item)
        {
            return;
        }

        conversationSelectionInProgress = true;
        var previousConversationId = viewModel.SelectedConversation?.Id;
        HideTransientMenus();
        try
        {
            await FinishVoiceRecordingGestureAsync(forceCancel: true);
            if (await viewModel.SelectConversationAsync(item, pageActivityCancellation?.Token ?? CancellationToken.None))
            {
                if (previousConversationId != item.Id)
                {
                    ResetMessageSearchUi();
                }
                ApplyLayoutColumns();
                RefreshActiveConversationLease();
                await QueueActiveImagePreviewsAsync(pageActivityCancellation?.Token ?? CancellationToken.None);
                ScrollActiveMessagesToEnd();
            }
            else
            {
                viewModel.ConversationList.SelectedConversation = viewModel.SelectedConversation;
            }
        }
        catch (Exception ex)
        {
            CrashDiagnostics.LogException("DesktopWorkspacePage.OpenConversation", ex);
            viewModel.ConversationList.SelectedConversation = viewModel.SelectedConversation;
        }
        finally
        {
            conversationSelectionInProgress = false;
        }
    }

    private async void OnBackToListClicked(object? sender, EventArgs e)
    {
        await FinishVoiceRecordingGestureAsync(forceCancel: true);
        HideTransientMenus();
        viewModel.ShowConversationList();
        ApplyLayoutColumns();
        RefreshActiveConversationLease();
    }

    private async void OnNewConversationClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync(ShellRouteCatalog.StartConversation);
    }

    private void OnSplitterPointerPressed(object? sender, PointerEventArgs e) =>
        splitterPointerActive = viewModel.IsSplitView;

    private void OnSplitterPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!splitterPointerActive || e.GetPosition(WorkspaceRoot) is not { } point)
        {
            return;
        }

        conversationListWidth = Math.Clamp(
            point.X,
            DesktopWorkspaceViewModel.MinimumConversationListWidth,
            MaximumConversationListWidthForWindow());
        ApplyLayoutColumns();
    }

    private void OnSplitterPointerReleased(object? sender, PointerEventArgs e) =>
        splitterPointerActive = false;

    private double MaximumConversationListWidthForWindow() =>
        Math.Min(
            DesktopWorkspaceViewModel.MaximumConversationListWidth,
            Math.Max(
                DesktopWorkspaceViewModel.MinimumConversationListWidth,
                WorkspaceRoot.Width - 360));

    private void OnMessageSearchClicked(object? sender, EventArgs e)
    {
        HeaderOverflowMenu.IsVisible = false;
        DesktopMessageContextMenu.IsVisible = false;
        MessageSearchBar.IsVisible = !MessageSearchBar.IsVisible;
        if (MessageSearchBar.IsVisible)
        {
            MessageSearchEntry.Focus();
        }
        else
        {
            MessageSearchEntry.Text = string.Empty;
            ResetMessageSearch();
        }
    }

    private void OnMessageSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        ResetMessageSearch();
        var query = e.NewTextValue?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        if (viewModel.IsDirectDetail)
        {
            messageSearchMatches.AddRange(viewModel.DirectChat.Messages.Where(message => MessageMatches(message, query)));
        }
        else if (viewModel.IsGroupDetail)
        {
            messageSearchMatches.AddRange(viewModel.GroupChat.Messages.Where(message => MessageMatches(message, query)));
        }

        if (messageSearchMatches.Count > 0)
        {
            messageSearchIndex = messageSearchMatches.Count - 1;
            ScrollToCurrentSearchResult();
        }
    }

    private void OnPreviousSearchResultClicked(object? sender, EventArgs e) => MoveSearchResult(-1);

    private void OnNextSearchResultClicked(object? sender, EventArgs e) => MoveSearchResult(1);

    private void MoveSearchResult(int delta)
    {
        if (messageSearchMatches.Count == 0)
        {
            return;
        }

        messageSearchIndex = (messageSearchIndex + delta + messageSearchMatches.Count) % messageSearchMatches.Count;
        ScrollToCurrentSearchResult();
    }

    private void ScrollToCurrentSearchResult()
    {
        if (messageSearchIndex < 0 || messageSearchIndex >= messageSearchMatches.Count)
        {
            return;
        }

        var match = messageSearchMatches[messageSearchIndex];
        if (match is ChatMessageItem direct)
        {
            DirectMessagesCollection.ScrollTo(direct, position: ScrollToPosition.Center, animate: true);
        }
        else if (match is GroupChatMessageItem group)
        {
            GroupMessagesCollection.ScrollTo(group, position: ScrollToPosition.Center, animate: true);
        }
    }

    private void ResetMessageSearch()
    {
        messageSearchMatches.Clear();
        messageSearchIndex = -1;
    }

    private void ResetMessageSearchUi()
    {
        MessageSearchBar.IsVisible = false;
        MessageSearchEntry.Text = string.Empty;
        ResetMessageSearch();
    }

    private async void OnAudioCallClicked(object? sender, EventArgs e) => await OpenCallAsync(isVideo: false);

    private async void OnVideoCallClicked(object? sender, EventArgs e) => await OpenCallAsync(isVideo: true);

    private async void OnOpenSettingsClicked(object? sender, TappedEventArgs e)
    {
        try
        {
            await Shell.Current.GoToAsync(ShellRouteCatalog.Settings);
        }
        catch (Exception ex)
        {
            CrashDiagnostics.LogException("DesktopWorkspacePage.OpenSettings", ex);
        }
    }

    private async Task OpenCallAsync(bool isVideo)
    {
        var conversation = viewModel.DirectChat.Conversation;
        if (!viewModel.IsDirectDetail || conversation is null)
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
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            CrashDiagnostics.LogException("DesktopWorkspacePage.OpenCall", ex);
            await DisplayAlertAsync("Звонок Deep", ex.Message, "Закрыть");
        }
    }

    private void OnOverflowClicked(object? sender, EventArgs e)
    {
        DesktopMessageContextMenu.IsVisible = false;
        GroupMembersPanel.IsVisible = false;
        HeaderOverflowMenu.IsVisible = !HeaderOverflowMenu.IsVisible;
    }

    private async void OnDetailHeaderTapped(object? sender, TappedEventArgs e)
    {
        HeaderOverflowMenu.IsVisible = false;
        DesktopMessageContextMenu.IsVisible = false;

        if (viewModel.IsGroupDetail)
        {
            GroupMembersPanel.IsVisible = true;
            return;
        }

        if (viewModel.IsDirectDetail && viewModel.DirectChat.Conversation is { } conversation)
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

    private void OnGroupMembersClicked(object? sender, EventArgs e)
    {
        HeaderOverflowMenu.IsVisible = false;
        DesktopMessageContextMenu.IsVisible = false;
        GroupMembersPanel.IsVisible = viewModel.IsGroupDetail;
    }

    private void OnCloseGroupMembersClicked(object? sender, EventArgs e) =>
        GroupMembersPanel.IsVisible = false;

    private async void OnRefreshChatClicked(object? sender, EventArgs e)
    {
        HeaderOverflowMenu.IsVisible = false;
        if (viewModel.IsDirectDetail)
        {
            await viewModel.DirectChat.ReceiveAsync();
        }
        else if (viewModel.IsGroupDetail)
        {
            await viewModel.GroupChat.RefreshAsync();
        }

        await QueueActiveImagePreviewsAsync(pageActivityCancellation?.Token ?? CancellationToken.None);
    }

    private void OnCloseOverflowClicked(object? sender, EventArgs e) => HeaderOverflowMenu.IsVisible = false;

    private void OnDirectMessageContextRequested(object? sender, EventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not ChatMessageItem message)
        {
            return;
        }

        selectedDirectMessage = message;
        selectedGroupMessage = null;
        ShowMessageContextMenu(sender as VisualElement, message.HasVisibleBody);
    }

    private void OnGroupMessageContextRequested(object? sender, EventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not GroupChatMessageItem message)
        {
            return;
        }

        selectedDirectMessage = null;
        selectedGroupMessage = message;
        ShowMessageContextMenu(sender as VisualElement, message.HasVisibleBody);
    }

    private void ShowMessageContextMenu(VisualElement? source, bool hasText)
    {
        HeaderOverflowMenu.IsVisible = false;
        var hasAttachment = SelectedPrimaryAttachment() is not null;
        CopyMessageButton.IsVisible = hasText;
        OpenAttachmentButton.IsVisible = hasAttachment;
        SaveAttachmentButton.IsVisible = hasAttachment;
        ShareAttachmentButton.IsVisible = hasAttachment;
        CopyAttachmentNameButton.IsVisible = hasAttachment;
        var translation = ResolveContextMenuTranslation(source);
        DesktopMessageContextMenu.TranslationX = translation.X;
        DesktopMessageContextMenu.TranslationY = translation.Y;
        DesktopMessageContextMenu.IsVisible = true;
    }

    private Point ResolveContextMenuTranslation(VisualElement? source)
    {
#if WINDOWS
        if (source?.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement sourceElement
            && DetailContent.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement detailElement)
        {
            var position = sourceElement
                .TransformToVisual(detailElement)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            return new Point(
                Math.Clamp(
                    position.X + sourceElement.ActualWidth - ContextMenuWidth,
                    8,
                    Math.Max(8, detailElement.ActualWidth - ContextMenuWidth - 8)),
                Math.Clamp(
                    position.Y,
                    0,
                    Math.Max(0, detailElement.ActualHeight - ContextMenuEstimatedHeight)));
        }
#endif
        return new Point(8, 0);
    }

    private async void OnContextReactionClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string emoji })
        {
            return;
        }

        DesktopMessageContextMenu.IsVisible = false;
        if (selectedDirectMessage is { } direct)
        {
            await viewModel.DirectChat.ToggleReactionAsync(direct.Id, emoji);
        }
        else if (selectedGroupMessage is { } group)
        {
            await viewModel.GroupChat.ToggleReactionAsync(group.Id, emoji);
        }

        ClearSelectedMessage();
    }

    private void OnContextReplyClicked(object? sender, EventArgs e)
    {
        DesktopMessageContextMenu.IsVisible = false;
        if (selectedDirectMessage is { } direct)
        {
            viewModel.DirectChat.BeginReply(direct);
            DirectDraftEditor.Focus();
        }
        else if (selectedGroupMessage is { } group)
        {
            viewModel.GroupChat.BeginReply(group);
            GroupDraftEditor.Focus();
        }

        ClearSelectedMessage();
    }

    private async void OnContextCopyClicked(object? sender, EventArgs e)
    {
        var text = selectedDirectMessage?.Body ?? selectedGroupMessage?.Body;
        if (!string.IsNullOrWhiteSpace(text))
        {
            await Clipboard.Default.SetTextAsync(text);
        }

        DesktopMessageContextMenu.IsVisible = false;
        ClearSelectedMessage();
    }

    private async void OnContextOpenAttachmentClicked(object? sender, EventArgs e)
    {
        var attachments = SelectedAttachments();
        HideTransientMenus();
        if (attachments.Count > 0)
        {
            await AttachmentOpenService.OpenAsync(this, attachments, attachmentFiles);
        }
    }

    private async void OnContextSaveAttachmentClicked(object? sender, EventArgs e)
    {
        var attachment = SelectedPrimaryAttachment();
        HideTransientMenus();
        if (attachment is null)
        {
            return;
        }

        try
        {
            await AttachmentOpenService.SaveAsync(attachment, attachmentFiles);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Вложение", ex.Message, "Закрыть");
        }
    }

    private async void OnContextShareAttachmentClicked(object? sender, EventArgs e)
    {
        var attachment = SelectedPrimaryAttachment();
        HideTransientMenus();
        if (attachment is null)
        {
            return;
        }

        try
        {
            await AttachmentOpenService.ShareAsync(attachment, attachmentFiles);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Вложение", ex.Message, "Закрыть");
        }
    }

    private async void OnContextCopyAttachmentNameClicked(object? sender, EventArgs e)
    {
        var attachment = SelectedPrimaryAttachment();
        HideTransientMenus();
        if (attachment is not null)
        {
            await Clipboard.Default.SetTextAsync(attachment.FileName);
        }
    }

    private IReadOnlyList<AttachmentMetadata> SelectedAttachments() =>
        selectedDirectMessage?.Attachments
        ?? selectedGroupMessage?.Attachments
        ?? [];

    private AttachmentMetadata? SelectedPrimaryAttachment()
    {
        var attachments = SelectedAttachments();
        return attachments.Count == 0 ? null : attachments[0];
    }

    private async void OnContextDeleteClicked(object? sender, EventArgs e)
    {
        DesktopMessageContextMenu.IsVisible = false;
        if (!await DisplayAlertAsync(
                "Удалить сообщение?",
                "Сообщение будет удалено с этого устройства.",
                "Удалить",
                "Отмена"))
        {
            ClearSelectedMessage();
            return;
        }

        if (selectedDirectMessage is { } direct)
        {
            await viewModel.DirectChat.DeleteMessageAsync(direct);
        }
        else if (selectedGroupMessage is { } group)
        {
            await viewModel.GroupChat.DeleteMessageAsync(group);
        }

        ClearSelectedMessage();
    }

    private async void OnDirectReactionChipTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is MessageReactionChip chip)
        {
            await viewModel.DirectChat.ToggleReactionAsync(chip.MessageId, chip.Emoji);
        }
    }

    private async void OnGroupReactionChipTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is MessageReactionChip chip)
        {
            await viewModel.GroupChat.ToggleReactionAsync(chip.MessageId, chip.Emoji);
        }
    }

    private async void OnDirectAttachmentTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is ChatMessageItem { Attachments.Count: > 0 } message)
        {
            await AttachmentOpenService.OpenAsync(this, message.Attachments, attachmentFiles);
        }
    }

    private async void OnGroupAttachmentTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is GroupChatMessageItem { Attachments.Count: > 0 } message)
        {
            await AttachmentOpenService.OpenAsync(this, message.Attachments, attachmentFiles);
        }
    }

    private async void OnDirectVoiceMessageTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is ChatMessageItem { Attachments.Count: > 0 } message)
        {
            await ToggleVoicePlaybackAsync(message.Attachments[0], message);
        }
    }

    private async void OnGroupVoiceMessageTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is GroupChatMessageItem { Attachments.Count: > 0 } message)
        {
            await ToggleVoicePlaybackAsync(message.Attachments[0], message);
        }
    }

    private async Task ToggleVoicePlaybackAsync(AttachmentMetadata attachment, object message)
    {
        try
        {
            if (string.Equals(activeVoiceAttachmentId, attachment.AttachmentId, StringComparison.Ordinal)
                && voicePlayback.Snapshot.IsPlaying)
            {
                voicePlayback.Stop();
                ApplyVoicePlaybackSnapshot(VoicePlaybackSnapshot.Stopped);
                return;
            }

            ClearActiveVoicePlayback();
            activeVoiceAttachmentId = attachment.AttachmentId;
            SetVoicePlayback(message, isPlaying: true, progress: 0, TimeSpan.Zero);
            await voicePlayback.ToggleAsync(
                attachment,
                attachmentFiles,
                pageActivityCancellation?.Token ?? CancellationToken.None);
            ApplyVoicePlaybackSnapshot(voicePlayback.Snapshot);
            EnsureVoicePlaybackTimer();
        }
        catch (Exception ex)
        {
            ClearActiveVoicePlayback();
            await DisplayAlertAsync("Голосовое сообщение", ex.Message, "Закрыть");
        }
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

    private void StopVoicePlaybackTimer() => voicePlaybackTimer?.Stop();

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
        SetVoicePlayback(item, snapshot.IsPlaying, snapshot.Progress, snapshot.Position);
        if (!snapshot.IsPlaying)
        {
            ClearVoicePlayback(snapshot.AttachmentId);
            activeVoiceAttachmentId = null;
            StopVoicePlaybackTimer();
        }
    }

    private object? FindVoiceMessageItem(string attachmentId) =>
        (object?)viewModel.DirectChat.Messages.FirstOrDefault(message =>
            string.Equals(message.VoiceAttachmentId, attachmentId, StringComparison.Ordinal))
        ?? (object?)viewModel.GroupChat.Messages.FirstOrDefault(message =>
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

        switch (FindVoiceMessageItem(attachmentId))
        {
            case ChatMessageItem direct:
                direct.ClearVoicePlayback();
                break;
            case GroupChatMessageItem group:
                group.ClearVoicePlayback();
                break;
        }
    }

    private static void SetVoicePlayback(object? message, bool isPlaying, double progress, TimeSpan position)
    {
        switch (message)
        {
            case ChatMessageItem direct:
                direct.SetVoicePlayback(isPlaying, progress, position);
                break;
            case GroupChatMessageItem group:
                group.SetVoicePlayback(isPlaying, progress, position);
                break;
        }
    }

    private void OnVoicePointerPressed(object? sender, PointerEventArgs e)
    {
        var kind = viewModel.DetailKind;
        if (kind == DesktopConversationDetailKind.None || voicePointerActive)
        {
            return;
        }

        voiceRecordingKind = kind;
        voiceDirectTarget = kind == DesktopConversationDetailKind.Direct ? viewModel.DirectChat : null;
        voiceGroupTarget = kind == DesktopConversationDetailKind.Group ? viewModel.GroupChat : null;
        voiceRecordingConversationId = viewModel.SelectedConversation?.Id;
        voicePointerActive = true;
        voiceCancelBySwipe = false;
        voicePointerStart = e.GetPosition(WorkspaceRoot) ?? Point.Zero;
        ResetVoiceRecordingGestureUi();
        voiceGestureStartTask = StartVoiceRecordingGestureAsync(kind);
    }

    private void OnVoicePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!voicePointerActive || e.GetPosition(WorkspaceRoot) is not { } point)
        {
            return;
        }

        var cancelProgress = Math.Clamp(
            -(point.X - voicePointerStart.X) / VoiceCancelSwipeThreshold,
            0,
            1);
        voiceCancelBySwipe = cancelProgress >= 1;
        ApplyVoiceRecordingGestureProgress(cancelProgress);
    }

    private async void OnVoicePointerReleased(object? sender, PointerEventArgs e)
    {
        OnVoicePointerMoved(sender, e);
        await FinishVoiceRecordingGestureAsync();
    }

    private async Task StartVoiceRecordingGestureAsync(DesktopConversationDetailKind kind)
    {
        var cancellationToken = pageActivityCancellation?.Token ?? CancellationToken.None;
        if (kind == DesktopConversationDetailKind.Direct && voiceDirectTarget is { } direct)
        {
            await direct.StartVoiceRecordingAsync(cancellationToken);
        }
        else if (kind == DesktopConversationDetailKind.Group && voiceGroupTarget is { } group)
        {
            await group.StartVoiceRecordingAsync(cancellationToken);
        }

        if (IsVoiceRecording(kind))
        {
            StartRecordingUiTimer();
        }
    }

    private async Task FinishVoiceRecordingGestureAsync(bool forceCancel = false)
    {
        await voiceGestureCompletionGate.WaitAsync();
        try
        {
            if (!voicePointerActive && voiceGestureStartTask is null && !forceCancel)
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
            if (!IsVoiceRecording(voiceRecordingKind))
            {
                return;
            }

            var targetIsStillActive = isPageActive
                && Volatile.Read(ref voicePageExitCancellationRequested) == 0
                && voiceRecordingConversationId is { } targetId
                && viewModel.SelectedConversation?.Id == targetId;
            if (forceCancel || voiceCancelBySwipe || !targetIsStillActive)
            {
                await CancelVoiceRecordingAsync(voiceRecordingKind);
            }
            else
            {
                await SendVoiceRecordingAsync(voiceRecordingKind);
            }
        }
        catch (Exception exception)
        {
            CrashDiagnostics.LogException("DesktopWorkspacePage.VoiceGesture", exception);
        }
        finally
        {
            voiceCancelBySwipe = false;
            voiceDirectTarget = null;
            voiceGroupTarget = null;
            voiceRecordingConversationId = null;
            voiceGestureCompletionGate.Release();
        }
    }

    private bool IsVoiceRecording(DesktopConversationDetailKind kind) => kind switch
    {
        DesktopConversationDetailKind.Direct => voiceDirectTarget?.IsRecordingVoice == true,
        DesktopConversationDetailKind.Group => voiceGroupTarget?.IsRecordingVoice == true,
        _ => false
    };

    private Task CancelVoiceRecordingAsync(DesktopConversationDetailKind kind) => kind switch
    {
        DesktopConversationDetailKind.Direct when voiceDirectTarget is { } direct => direct.CancelVoiceRecordingAsync(),
        DesktopConversationDetailKind.Group when voiceGroupTarget is { } group => group.CancelVoiceRecordingAsync(),
        _ => Task.CompletedTask
    };

    private Task SendVoiceRecordingAsync(DesktopConversationDetailKind kind) => kind switch
    {
        DesktopConversationDetailKind.Direct when voiceDirectTarget is { } direct => direct.StopVoiceRecordingAndSendAsync(),
        DesktopConversationDetailKind.Group when voiceGroupTarget is { } group => group.StopVoiceRecordingAndSendAsync(),
        _ => Task.CompletedTask
    };

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
        DirectVoiceElapsedLabel.Text = "00:00";
        GroupVoiceElapsedLabel.Text = "00:00";
    }

    private void OnVoiceRecordingTick(object? sender, EventArgs e) => UpdateRecordingElapsed();

    private void UpdateRecordingElapsed()
    {
        var elapsed = DateTimeOffset.UtcNow - voiceRecordingStartedAt;
        var seconds = Math.Max(0, (int)elapsed.TotalSeconds);
        var label = $"{seconds / 60:00}:{seconds % 60:00}";
        if (voiceRecordingKind == DesktopConversationDetailKind.Direct)
        {
            DirectVoiceElapsedLabel.Text = label;
        }
        else if (voiceRecordingKind == DesktopConversationDetailKind.Group)
        {
            GroupVoiceElapsedLabel.Text = label;
        }
    }

    private void ApplyVoiceRecordingGestureProgress(double progress)
    {
        var panel = voiceRecordingKind == DesktopConversationDetailKind.Direct
            ? DirectVoiceRecordingPanel
            : GroupVoiceRecordingPanel;
        var cancel = voiceRecordingKind == DesktopConversationDetailKind.Direct
            ? DirectVoiceCancelPill
            : GroupVoiceCancelPill;
        var hint = voiceRecordingKind == DesktopConversationDetailKind.Direct
            ? DirectVoiceSlideHintLabel
            : GroupVoiceSlideHintLabel;
        panel.TranslationX = -VoiceCancelPanelTranslation * progress;
        cancel.Opacity = 1 - (0.55 * progress);
        hint.Text = progress >= 1 ? "Отпустите, чтобы отменить" : "Сдвиньте влево для отмены";
        hint.TextColor = progress >= 1
            ? ResourceColor("DangerColor", Colors.IndianRed)
            : ResourceColor("TextSecondary", Colors.Gray);
    }

    private void ResetVoiceRecordingGestureUi()
    {
        foreach (var panel in new[] { DirectVoiceRecordingPanel, GroupVoiceRecordingPanel })
        {
            panel.TranslationX = 0;
        }

        DirectVoiceCancelPill.Opacity = 1;
        GroupVoiceCancelPill.Opacity = 1;
        DirectVoiceSlideHintLabel.Text = "Сдвиньте влево для отмены";
        GroupVoiceSlideHintLabel.Text = "Сдвиньте влево для отмены";
        DirectVoiceSlideHintLabel.TextColor = ResourceColor("TextSecondary", Colors.Gray);
        GroupVoiceSlideHintLabel.TextColor = ResourceColor("TextSecondary", Colors.Gray);
    }

    private static Color ResourceColor(string key, Color fallback) =>
        Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color color
            ? color
            : fallback;

    private void OnDirectMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
#if DEBUG && DEEP_PHYSICAL_E2E
        UpdatePhysicalRouteNodeMarker();
#endif
        if (e.NewItems is not null)
        {
            _ = QueueImagePreviewsAsync(e.NewItems.OfType<ChatMessageItem>(), pageActivityCancellation?.Token ?? CancellationToken.None);
        }
    }

    private void OnGroupMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            _ = QueueImagePreviewsAsync(e.NewItems.OfType<GroupChatMessageItem>(), pageActivityCancellation?.Token ?? CancellationToken.None);
        }
    }

    private async Task QueueActiveImagePreviewsAsync(CancellationToken cancellationToken)
    {
        if (viewModel.IsDirectDetail)
        {
            await QueueImagePreviewsAsync(viewModel.DirectChat.Messages, cancellationToken);
        }
        else if (viewModel.IsGroupDetail)
        {
            await QueueImagePreviewsAsync(viewModel.GroupChat.Messages, cancellationToken);
        }
    }

    private async Task QueueImagePreviewsAsync(IEnumerable<ChatMessageItem> messages, CancellationToken cancellationToken)
    {
        foreach (var message in messages.Where(static item => item.IsImageMessage && !item.HasImagePreview))
        {
            var path = await ResolveImagePathAsync(message.PrimaryImageAttachment, cancellationToken);
            if (path is not null)
            {
                message.SetImagePreviewPath(path);
            }
        }
    }

    private async Task QueueImagePreviewsAsync(IEnumerable<GroupChatMessageItem> messages, CancellationToken cancellationToken)
    {
        foreach (var message in messages.Where(static item => item.IsImageMessage && !item.HasImagePreview))
        {
            var path = await ResolveImagePathAsync(message.PrimaryImageAttachment, cancellationToken);
            if (path is not null)
            {
                message.SetImagePreviewPath(path);
            }
        }
    }

    private async Task<string?> ResolveImagePathAsync(AttachmentMetadata? attachment, CancellationToken cancellationToken)
    {
        if (attachment is null)
        {
            return null;
        }

        try
        {
            var file = AttachmentOpenService.TryGetCachedFile(attachment);
            if (file is null && attachmentFiles.IsEnabled && attachment.RemoteUri is not null)
            {
                file = await AttachmentOpenService.DownloadToCacheAsync(attachment, attachmentFiles, cancellationToken);
            }

            return file?.Path;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            CrashDiagnostics.LogException("DesktopWorkspacePage.ImagePreview", ex);
            return null;
        }
    }

    private void RefreshActiveConversationLease()
    {
        activeConversationLease?.Dispose();
        activeConversationLease = isPageActive
            && viewModel.IsDetailPaneVisible
            && viewModel.SelectedConversation is { } selected
            ? activeConversationTracker.ActivateConversation(selected.Id)
            : null;
    }

    private async Task ConfigureSyncTimerAsync(CancellationToken cancellationToken)
    {
        var pushDriven = await syncPollingPolicy
            .IsPushDrivenSyncAvailableAsync(cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (IsCurrentPageActivity(cancellationToken))
            {
                EnsureSyncTimer(pushDriven ? TimeSpan.FromMinutes(15) : TimeSpan.FromSeconds(30));
            }
        });
    }

    private void EnsureSyncTimer(TimeSpan interval)
    {
        syncTimer ??= Dispatcher.CreateTimer();
        syncTimer.Tick -= OnSyncTimerTick;
        syncTimer.Tick += OnSyncTimerTick;
        syncTimer.Interval = interval;
        syncTimer.Start();
    }

    private void EnsureIncomingCallTimer()
    {
        incomingCallTimer ??= Dispatcher.CreateTimer();
        incomingCallTimer.Tick -= OnIncomingCallTick;
        incomingCallTimer.Tick += OnIncomingCallTick;
        incomingCallTimer.Interval = incomingCallPolling.CurrentDelay;
        incomingCallTimer.Start();
    }

    private async Task InitializeRealtimeServicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await backgroundSyncScheduling.EnsureScheduledForActiveAccountAsync(cancellationToken);
            if (Preferences.Default.Get(ClientSettingKeys.NotificationsFastMode, true)
                && !await syncPollingPolicy.IsPushDrivenSyncAvailableAsync(cancellationToken))
            {
                await pushRegistrationLifecycle.EnsureRegisteredAsync(cancellationToken);
                syncPollingPolicy.Invalidate();
            }

            await ConfigureSyncTimerAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (IsCurrentPageActivity(cancellationToken))
                {
                    EnsureIncomingCallTimer();
                }
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            CrashDiagnostics.LogException("DesktopWorkspacePage.RealtimeServices", ex);
        }
    }

    private void OnBackgroundSyncScheduled() =>
        MainThread.BeginInvokeOnMainThread(RequestSynchronization);

    private bool IsCurrentPageActivity(CancellationToken cancellationToken) =>
        isPageActive
        && pageActivityCancellation is { } activity
        && activity.Token == cancellationToken
        && !cancellationToken.IsCancellationRequested;

    private void OnSyncTimerTick(object? sender, EventArgs e) => RequestSynchronization();

    private async void OnIncomingCallTick(object? sender, EventArgs e)
    {
        try
        {
            await CheckIncomingCallsAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task CheckIncomingCallsAsync()
    {
        if (checkingCalls || pageActivityCancellation is not { } activity)
        {
            return;
        }

        var polling = incomingCallPolling;
        try
        {
            checkingCalls = true;
            var offers = await callCoordinator.ReceiveIncomingOffersAsync(activity.Token);
            activity.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(incomingCallPolling, polling))
            {
                return;
            }
            var success = polling.RecordSuccess();
            if (incomingCallTimer is not null)
            {
                incomingCallTimer.Interval = success.NextDelay;
            }
            if (success.Recovered)
            {
                CrashDiagnostics.LogInfo(
                    "DesktopWorkspacePage.IncomingCalls",
                    "Incoming-call polling recovered.");
            }
            foreach (var offer in offers)
            {
                activity.Token.ThrowIfCancellationRequested();
                var known = viewModel.ConversationList.Conversations
                    .FirstOrDefault(item => item.Id.Value == offer.RemoteParty.Value);
                var call = known is null ? offer : offer with { DisplayName = known.Title };
                var kind = call.IsVideo ? "Видеозвонок" : "Аудиозвонок";
                var accepted = await DisplayAlertAsync(
                    kind,
                    $"{call.DisplayName} звонит вам в Deep.",
                    "Ответить",
                    "Отклонить");
                activity.Token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(incomingCallPolling, polling))
                {
                    return;
                }
                if (!accepted)
                {
                    await callCoordinator.SendAsync(
                        call,
                        CallSignalType.Bye,
                        "{\"reason\":\"declined\"}",
                        activity.Token);
                    continue;
                }

                await Shell.Current.GoToAsync(
                    ShellRouteCatalog.Call,
                    new ShellNavigationQueryParameters { ["call"] = call });
                return;
            }
        }
        catch (OperationCanceledException) when (activity.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            var failure = polling.RecordFailure(
                exception,
                activity.Token);
            if (ReferenceEquals(incomingCallPolling, polling) &&
                incomingCallTimer is not null)
            {
                incomingCallTimer.Interval = failure.NextDelay;
            }
            if (failure.EnteredDegraded)
            {
                CrashDiagnostics.LogInfo(
                    "DesktopWorkspacePage.IncomingCalls",
                    "Incoming-call polling degraded; retry cadence reduced.");
            }
            if (failure.ShouldLogUnexpected)
            {
                CrashDiagnostics.LogException(
                    "DesktopWorkspacePage.IncomingCalls.Unexpected",
                    exception);
            }
        }
        finally
        {
            checkingCalls = false;
        }
    }

    private void RequestSynchronization()
    {
        if (pageActivityCancellation is not { } activity || activity.IsCancellationRequested)
        {
            return;
        }

        Interlocked.Exchange(ref syncRequested, 1);
        lock (syncPumpGate)
        {
            if (syncPump is { IsCompleted: false })
            {
                return;
            }

            syncPump = RunSyncPumpAsync(activity);
        }
    }

    private async Task RunSyncPumpAsync(CancellationTokenSource activity)
    {
        try
        {
            while (!activity.IsCancellationRequested
                   && Interlocked.Exchange(ref syncRequested, 0) != 0)
            {
                var synchronized = await viewModel.RefreshAsync(activity.Token);
                if (!synchronized)
                {
                    return;
                }

                if (viewModel.IsDirectDetail)
                {
                    await viewModel.DirectChat.ReceiveAsync(activity.Token);
                }
                else if (viewModel.IsGroupDetail)
                {
                    await viewModel.GroupChat.RefreshAsync(activity.Token);
                }

                BackgroundSyncBridge.MarkHandled();
            }
        }
        catch (OperationCanceledException) when (activity.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            CrashDiagnostics.LogException("DesktopWorkspacePage.Sync", ex);
        }
        finally
        {
            var restart = false;
            lock (syncPumpGate)
            {
                syncPump = null;
                restart = Volatile.Read(ref syncRequested) != 0
                    && pageActivityCancellation is { IsCancellationRequested: false };
            }

            if (restart)
            {
                MainThread.BeginInvokeOnMainThread(RequestSynchronization);
            }
        }
    }

    private void ScrollActiveMessagesToEnd()
    {
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(80), () =>
        {
            if (viewModel.IsDirectDetail && viewModel.DirectChat.Messages.Count > 0)
            {
                DirectMessagesCollection.ScrollTo(viewModel.DirectChat.Messages[^1], ScrollToPosition.End, animate: false);
            }
            else if (viewModel.IsGroupDetail && viewModel.GroupChat.Messages.Count > 0)
            {
                GroupMessagesCollection.ScrollTo(viewModel.GroupChat.Messages[^1], ScrollToPosition.End, animate: false);
            }
        });
    }

    private void HideTransientMenus()
    {
        HeaderOverflowMenu.IsVisible = false;
        GroupMembersPanel.IsVisible = false;
        DesktopMessageContextMenu.IsVisible = false;
        ClearSelectedMessage();
    }

    private void ClearSelectedMessage()
    {
        selectedDirectMessage = null;
        selectedGroupMessage = null;
    }

    private static bool MessageMatches(ChatMessageItem message, string query) =>
        Contains(message.Body, query)
        || Contains(message.ReplyPreview, query)
        || Contains(message.AttachmentSummary, query);

    private static bool MessageMatches(GroupChatMessageItem message, string query) =>
        Contains(message.Body, query)
        || Contains(message.SenderLabel, query)
        || Contains(message.ReplyPreview, query)
        || Contains(message.AttachmentSummary, query);

    private static bool Contains(string? value, string query) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains(query, StringComparison.CurrentCultureIgnoreCase);
}
