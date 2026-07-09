using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui.Pages;

public partial class ConversationsPage : ContentPage
{
    private const string AvatarFileName = "profile-avatar.jpg";
    private const int ChatOpenPreloadPageSize = 30;
    private static readonly SemaphoreSlim ChatOpenPreloadGate = new(1, 1);
    private readonly object chatOpenPreloadCancellationSync = new();
    private IDispatcherTimer? autoSyncTimer;
    private IDispatcherTimer? incomingCallTimer;
    private readonly ConversationsViewModel viewModel;
    private readonly ClientRuntime runtime;
    private readonly ChatOpenUiCache openCache;
    private readonly INetworkStatusService networkStatusService;
    private readonly IPushRegistrationCoordinator pushRegistration;
    private readonly CallSessionCoordinator callCoordinator;
    private readonly SyncPollingPolicy syncPollingPolicy;
    private bool hasLoaded;
    private bool pushRegistrationStarted;
    private bool checkingCalls;
    private bool preloadingChatOpenCache;
    private bool syncingConversations;
    private CancellationTokenSource? pageActivityCancellation;
    private CancellationTokenSource? chatOpenPreloadCancellation;

    public ConversationsPage(
        ConversationsViewModel viewModel,
        ClientRuntime runtime,
        ChatOpenUiCache openCache,
        INetworkStatusService networkStatusService,
        IPushRegistrationCoordinator pushRegistration,
        CallSessionCoordinator callCoordinator,
        SyncPollingPolicy syncPollingPolicy)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        this.runtime = runtime;
        this.openCache = openCache;
        this.networkStatusService = networkStatusService;
        this.pushRegistration = pushRegistration;
        this.callCoordinator = callCoordinator;
        this.syncPollingPolicy = syncPollingPolicy;
        BindingContext = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        pageActivityCancellation?.Cancel();
        pageActivityCancellation?.Dispose();
        pageActivityCancellation = new CancellationTokenSource();
        networkStatusService.StatusChanged -= OnNetworkStatusChanged;
        networkStatusService.StatusChanged += OnNetworkStatusChanged;
        BackgroundSyncBridge.SyncScheduled -= OnBackgroundSyncScheduled;
        BackgroundSyncBridge.SyncScheduled += OnBackgroundSyncScheduled;
        ApplyAndroidSafeAreaCompensation();
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(120), ApplyAndroidSafeAreaCompensation);
        UpdateProfileAvatarUi();
        UpdateNetworkUi();
        var loadCachedStopwatch = System.Diagnostics.Stopwatch.StartNew();
        await viewModel.LoadCachedAsync();
        CrashDiagnostics.LogInfo(
            "Perf.Conversations",
            $"LoadCached count={viewModel.Conversations.Count} elapsedMs={loadCachedStopwatch.ElapsedMilliseconds}");
        _ = PreloadVisibleChatOpenCacheAsync();
        if (!hasLoaded)
        {
            hasLoaded = true;
        }

        await EnsurePushRegistrationAsync();
        await ConfigureAutoSyncAsync();
        await ConfigureIncomingCallPollingAsync();
        ScheduleForegroundCatchUpSync();
    }

    private async Task EnsurePushRegistrationAsync()
    {
        if (pushRegistrationStarted ||
            !Preferences.Default.Get(ClientSettingKeys.NotificationsFastMode, true))
        {
            return;
        }

        if (await syncPollingPolicy.IsPushDrivenSyncAvailableAsync())
        {
            pushRegistrationStarted = true;
            return;
        }

        pushRegistrationStarted = true;
        _ = RegisterPushAsync();
    }

    private async Task RegisterPushAsync()
    {
        try
        {
            await pushRegistration.RegisterAsync();
            syncPollingPolicy.Invalidate();
            await MainThread.InvokeOnMainThreadAsync(ConfigureAutoSyncAsync);
        }
        catch (Exception ex)
        {
            CrashDiagnostics.LogInfo("Push", $"Automatic registration failed: {ex.Message}");
            pushRegistrationStarted = false;
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        networkStatusService.StatusChanged -= OnNetworkStatusChanged;
        BackgroundSyncBridge.SyncScheduled -= OnBackgroundSyncScheduled;
        autoSyncTimer?.Stop();
        incomingCallTimer?.Stop();
        pageActivityCancellation?.Cancel();
        pageActivityCancellation?.Dispose();
        pageActivityCancellation = null;
        CancelChatOpenPreload();
    }

    private void OnBackgroundSyncScheduled()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (pageActivityCancellation is { } pageCancellation)
            {
                _ = SyncConversationsInBackgroundAsync(pageCancellation.Token);
            }
        });
    }

    private async Task SyncConversationsInBackgroundAsync(CancellationToken cancellationToken)
    {
        if (viewModel.IsBusy || syncingConversations || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            syncingConversations = true;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await viewModel.SyncAsync(cancellationToken);
            CrashDiagnostics.LogInfo(
                "Perf.Conversations",
                $"BackgroundSync count={viewModel.Conversations.Count} elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (Exception ex) when (IsCancellation(ex) || cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            CrashDiagnostics.LogException("ConversationsPage.BackgroundSync", ex);
        }
        finally
        {
            syncingConversations = false;
        }
    }

    private async void OnConversationTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Element { BindingContext: ConversationListItem selected })
        {
            return;
        }

        viewModel.SelectedConversation = selected;
        pageActivityCancellation?.Cancel();
        CancelChatOpenPreload();
        await OpenConversationAsync(selected);
    }

    private void OnSearchClicked(object? sender, EventArgs e)
    {
        SearchEntry.IsVisible = !SearchEntry.IsVisible;
        if (SearchEntry.IsVisible)
        {
            SearchEntry.Focus();
        }
        else
        {
            viewModel.SearchQuery = string.Empty;
        }
    }

    private async void OnNewConversationClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync(ShellRouteCatalog.StartConversation);
    }

    private async void OnProfileClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync(ShellRouteCatalog.Settings);
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

    private static string GetAvatarPath() => Path.Combine(FileSystem.Current.AppDataDirectory, AvatarFileName);

    private void ApplyAndroidSafeAreaCompensation()
    {
#if ANDROID
        var missingTop = AndroidSafeArea.GetTopOverlap(HeaderBar);
        if (missingTop <= 0.5)
        {
            return;
        }

        RootLayout.Padding = new Thickness(
            RootLayout.Padding.Left,
            RootLayout.Padding.Top + missingTop,
            RootLayout.Padding.Right,
            RootLayout.Padding.Bottom);
#endif
    }

    private static Task OpenConversationAsync(ConversationListItem selected)
    {
        var route = selected.Kind == ConversationKind.OneToOne
            ? $"{ShellRouteCatalog.Chat}?sessionId={Uri.EscapeDataString(selected.Id.Value)}&displayName={Uri.EscapeDataString(selected.Title)}"
            : $"{ShellRouteCatalog.GroupChat}?groupId={Uri.EscapeDataString(selected.Id.Value)}&displayName={Uri.EscapeDataString(selected.Title)}";

        return Shell.Current.GoToAsync(route, animate: false);
    }

    private void OnNetworkStatusChanged(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(UpdateNetworkUi);
    }

    private void UpdateNetworkUi()
    {
        var connected = networkStatusService.IsConnected;
        if (ProfileStatusDot is not null)
        {
            var key = connected ? "PrimaryColor" : "DangerColor";
            var app = Application.Current;
            if (app is not null && app.Resources.TryGetValue(key, out var value) && value is Color color)
            {
                ProfileStatusDot.Background = new SolidColorBrush(color);
            }
        }

        if (NetworkBanner is not null && NetworkBannerText is not null)
        {
            NetworkBanner.IsVisible = !connected;
            NetworkBannerText.Text = networkStatusService.ConnectionLabel;
        }
    }

    private async Task ConfigureAutoSyncAsync()
    {
        if (await syncPollingPolicy.IsPushDrivenSyncAvailableAsync())
        {
            autoSyncTimer?.Stop();
            return;
        }

        EnsureAutoSync();
    }

    private void EnsureAutoSync()
    {
        if (autoSyncTimer is null)
        {
            autoSyncTimer = Dispatcher.CreateTimer();
            autoSyncTimer.Interval = TimeSpan.FromSeconds(30);
            autoSyncTimer.Tick += OnAutoSyncTick;
        }

        autoSyncTimer.Start();
    }

    private void EnsureIncomingCallPolling()
    {
        if (incomingCallTimer is null)
        {
            incomingCallTimer = Dispatcher.CreateTimer();
            incomingCallTimer.Interval = TimeSpan.FromSeconds(30);
            incomingCallTimer.Tick += OnIncomingCallTick;
        }

        incomingCallTimer.Start();
    }

    private async Task ConfigureIncomingCallPollingAsync()
    {
        if (await syncPollingPolicy.IsPushDrivenSyncAvailableAsync())
        {
            incomingCallTimer?.Stop();
            return;
        }

        EnsureIncomingCallPolling();
    }

    private void ScheduleForegroundCatchUpSync()
    {
        if (pageActivityCancellation is not { } pageCancellation)
        {
            return;
        }

        var cancellationToken = pageCancellation.Token;
        Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(12), () =>
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _ = SyncConversationsInBackgroundAsync(cancellationToken);
            }
        });
    }

    private async Task PreloadVisibleChatOpenCacheAsync()
    {
        if (preloadingChatOpenCache ||
            runtime.Store is not IOneToOneConversationOpenRepository openRepository)
        {
            return;
        }

        var candidates = viewModel.Conversations
            .Where(static item => item.Kind == ConversationKind.OneToOne)
            .Take(1)
            .ToArray();
        if (candidates.Length == 0)
        {
            return;
        }

        var preloadCancellation = BeginChatOpenPreloadCancellation();
        var cancellationToken = preloadCancellation.Token;

        preloadingChatOpenCache = true;
        bool preloadGateAcquired;
        try
        {
            preloadGateAcquired = await ChatOpenPreloadGate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            preloadingChatOpenCache = false;
            FinishChatOpenPreloadCancellation(preloadCancellation);
            return;
        }

        if (!preloadGateAcquired)
        {
            preloadingChatOpenCache = false;
            FinishChatOpenPreloadCancellation(preloadCancellation);
            return;
        }

        try
        {
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var recipient = SessionId.Parse(candidate.Id.Value);
                if (openCache.TryGet(recipient, out _))
                {
                    continue;
                }

                var snapshot = await openRepository.OpenOneToOneConversationAsync(
                    recipient,
                    candidate.Title,
                    ChatOpenPreloadPageSize,
                    runtime.Clock.UtcNow,
                    cancellationToken,
                    markAsRead: false).ConfigureAwait(false);
                if (snapshot is null)
                {
                    continue;
                }

                openCache.Store(ToOpenUiSnapshot(recipient, snapshot));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            CrashDiagnostics.LogException("ConversationsPage.PreloadOpenCache", ex);
        }
        finally
        {
            ChatOpenPreloadGate.Release();
            preloadingChatOpenCache = false;
            FinishChatOpenPreloadCancellation(preloadCancellation);
        }
    }

    private CancellationTokenSource BeginChatOpenPreloadCancellation()
    {
        var next = new CancellationTokenSource();
        CancellationTokenSource? previous;
        lock (chatOpenPreloadCancellationSync)
        {
            previous = chatOpenPreloadCancellation;
            chatOpenPreloadCancellation = next;
        }

        previous?.Cancel();
        return next;
    }

    private void CancelChatOpenPreload()
    {
        CancellationTokenSource? cancellation;
        lock (chatOpenPreloadCancellationSync)
        {
            cancellation = chatOpenPreloadCancellation;
            chatOpenPreloadCancellation = null;
        }

        cancellation?.Cancel();
    }

    private void FinishChatOpenPreloadCancellation(CancellationTokenSource cancellation)
    {
        lock (chatOpenPreloadCancellationSync)
        {
            if (ReferenceEquals(chatOpenPreloadCancellation, cancellation))
            {
                chatOpenPreloadCancellation = null;
            }
        }

        cancellation.Dispose();
    }

    private ChatOpenUiSnapshot ToOpenUiSnapshot(SessionId recipient, OneToOneConversationOpenSnapshot snapshot)
    {
        var items = snapshot.RecentMessages
            .Select(message => ToChatMessageItem(ApplyReadCursor(message, snapshot.ReadAt)))
            .ToArray();
        return new ChatOpenUiSnapshot(
            snapshot.ActiveAccount,
            recipient,
            snapshot.Conversation,
            snapshot.Contact?.IsBlocked == true,
            recipient != snapshot.ActiveAccount.SessionId
                && snapshot.Contact is { IsApproved: false, IsBlocked: false },
            items,
            snapshot.RecentMessages.Count == 0 ? null : snapshot.RecentMessages[0].CreatedAt,
            snapshot.RecentMessages.Count == ChatOpenPreloadPageSize,
            runtime.Clock.UtcNow);
    }

    private static ChatMessageItem ToChatMessageItem(Message message) =>
        new(
            message.Id,
            message.Body,
            message.Direction,
            message.DeliveryState,
            message.CreatedAt,
            message.Attachments,
            message.ReplyTo,
            message.ReactionItems);

    private static Message ApplyReadCursor(Message message, DateTimeOffset readAt) =>
        message.Direction == MessageDirection.Incoming && message.CreatedAt <= readAt
            ? message.Mark(MessageDeliveryState.Read, readAt)
            : message;

    private static bool IsCancellation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException)
            {
                return true;
            }
        }

        return false;
    }

    private async void OnAutoSyncTick(object? sender, EventArgs e)
    {
        if (pageActivityCancellation is not { } pageCancellation)
        {
            return;
        }

        if (viewModel.IsBusy)
        {
            return;
        }

        await SyncConversationsInBackgroundAsync(pageCancellation.Token);
    }

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
        if (checkingCalls)
        {
            return;
        }

        try
        {
            checkingCalls = true;
            var offers = await callCoordinator.ReceiveIncomingOffersAsync();
            foreach (var offer in offers)
            {
                var known = viewModel.Conversations.FirstOrDefault(item => item.Id.Value == offer.RemoteParty.Value);
                var call = known is null ? offer : offer with { DisplayName = known.Title };
                var kind = call.IsVideo ? "Видеозвонок" : "Аудиозвонок";
                var accepted = await DisplayAlertAsync(
                    kind,
                    $"{call.DisplayName} звонит вам в Deep.",
                    "Ответить",
                    "Отклонить");
                if (!accepted)
                {
                    await callCoordinator.SendAsync(
                        call,
                        CallSignalType.Bye,
                        "{\"reason\":\"declined\"}");
                    continue;
                }

                await Shell.Current.GoToAsync(
                    ShellRouteCatalog.Call,
                    new ShellNavigationQueryParameters { ["call"] = call });
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or System.Net.WebException)
        {
            CrashDiagnostics.LogException("ConversationsPage.IncomingCalls", exception);
        }
        finally
        {
            checkingCalls = false;
        }
    }
}
