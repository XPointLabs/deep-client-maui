using Microsoft.Maui.Controls;

namespace Deep.Client.Maui.Services;

internal static class AndroidSafeArea
{
    public static double GetStatusBarHeight()
    {
#if ANDROID
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        var insets = activity?.Window?.DecorView?.RootWindowInsets;
        var density = activity?.Resources?.DisplayMetrics?.Density ?? 1f;
        if (insets is null || density <= 0)
        {
            return 0;
        }

#pragma warning disable CA1422
        var pixels = OperatingSystem.IsAndroidVersionAtLeast(30)
            ? insets.GetInsets(Android.Views.WindowInsets.Type.StatusBars()).Top
            : insets.SystemWindowInsetTop;
#pragma warning restore CA1422
        return pixels / density;
#else
        return 0;
#endif
    }

    public static double GetTopOverlap(VisualElement element)
    {
#if ANDROID
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        var insets = activity?.Window?.DecorView?.RootWindowInsets;
        var density = activity?.Resources?.DisplayMetrics?.Density ?? 1f;
        if (insets is null || density <= 0 || element.Handler?.PlatformView is not Android.Views.View view)
        {
            return 0;
        }

#pragma warning disable CA1422
        var statusBarPixels = OperatingSystem.IsAndroidVersionAtLeast(30)
            ? insets.GetInsets(Android.Views.WindowInsets.Type.StatusBars()).Top
            : insets.SystemWindowInsetTop;
#pragma warning restore CA1422
        if (statusBarPixels <= 0)
        {
            return 0;
        }

        var location = new int[2];
        view.GetLocationOnScreen(location);
        return Math.Max(0, statusBarPixels - location[1]) / density;
#else
        return 0;
#endif
    }
}
