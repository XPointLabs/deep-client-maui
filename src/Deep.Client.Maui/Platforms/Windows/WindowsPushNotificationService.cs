#if WINDOWS
using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Services;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Microsoft.Windows.PushNotifications;
#endif

namespace Deep.Client.Maui;

#if WINDOWS
internal static class WindowsPushNotificationService
{
    private static readonly object Sync = new();
    private static readonly SemaphoreSlim ChannelGate = new(1, 1);
    private static readonly SemaphoreSlim PayloadGate = new(1, 1);
    private static readonly List<PushNotificationChannel> OpenChannels = [];
    private static PushNotificationManager? manager;
    private static AppNotificationManager? appNotificationManager;
    private static bool pushInitialized;
    private static bool appNotificationsInitialized;

    public static void Initialize()
    {
        InitializePushNotifications();
        InitializeAppNotifications();
    }

    public static AppActivationArguments? GetCurrentActivation()
    {
        try
        {
            return AppInstance.GetCurrent().GetActivatedEventArgs();
        }
        catch (Exception exception)
        {
            CrashDiagnostics.LogException("Windows.Activation", exception);
            return null;
        }
    }

    public static bool TryGetPushActivation(
        AppActivationArguments? activation,
        out PushNotificationReceivedEventArgs? pushArgs)
    {
        pushArgs = activation?.Kind == ExtendedActivationKind.Push
            ? activation.Data as PushNotificationReceivedEventArgs
            : null;
        return pushArgs is not null;
    }

    public static async Task<string?> GetChannelUriAsync(CancellationToken cancellationToken)
    {
        InitializePushNotifications();
        var currentManager = manager;
        if (currentManager is null)
        {
            return null;
        }

        var configuredRemoteId = MauiProgram.ResolveRuntimeSetting(MauiProgram.WindowsPushRemoteIdEnv);
        if (!Guid.TryParse(configuredRemoteId, out var remoteId) || remoteId == Guid.Empty)
        {
            CrashDiagnostics.LogInfo(
                "Windows.Push",
                $"{MauiProgram.WindowsPushRemoteIdEnv} is not configured; polling remains enabled.");
            return null;
        }

        await ChannelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await currentManager
                .CreateChannelAsync(remoteId)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            if (result.Status != PushNotificationChannelStatus.CompletedSuccess || result.Channel is null)
            {
                CrashDiagnostics.LogInfo(
                    "Windows.Push.Channel",
                    $"Channel request failed: status={result.Status}; error={result.ExtendedError?.HResult}.");
                return null;
            }

            var nextChannel = result.Channel;
            var channelUri = nextChannel.Uri?.ToString();
            if (!WnsChannelUriValidator.IsValid(channelUri))
            {
                nextChannel.Close();
                CrashDiagnostics.LogInfo("Windows.Push.Channel", "WNS returned an invalid channel URI.");
                return null;
            }

            // Keep the previous channel alive until the new URI has been accepted and
            // durably stored by the Deep push server. WNS permits concurrent channels.
            lock (Sync)
            {
                if (!OpenChannels.Any(existing => string.Equals(
                        existing.Uri?.ToString(),
                        channelUri,
                        StringComparison.Ordinal)))
                {
                    OpenChannels.Add(nextChannel);
                }
            }

            PushTokenBridge.Set("wns", channelUri!);
            CrashDiagnostics.LogInfo(
                "Windows.Push.Channel",
                $"Channel acquired; expiresAt={nextChannel.ExpirationTime:O}.");
            return channelUri;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            CrashDiagnostics.LogException("Windows.Push.Channel", exception);
            return null;
        }
        finally
        {
            ChannelGate.Release();
        }
    }

    public static void CommitChannel(string channelUri)
    {
        if (!WnsChannelUriValidator.IsValid(channelUri))
        {
            return;
        }

        lock (Sync)
        {
            for (var index = OpenChannels.Count - 1; index >= 0; index--)
            {
                var candidate = OpenChannels[index];
                var candidateUri = candidate.Uri?.ToString();
                if (string.Equals(candidateUri, channelUri, StringComparison.Ordinal))
                {
                    continue;
                }

                TryClose(candidate);
                OpenChannels.RemoveAt(index);
            }
        }
    }

    public static void CloseChannels()
    {
        lock (Sync)
        {
            foreach (var openChannel in OpenChannels)
            {
                TryClose(openChannel);
            }

            OpenChannels.Clear();
            PushTokenBridge.Clear();
        }
    }

    public static async Task ProcessPushActivationAsync(
        PushNotificationReceivedEventArgs pushArgs,
        IServiceProvider? services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pushArgs);
        await ProcessPayloadAsync(pushArgs.Payload, services, cancellationToken).ConfigureAwait(false);
    }

    private static void InitializePushNotifications()
    {
        lock (Sync)
        {
            if (pushInitialized)
            {
                return;
            }

            try
            {
                if (!PushNotificationManager.IsSupported())
                {
                    pushInitialized = true;
                    CrashDiagnostics.LogInfo(
                        "Windows.Push",
                        "Windows App SDK push notifications are unavailable; polling remains enabled.");
                    return;
                }

                manager = WindowsRegistrationAttempt.TryRegister(
                    static () => PushNotificationManager.Default,
                    static manager =>
                    {
                        manager.PushReceived += OnPushReceived;
                    },
                    static manager =>
                    {
                        manager.Register();
                    },
                    static manager => manager.PushReceived -= OnPushReceived,
                    static manager => manager.Unregister(),
                    static exception => CrashDiagnostics.LogException("Windows.Push.Initialize", exception));
                pushInitialized = manager is not null;
            }
            catch (Exception exception)
            {
                manager = null;
                pushInitialized = false;
                CrashDiagnostics.LogException("Windows.Push.Initialize", exception);
            }
        }
    }

    private static void InitializeAppNotifications()
    {
        lock (Sync)
        {
            if (appNotificationsInitialized)
            {
                return;
            }

            try
            {
                if (!AppNotificationManager.IsSupported())
                {
                    appNotificationsInitialized = true;
                    CrashDiagnostics.LogInfo(
                        "Windows.AppNotifications",
                        "Windows App SDK app notifications are unavailable.");
                    return;
                }

                appNotificationManager = WindowsRegistrationAttempt.TryRegister(
                    static () => AppNotificationManager.Default,
                    static candidate => candidate.NotificationInvoked += OnNotificationInvoked,
                    static candidate => candidate.Register(),
                    static candidate => candidate.NotificationInvoked -= OnNotificationInvoked,
                    static candidate => candidate.Unregister(),
                    static exception => CrashDiagnostics.LogException(
                        "Windows.AppNotifications.Initialize",
                        exception));
                appNotificationsInitialized = appNotificationManager is not null;
            }
            catch (Exception exception)
            {
                appNotificationManager = null;
                appNotificationsInitialized = false;
                // A local toast failure must never disable raw WNS delivery.
                CrashDiagnostics.LogException("Windows.AppNotifications.Initialize", exception);
            }
        }
    }

    private static async void OnPushReceived(
        PushNotificationManager sender,
        PushNotificationReceivedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            await ProcessPayloadAsync(args.Payload, services: null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            CrashDiagnostics.LogException("Windows.Push.Received", exception);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static void OnNotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args)
    {
        BackgroundSyncBridge.PublishScheduledSync();
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView
                is Microsoft.UI.Xaml.Window window)
            {
                window.Activate();
            }
        });
    }

    private static async Task ProcessPayloadAsync(
        byte[] payload,
        IServiceProvider? services,
        CancellationToken cancellationToken)
    {
        await PayloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!WnsPushPayloadCodec.TryDecode(payload, out var data))
            {
                CrashDiagnostics.LogInfo("Windows.Push", "Rejected malformed WNS raw payload.");
                return;
            }

            var authentication = await PushNotificationBackgroundHandler
                .AuthenticateAsync(
                    data,
                    BackgroundSyncBridge.PublishScheduledSync,
                    cancellationToken)
                .ConfigureAwait(false);
            if (authentication.Status == PushAuthenticationStatus.Rejected ||
                authentication.Status == PushAuthenticationStatus.Replay && !BackgroundSyncBridge.HasPendingSync())
            {
                return;
            }

            var synchronized = await MauiBackgroundSyncRunner
                .TrySynchronizeAsync(services, cancellationToken)
                .ConfigureAwait(false);
            if (!synchronized)
            {
                return;
            }

            services ??= IPlatformApplication.Current?.Services ?? App.Services;
            var activeConversationTracker = services?.GetService<IActiveConversationTracker>()
                ?? throw new InvalidOperationException("The active conversation tracker is unavailable.");
            var coordinator = new IncomingMessageNotificationCoordinator(
                (limit, token) => MauiBackgroundSyncRunner
                    .ListPendingIncomingMessageNotificationIdsAsync(services, limit, token),
                (messageIds, token) => MauiBackgroundSyncRunner
                    .MarkIncomingMessageNotificationsPresentedAsync(services, messageIds, token),
                activeConversationTracker,
                (notificationId, _) =>
                {
                    ShowGenericNotification(notificationId);
                    return Task.CompletedTask;
                },
                BackgroundSyncBridge.PublishScheduledSync);
            await coordinator.PresentPendingAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            PayloadGate.Release();
        }
    }

    private static void ShowGenericNotification(string notificationId)
    {
        var notification = new AppNotificationBuilder()
            .AddText("Deep")
            .AddText("Новое сообщение")
            .BuildNotification();
        notification.Tag = notificationId[..Math.Min(notificationId.Length, 16)];
        notification.ExpiresOnReboot = true;
        AppNotificationManager.Default.Show(notification);
    }

    private static void TryClose(PushNotificationChannel candidate)
    {
        try
        {
            candidate.Close();
        }
        catch (Exception exception)
        {
            CrashDiagnostics.LogException("Windows.Push.CloseChannel", exception);
        }
    }
}
#endif

internal static class WindowsRegistrationAttempt
{
    public static T? TryRegister<T>(
        Func<T> candidateFactory,
        Action<T> attachHandler,
        Action<T> register,
        Action<T> detachHandler,
        Action<T> unregister,
        Action<Exception> reportFailure)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(candidateFactory);
        ArgumentNullException.ThrowIfNull(attachHandler);
        ArgumentNullException.ThrowIfNull(register);
        ArgumentNullException.ThrowIfNull(detachHandler);
        ArgumentNullException.ThrowIfNull(unregister);
        ArgumentNullException.ThrowIfNull(reportFailure);

        T? candidate = null;
        var attachAttempted = false;
        var registrationAttempted = false;
        try
        {
            candidate = candidateFactory()
                ?? throw new InvalidOperationException("The Windows notification manager is unavailable.");
            attachAttempted = true;
            attachHandler(candidate);
            registrationAttempted = true;
            register(candidate);
            return candidate;
        }
        catch (Exception exception)
        {
            ReportFailure(reportFailure, exception);
            if (candidate is not null && attachAttempted)
            {
                TryCleanup(() => detachHandler(candidate), reportFailure);
            }

            if (candidate is not null && registrationAttempted)
            {
                TryCleanup(() => unregister(candidate), reportFailure);
            }

            return null;
        }
    }

    private static void TryCleanup(Action cleanup, Action<Exception> reportFailure)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            ReportFailure(reportFailure, exception);
        }
    }

    private static void ReportFailure(Action<Exception> reportFailure, Exception exception)
    {
        try
        {
            reportFailure(exception);
        }
        catch (Exception)
        {
        }
    }
}
