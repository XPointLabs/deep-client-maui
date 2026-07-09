#if ANDROID
using Android.App;
using Android.Content.PM;
using Android.Content;
using Android.OS;
using Android.Views;
using Microsoft.Maui;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Platform;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui;

[Activity(Name = "network.xpoint.deep.MainActivity", Theme = "@style/Maui.SplashTheme", LaunchMode = LaunchMode.SingleTop, WindowSoftInputMode = SoftInput.AdjustResize, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
#pragma warning disable CA1422
        Window?.SetSoftInputMode(SoftInput.AdjustResize);
        Window?.SetStatusBarColor(Android.Graphics.Color.Black);
        Window?.SetNavigationBarColor(Android.Graphics.Color.Black);
        if (OperatingSystem.IsAndroidVersionAtLeast(30) && Window?.InsetsController is { } controller)
        {
            controller.SetSystemBarsAppearance(
                0,
                (int)(WindowInsetsControllerAppearance.LightStatusBars | WindowInsetsControllerAppearance.LightNavigationBars));
        }

        if (Window?.DecorView is { } decorView)
        {
            var flags = decorView.SystemUiFlags;
            flags &= ~SystemUiFlags.LightStatusBar;
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                flags &= ~SystemUiFlags.LightNavigationBar;
            }

            decorView.SystemUiFlags = flags;
        }

        ApplyPrivacyScreenSetting();
#pragma warning restore CA1422
    }

    public override bool DispatchTouchEvent(MotionEvent? ev)
    {
        AndroidVoiceGestureRouter.Dispatch(ev);
        return base.DispatchTouchEvent(ev);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleIntent(intent);
    }

    protected override void OnResume()
    {
        base.OnResume();
        ApplyPrivacyScreenSetting();
        HandleIntent(Intent);
    }

    private void ApplyPrivacyScreenSetting()
    {
        if (Window is null)
        {
            return;
        }

#if DEBUG
        var screenSecurityEnabled = false;
#else
        var screenSecurityEnabled = Preferences.Default.Get(ClientSettingKeys.PrivacyScreenSecurity, true);
#endif
        if (screenSecurityEnabled)
        {
            Window.AddFlags(WindowManagerFlags.Secure);
        }
        else
        {
            Window.ClearFlags(WindowManagerFlags.Secure);
        }
    }

    private static void HandleIntent(Intent? intent)
    {
        if (intent is null)
        {
            return;
        }

        var action = intent.Action;
        if (string.Equals(action, Intent.ActionSend, StringComparison.Ordinal))
        {
            var text = intent.GetStringExtra(Intent.ExtraText);
            var stream = GetShareStream(intent);
            var files = string.IsNullOrWhiteSpace(stream) ? Array.Empty<string>() : new[] { stream };

            MauiShareExtensionBridge.EnqueueAsync(new SharePayload(text, files)).GetAwaiter().GetResult();
        }

        var notificationAction = intent.GetStringExtra("notification_action");
        var conversationId = intent.GetStringExtra("conversation_id");
        var notificationId = intent.GetStringExtra("notification_id");

        if (!string.IsNullOrWhiteSpace(notificationAction) && !string.IsNullOrWhiteSpace(conversationId))
        {
            NotificationActionBridge.Publish(new NotificationAction(
                notificationAction,
                conversationId,
                notificationId ?? Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow));
        }
    }

    private static string? GetShareStream(Intent intent)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            return intent.GetParcelableExtra(
                Intent.ExtraStream,
                Java.Lang.Class.FromType(typeof(Android.Net.Uri)))?.ToString();
        }

#pragma warning disable CA1422
        return intent.GetParcelableExtra(Intent.ExtraStream)?.ToString();
#pragma warning restore CA1422
    }
}
#endif
