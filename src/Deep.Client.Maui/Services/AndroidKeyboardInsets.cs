using Microsoft.Maui.ApplicationModel;

#if ANDROID
using Android.Graphics;
using Android.Views;
using AndroidRect = Android.Graphics.Rect;
using AndroidView = Android.Views.View;
#endif

namespace Deep.Client.Maui.Services;

internal static class AndroidKeyboardInsets
{
    public static IDisposable Observe(Action<double> onChanged)
    {
#if ANDROID
        var decorView = Platform.CurrentActivity?.Window?.DecorView;
        return decorView is null
            ? NoopDisposable.Instance
            : new KeyboardInsetSubscription(decorView, onChanged);
#else
        return NoopDisposable.Instance;
#endif
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();

        public void Dispose()
        {
        }
    }

#if ANDROID
    private sealed class KeyboardInsetSubscription : Java.Lang.Object, ViewTreeObserver.IOnGlobalLayoutListener, IDisposable
    {
        private readonly AndroidView decorView;
        private readonly Action<double> onChanged;
        private double lastBottomInset = -1;

        public KeyboardInsetSubscription(AndroidView decorView, Action<double> onChanged)
        {
            this.decorView = decorView;
            this.onChanged = onChanged;
            decorView.ViewTreeObserver?.AddOnGlobalLayoutListener(this);
            OnGlobalLayout();
        }

        public void OnGlobalLayout()
        {
            var density = decorView.Context?.Resources?.DisplayMetrics?.Density ?? 1f;
            if (density <= 0)
            {
                density = 1f;
            }

            var bottomInset = GetKeyboardBottomInsetPixels(decorView) / density;
            if (bottomInset < 40)
            {
                bottomInset = 0;
            }

            if (Math.Abs(bottomInset - lastBottomInset) < 0.5)
            {
                return;
            }

            lastBottomInset = bottomInset;
            onChanged(bottomInset);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && decorView.ViewTreeObserver is { IsAlive: true } observer)
            {
                observer.RemoveOnGlobalLayoutListener(this);
            }

            base.Dispose(disposing);
        }

        private static int GetKeyboardBottomInsetPixels(AndroidView decorView)
        {
            if (decorView.RootWindowInsets is { } insets)
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(30))
                {
                    return insets.GetInsets(WindowInsets.Type.Ime()).Bottom;
                }
            }

            // On pre-Android 11 there is no reliable IME inset; use the visible
            // window frame and ignore small navigation-bar-only deltas.
            var visibleFrame = new AndroidRect();
            decorView.GetWindowVisibleDisplayFrame(visibleFrame);
            var rootHeight = decorView.RootView?.Height ?? decorView.Height;
            return Math.Max(0, rootHeight - visibleFrame.Bottom);
        }
    }
#endif
}
