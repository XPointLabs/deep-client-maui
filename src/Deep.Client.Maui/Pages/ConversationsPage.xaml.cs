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
    private const int ChatOpenPreloadPageSize = 20;
    private static readonly SemaphoreSlim ChatOpenPreloadGate = new(1, 1);
    private readonly object chatOpenPreloadCancellationSync = new();
    private IDispatcherTimer? autoSyncTimer;
    private IDispatcherTimer? incomingCallTimer;
    private readonly ConversationsViewModel viewModel;
    private readonly ClientRuntime runtime;
    private readonly ChatOpenUiCache openCache;
    private readonly INetworkStatusService networkStatusService;
    private readonly PushRegistrationLifecycleCoordinator pushRegistrationLifecycle;
    private readonly CallSessionCoordinator callCoordinator;
    private readonly SyncPollingPolicy syncPollingPolicy;
    private readonly BackgroundSyncSchedulingCoordinator backgroundSyncScheduling;
    private IncomingCallPollingBackoff incomingCallPolling = new();
    private bool checkingCalls;
    private bool preloadingChatOpenCache;
    private readonly object syncPumpGate = new();
    private Task? syncPump;
    private int syncRequested;
    private CancellationTokenSource? pageActivityCancellation;
    private CancellationTokenSource? chatOpenPreloadCancellation;
#if DEBUG && DEEP_PHYSICAL_E2E
    private Label? physicalRuntimeReadyMarker;
#endif

    public ConversationsPage(
        ConversationsViewModel viewModel,
        ClientRuntime runtime,
        ChatOpenUiCache openCache,
        INetworkStatusService networkStatusService,
        PushRegistrationLifecycleCoordinator pushRegistrationLifecycle,
        CallSessionCoordinator callCoordinator,
        SyncPollingPolicy syncPollingPolicy,
        BackgroundSyncSchedulingCoordinator backgroundSyncScheduling)
    {
        InitializeComponent();
#if DEBUG && DEEP_PHYSICAL_E2E
        CreatePhysicalRuntimeReadyMarker();
#endif
        this.viewModel = viewModel;
        this.runtime = runtime;
        this.openCache = openCache;
        this.networkStatusService = networkStatusService;
        this.pushRegistrationLifecycle = pushRegistrationLifecycle;
        this.callCoordinator = callCoordinator;
        this.syncPollingPolicy = syncPollingPolicy;
        this.backgroundSyncScheduling = backgroundSyncScheduling;
        BindingContext = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
#if DEBUG && DEEP_PHYSICAL_E2E
        SetPhysicalRuntimeReady(false);
#endif
        pageActivityCancellation?.Cancel();
        pageActivityCancellation?.Dispose();
        var activityCancellation = new CancellationTokenSource();
        pageActivityCancellation = activityCancellation;
        incomingCallPolling = new IncomingCallPollingBackoff();
        var cancellationToken = activityCancellation.Token;
        networkStatusService.StatusChanged -= OnNetworkStatusChanged;
        networkStatusService.StatusChanged += OnNetworkStatusChanged;
        BackgroundSyncBridge.SyncScheduled -= OnBackgroundSyncScheduled;
        BackgroundSyncBridge.SyncScheduled += OnBackgroundSyncScheduled;
        UpdateProfileAvatarUi();
        UpdateNetworkUi();
        try
        {
            var activeAccount = await runtime.Accounts.GetActiveAccountAsync(cancellationToken);
            if (activeAccount is not null)
            {
                AttachmentOpenService.SetAccountScope(activeAccount.SessionId.Value);
            }

            var loadCachedStopwatch = System.Diagnostics.Stopwatch.StartNew();
            await viewModel.LoadCachedAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            CrashDiagnostics.LogInfo(
                "Perf.Conversations",
                $"LoadCached count={viewModel.Conversations.Count} elapsedMs={loadCachedStopwatch.ElapsedMilliseconds}");
            if (!ReferenceEquals(pageActivityCancellation, activityCancellation))
            {
                return;
            }

            _ = PreloadVisibleChatOpenCacheAsync();
            ScheduleForegroundCatchUpSync();
            if (BackgroundSyncBridge.HasPendingSync())
            {
                RequestConversationSync(cancellationToken);
            }

            _ = InitializeRealtimeServicesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            CrashDiagnostics.LogException("ConversationsPage.OnAppearing", exception);
        }
    }

    private async Task InitializeRealtimeServicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await backgroundSyncScheduling
                .EnsureScheduledForActiveAccountAsync(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await EnsurePushRegistrationAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await MainThread.InvokeOnMainThreadAsync(ConfigureAutoSyncAsync).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await MainThread.InvokeOnMainThreadAsync(
                () => ConfigureIncomingCallPollingAsync(cancellationToken))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            CrashDiagnostics.LogException("ConversationsPage.RealtimeServices", ex);
        }
    }

    private async Task EnsurePushRegistrationAsync(CancellationToken cancellationToken)
    {
        if (!Preferences.Default.Get(ClientSettingKeys.NotificationsFastMode, true))
        {
            return;
        }

        if (await syncPollingPolicy.IsPushDrivenSyncAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await pushRegistrationLifecycle.EnsureRegisteredAsync(cancellationToken).ConfigureAwait(false);
        syncPollingPolicy.Invalidate();
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
                RequestConversationSync(pageCancellation.Token);
            }
        });
    }

    private void RequestConversationSync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
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

            syncPump = RunSyncPumpAsync(cancellationToken);
        }
    }

    private async Task RunSyncPumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested
                   && Interlocked.Exchange(ref syncRequested, 0) != 0)
            {
                try
                {
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    var synchronized = await viewModel.SyncAsync(cancellationToken);
                    if (!synchronized)
                    {
#if DEBUG && DEEP_PHYSICAL_E2E
                        SetPhysicalRuntimeReady(false);
#endif
                        CrashDiagnostics.LogInfo("Sync", "Foreground synchronization failed; pending work remains queued.");
                        return;
                    }

#if DEBUG && DEEP_PHYSICAL_E2E
                    SetPhysicalRuntimeReady(true);
#endif
                    BackgroundSyncBridge.MarkHandled();
                    CrashDiagnostics.LogInfo(
                        "Perf.Conversations",
                        $"BackgroundSync count={viewModel.Conversations.Count} elapsedMs={stopwatch.ElapsedMilliseconds}");
                }
                catch (Exception ex) when (IsCancellation(ex) || cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
#if DEBUG && DEEP_PHYSICAL_E2E
                    SetPhysicalRuntimeReady(false);
#endif
                    CrashDiagnostics.LogException("ConversationsPage.BackgroundSync", ex);
                    return;
                }
            }
        }
        finally
        {
            lock (syncPumpGate)
            {
                syncPump = null;
                if (!cancellationToken.IsCancellationRequested && Volatile.Read(ref syncRequested) != 0)
                {
                    syncPump = RunSyncPumpAsync(cancellationToken);
                }
            }
        }
    }

    private async void OnConversationTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Element { BindingContext: ConversationListItem selected })
        {
            return;
        }

        try
        {
            var navigationStopwatch = System.Diagnostics.Stopwatch.StartNew();
            CrashDiagnostics.LogInfo("Perf.Chat", "NavigateStart");
            viewModel.SelectedConversation = selected;
            pageActivityCancellation?.Cancel();
            CancelChatOpenPreload();
            await OpenConversationAsync(selected);
            CrashDiagnostics.LogInfo("Perf.Chat", $"NavigateComplete elapsedMs={navigationStopwatch.ElapsedMilliseconds}");
        }
        catch (Exception exception)
        {
            CrashDiagnostics.LogException("ConversationsPage.OpenConversation", exception);
        }
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
        try
        {
            await Shell.Current.GoToAsync(ShellRouteCatalog.StartConversation);
        }
        catch (Exception exception)
        {
            CrashDiagnostics.LogException("ConversationsPage.NewConversation", exception);
        }
    }

    private async void OnProfileClicked(object? sender, EventArgs e)
    {
        try
        {
            await Shell.Current.GoToAsync(ShellRouteCatalog.Settings);
        }
        catch (Exception exception)
        {
            CrashDiagnostics.LogException("ConversationsPage.OpenSettings", exception);
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

    private static Task OpenConversationAsync(ConversationListItem selected)
    {
        var route = selected.Kind switch
        {
            ConversationKind.OneToOne =>
                $"{ShellRouteCatalog.Chat}?sessionId={Uri.EscapeDataString(selected.Id.Value)}&displayName={Uri.EscapeDataString(selected.Title)}",
            ConversationKind.GroupV2 or ConversationKind.Community =>
                $"{ShellRouteCatalog.GroupChat}?groupId={Uri.EscapeDataString(selected.Id.Value)}&displayName={Uri.EscapeDataString(selected.Title)}",
            _ => throw new InvalidDataException("Conversation kind is unsupported.")
        };

        return Shell.Current.GoToAsync(route, animate: false);
    }

#if DEBUG && DEEP_PHYSICAL_E2E
    private void CreatePhysicalRuntimeReadyMarker()
    {
        physicalRuntimeReadyMarker = new Label
        {
            AutomationId = "PhysicalE2E.RuntimeReadyMarker",
            Text = "ready",
            IsVisible = false,
            FontSize = 1,
            Opacity = 0.01,
            InputTransparent = true,
            ZIndex = 100
        };
        RootLayout.Children.Add(physicalRuntimeReadyMarker);
        Grid.SetRow(physicalRuntimeReadyMarker, 3);
    }

    private void SetPhysicalRuntimeReady(bool ready)
    {
        if (physicalRuntimeReadyMarker is null)
            return;
        void Apply() => physicalRuntimeReadyMarker.IsVisible = ready;
        if (MainThread.IsMainThread)
            Apply();
        else
            MainThread.BeginInvokeOnMainThread(Apply);
    }
#endif

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
        var pushDriven = await syncPollingPolicy.IsPushDrivenSyncAvailableAsync();
        EnsureAutoSync(pushDriven ? TimeSpan.FromMinutes(15) : TimeSpan.FromSeconds(30));
    }

    private void EnsureAutoSync(TimeSpan interval)
    {
        if (autoSyncTimer is null)
        {
            autoSyncTimer = Dispatcher.CreateTimer();
            autoSyncTimer.Tick += OnAutoSyncTick;
        }

        autoSyncTimer.Interval = interval;
        autoSyncTimer.Start();
    }

    private void EnsureIncomingCallPolling()
    {
        if (incomingCallTimer is null)
        {
            incomingCallTimer = Dispatcher.CreateTimer();
            incomingCallTimer.Tick += OnIncomingCallTick;
        }

        incomingCallTimer.Interval = incomingCallPolling.CurrentDelay;
        incomingCallTimer.Start();
    }

    private Task ConfigureIncomingCallPollingAsync(
        CancellationToken cancellationToken)
    {
        if (!IncomingCallPollingBackoff.IsCurrentActivity(
                pageActivityCancellation,
                cancellationToken))
        {
            return Task.CompletedTask;
        }

        EnsureIncomingCallPolling();
        return Task.CompletedTask;
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
                RequestConversationSync(cancellationToken);
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
                if (openCache.TryGet(recipient, runtime.Clock.UtcNow, out var cached)
                    && cached.Conversation.UpdatedAt >= candidate.UpdatedAt)
                {
                    continue;
                }

                openCache.Remove(recipient);

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
            snapshot.RecentMessages.Count == 0 ? null : snapshot.RecentMessages[0].Id,
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
            message.ReactionItems,
            message.ExpiresAt);

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

    private void OnAutoSyncTick(object? sender, EventArgs e)
    {
        if (pageActivityCancellation is not { } pageCancellation)
        {
            return;
        }

        if (viewModel.IsBusy)
        {
            return;
        }

        RequestConversationSync(pageCancellation.Token);
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
        if (checkingCalls || pageActivityCancellation is not { } activity)
        {
            return;
        }

        var cancellationToken = activity.Token;
        var polling = incomingCallPolling;
        try
        {
            checkingCalls = true;
            var offers = await callCoordinator.ReceiveIncomingOffersAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
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
                    "ConversationsPage.IncomingCalls",
                    "Incoming-call polling recovered.");
            }
            foreach (var offer in offers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var known = viewModel.Conversations.FirstOrDefault(item => item.Id.Value == offer.RemoteParty.Value);
                var call = known is null ? offer : offer with { DisplayName = known.Title };
                var kind = call.IsVideo ? "Видеозвонок" : "Аудиозвонок";
                var accepted = await DisplayAlertAsync(
                    kind,
                    $"{call.DisplayName} звонит вам в Deep.",
                    "Ответить",
                    "Отклонить");
                cancellationToken.ThrowIfCancellationRequested();
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
                        cancellationToken);
                    continue;
                }

                await Shell.Current.GoToAsync(
                    ShellRouteCatalog.Call,
                    new ShellNavigationQueryParameters { ["call"] = call });
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            var failure = polling.RecordFailure(
                exception,
                cancellationToken);
            if (ReferenceEquals(incomingCallPolling, polling) &&
                incomingCallTimer is not null)
            {
                incomingCallTimer.Interval = failure.NextDelay;
            }
            if (failure.EnteredDegraded)
            {
                CrashDiagnostics.LogInfo(
                    "ConversationsPage.IncomingCalls",
                    "Incoming-call polling degraded; retry cadence reduced.");
            }
            if (failure.ShouldLogUnexpected)
            {
                CrashDiagnostics.LogException(
                    "ConversationsPage.IncomingCalls.Unexpected",
                    exception);
            }
        }
        finally
        {
            checkingCalls = false;
        }
    }
}
