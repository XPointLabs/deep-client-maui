#if ANDROID
using Android.App;
using Android.Content.PM;
using Android.Content;
using Android.OS;
using Android.Views;
using Microsoft.Maui;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui;

[Activity(Name = "network.xpoint.deep.MainActivity", Theme = "@style/Maui.SplashTheme", LaunchMode = LaunchMode.SingleTop, WindowSoftInputMode = SoftInput.AdjustResize, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    internal const string TrustedNotificationAction = "network.xpoint.deep.action.OPEN_NOTIFICATION";

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
        _ = ResumeAfterUnlockAsync(intent);
    }

    protected override void OnResume()
    {
        base.OnResume();
        ResolveActiveConversationTracker()?.SetApplicationForeground(true);
        ApplyPrivacyScreenSetting();
        _ = ResumeAfterUnlockAsync(Intent);
    }

    protected override void OnStop()
    {
        ResolveAppLock()?.MarkAppHidden();
        base.OnStop();
    }

    protected override void OnPause()
    {
        App.Services?.GetService<IRealityTransportRuntime>()?.SetForeground(false);
        ResolveActiveConversationTracker()?.SetApplicationForeground(false);
        base.OnPause();
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        if (ResolveAppLock() is AndroidAppLockService appLock &&
            appLock.HandleActivityResult(requestCode, resultCode))
        {
            return;
        }

        base.OnActivityResult(requestCode, resultCode, data);
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
        if (intent is null
            || !string.Equals(intent.Action, TrustedNotificationAction, StringComparison.Ordinal)
            || !string.Equals(intent.Component?.ClassName, "network.xpoint.deep.MainActivity", StringComparison.Ordinal))
        {
            return;
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

    private static async Task ResumeAfterUnlockAsync(Intent? intent)
    {
        var appLock = ResolveAppLock();
        if (appLock is not null && !await appLock.AuthenticateIfRequiredAsync().ConfigureAwait(false))
        {
            return;
        }

        HandleIntent(intent);
        var realityTransport = App.Services?.GetService<IRealityTransportRuntime>();
        if (realityTransport is not null)
        {
            try
            {
                await realityTransport.OnForegroundAsync().ConfigureAwait(false);
            }
            catch (System.OperationCanceledException)
            {
                // App shutdown owns cancellation; no retry loop is started from the Activity.
            }
            catch (Exception exception)
            {
                CrashDiagnostics.LogException("Android.RealityTransportForeground", exception);
            }
        }
    }

    private static IAppLockService? ResolveAppLock() =>
        App.Services?.GetService<IAppLockService>();

    private static IActiveConversationTracker? ResolveActiveConversationTracker() =>
        App.Services?.GetService<IActiveConversationTracker>();
}
#endif
