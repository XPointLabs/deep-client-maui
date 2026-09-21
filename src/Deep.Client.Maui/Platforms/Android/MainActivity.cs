#if ANDROID
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Microsoft.Maui;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui;

[Activity(
    Name = "network.xpoint.deep.MainActivity",
    Theme = "@style/Maui.SplashTheme",
    LaunchMode = LaunchMode.SingleTop,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.ScreenSize
        | ConfigChanges.Orientation
        | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize
        | ConfigChanges.Density)]
public sealed class MainActivity : MauiAppCompatActivity
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
                (int)(WindowInsetsControllerAppearance.LightStatusBars
                    | WindowInsetsControllerAppearance.LightNavigationBars));
        }

        if (Window?.DecorView is { } decorView)
        {
            var flags = decorView.SystemUiFlags & ~SystemUiFlags.LightStatusBar;
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                flags &= ~SystemUiFlags.LightNavigationBar;
            }

            decorView.SystemUiFlags = flags;
        }
#pragma warning restore CA1422

        ApplyPrivacyScreenSetting();
    }

    protected override void OnResume()
    {
        base.OnResume();
        ApplyPrivacyScreenSetting();
    }

    protected override void OnPause()
    {
        CurrentFocus?.ClearFocus();
        base.OnPause();
    }

    private void ApplyPrivacyScreenSetting()
    {
        if (Window is null)
        {
            return;
        }

#if DEBUG
        Window.ClearFlags(WindowManagerFlags.Secure);
#else
        var screenSecurityEnabled = Preferences.Default.Get(ClientSettingKeys.PrivacyScreenSecurity, true);
        if (screenSecurityEnabled)
        {
            Window.AddFlags(WindowManagerFlags.Secure);
        }
        else
        {
            Window.ClearFlags(WindowManagerFlags.Secure);
        }
#endif
    }
}
#endif
