#if WINDOWS
using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Platform;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.PushNotifications;

namespace Deep.Client.Maui.WinUI;

public partial class App : MauiWinUIApplication
{
    private Microsoft.UI.Xaml.Window? appLockWindow;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnWinUiUnhandledException;
        Connectivity.ConnectivityChanged += OnConnectivityChanged;
        WindowsPushNotificationService.Initialize();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var activation = WindowsPushNotificationService.GetCurrentActivation();
        if (WindowsPushNotificationService.TryGetPushActivation(activation, out var pushArgs))
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            _ = ProcessHeadlessPushActivationAsync(pushArgs!, dispatcher);
            return;
        }

        base.OnLaunched(args);
        try
        {
            ApplyLaunchArguments(args.Arguments ?? string.Empty);
            AttachAppLockLifecycle();
            if (WindowsShareTargetService.TryGetShareActivation(activation, out var shareArgs))
            {
                _ = WindowsShareTargetService.ProcessAsync(shareArgs!);
            }
        }
        catch (Exception ex)
        {
            CrashDiagnostics.LogException("Windows.App.OnLaunched", ex);
        }
    }

    private async Task ProcessHeadlessPushActivationAsync(
        PushNotificationReceivedEventArgs pushArgs,
        DispatcherQueue dispatcher)
    {
        var deferral = pushArgs.GetDeferral();
        try
        {
            var mauiApp = CreateMauiApp();
            await WindowsPushNotificationService
                .ProcessPushActivationAsync(pushArgs, mauiApp.Services)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            CrashDiagnostics.LogException("Windows.Push.BackgroundActivation", exception);
        }
        finally
        {
            deferral.Complete();
            dispatcher.TryEnqueue(Exit);
        }
    }

    private static void ApplyLaunchArguments(string launchArgs)
    {
        if (launchArgs.Contains("notification_action=", StringComparison.OrdinalIgnoreCase))
        {
            var values = ParseKeyValuePairs(launchArgs);
            if (values.TryGetValue("notification_action", out var action) &&
                values.TryGetValue("conversation_id", out var conversationId))
            {
                NotificationActionBridge.Publish(new NotificationAction(
                    action,
                    conversationId,
                    values.TryGetValue("notification_id", out var notificationId)
                        ? notificationId
                        : Guid.NewGuid().ToString("N"),
                    DateTimeOffset.UtcNow));
            }
        }

        if (launchArgs.Contains("share_text=", StringComparison.OrdinalIgnoreCase))
        {
            var values = ParseKeyValuePairs(launchArgs);
            values.TryGetValue("share_text", out var text);
            MauiShareExtensionBridge.EnqueueInBackground(new SharePayload(text, Array.Empty<string>()));
        }
    }

    private void AttachAppLockLifecycle()
    {
        var mauiWindow = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
        if (mauiWindow is null)
        {
            return;
        }

        if (mauiWindow.Handler?.PlatformView is Microsoft.UI.Xaml.Window platformWindow)
        {
            AttachAppLockWindow(platformWindow);
            return;
        }

        mauiWindow.HandlerChanged += OnMauiWindowHandlerChanged;
    }

    private void OnMauiWindowHandlerChanged(object? sender, EventArgs args)
    {
        if (sender is not Microsoft.Maui.Controls.Window mauiWindow ||
            mauiWindow.Handler?.PlatformView is not Microsoft.UI.Xaml.Window platformWindow)
        {
            return;
        }

        mauiWindow.HandlerChanged -= OnMauiWindowHandlerChanged;
        AttachAppLockWindow(platformWindow);
    }

    private void AttachAppLockWindow(Microsoft.UI.Xaml.Window platformWindow)
    {
        if (ReferenceEquals(appLockWindow, platformWindow))
        {
            return;
        }

        if (appLockWindow is not null)
        {
            appLockWindow.Activated -= OnWindowActivated;
        }

        appLockWindow = platformWindow;
        appLockWindow.Activated += OnWindowActivated;
        SetApplicationForeground(true);
        Deep.Client.Maui.App.Services?.GetService<IPrivacyScreenService>()?.ApplyFromPreferences();
        _ = RunForegroundMaintenanceAsync();
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        var appLock = Deep.Client.Maui.App.Services?.GetService<IAppLockService>();
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            SetApplicationForeground(false);
            appLock?.MarkAppHidden();
            return;
        }

        SetApplicationForeground(true);
        _ = RunForegroundMaintenanceAsync();
    }

    private static void SetApplicationForeground(bool isForeground) =>
        Deep.Client.Maui.App.Services?
            .GetService<IActiveConversationTracker>()?
            .SetApplicationForeground(isForeground);

    private static void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs args)
    {
        // Network activation remains closed until a Deep-account messaging runtime exists.
    }

    private static async Task RunForegroundMaintenanceAsync()
    {
        try
        {
            var services = Deep.Client.Maui.App.Services;
            var appLock = services?.GetService<IAppLockService>();
            if (appLock is not null && !await appLock.AuthenticateIfRequiredAsync().ConfigureAwait(false))
            {
                return;
            }

            // Do not compose Reality, push, DNS or certificate state from foreground startup.
        }
        catch (Exception exception)
        {
            CrashDiagnostics.LogException("Windows.ForegroundMaintenance", exception);
        }
    }

    private static async Task RunPushMaintenanceAsync()
    {
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static Dictionary<string, string> ParseKeyValuePairs(string args)
    {
        return args.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0]),
                parts => Uri.UnescapeDataString(parts[1]),
                StringComparer.OrdinalIgnoreCase);
    }

    private static void OnWinUiUnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        CrashDiagnostics.LogException(
            "WinUI.UnhandledException",
            args.Exception,
            $"Message={args.Message}; Handled={args.Handled}");
    }
}
#endif
