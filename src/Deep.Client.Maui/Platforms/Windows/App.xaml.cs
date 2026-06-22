#if WINDOWS
using Microsoft.Maui;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Platform;
using Microsoft.UI.Xaml;

namespace Deep.Client.Maui.WinUI;

public partial class App : MauiWinUIApplication
{
    public App()
    {
        InitializeComponent();
        UnhandledException += OnWinUiUnhandledException;
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);
        try
        {
            var launchArgs = args.Arguments ?? string.Empty;
            if (launchArgs.Contains("notification_action=", StringComparison.OrdinalIgnoreCase))
            {
                var values = ParseKeyValuePairs(launchArgs);
                if (values.TryGetValue("notification_action", out var action)
                    && values.TryGetValue("conversation_id", out var conversationId))
                {
                    NotificationActionBridge.Publish(new NotificationAction(
                        action,
                        conversationId,
                        values.TryGetValue("notification_id", out var notificationId) ? notificationId : Guid.NewGuid().ToString("N"),
                        DateTimeOffset.UtcNow));
                }
            }

            if (launchArgs.Contains("share_text=", StringComparison.OrdinalIgnoreCase))
            {
                var values = ParseKeyValuePairs(launchArgs);
                values.TryGetValue("share_text", out var text);
                MauiShareExtensionBridge.EnqueueAsync(new SharePayload(text, Array.Empty<string>())).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            CrashDiagnostics.LogException("Windows.App.OnLaunched", ex);
        }
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

    private static void OnWinUiUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        CrashDiagnostics.LogException(
            "WinUI.UnhandledException",
            args.Exception,
            $"Message={args.Message}; Handled={args.Handled}");
    }
}
#endif
