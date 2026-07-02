using Microsoft.Maui.Controls;

#if WINDOWS
using Microsoft.Maui.Platform;
using Microsoft.Web.WebView2.Core;
#endif

#if ANDROID
using Android.Webkit;
#endif

namespace Deep.Client.Maui.Services;

public static class CallWebViewPlatform
{
    public static void Configure(HybridWebView view)
    {
#if ANDROID
        if (view.Handler?.PlatformView is Android.Webkit.WebView webView)
        {
            webView.Settings.MediaPlaybackRequiresUserGesture = false;
            webView.SetWebChromeClient(new CallWebChromeClient());
        }
#elif WINDOWS
        if (view.Handler?.PlatformView is MauiHybridWebView webView)
        {
            webView.RunAfterInitialize(() =>
            {
                webView.CoreWebView2.PermissionRequested -= OnPermissionRequested;
                webView.CoreWebView2.PermissionRequested += OnPermissionRequested;
            });
        }
#endif
    }

#if WINDOWS
    private static void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs args)
    {
        if (args.Uri.StartsWith("https://0.0.0.1/", StringComparison.OrdinalIgnoreCase)
            && args.PermissionKind is CoreWebView2PermissionKind.Camera or CoreWebView2PermissionKind.Microphone)
        {
            args.State = CoreWebView2PermissionState.Allow;
        }
    }
#endif

#if ANDROID
    private sealed class CallWebChromeClient : WebChromeClient
    {
        public override bool OnConsoleMessage(ConsoleMessage? consoleMessage)
        {
            if (consoleMessage is not null)
            {
                Android.Util.Log.Debug("DeepCallWeb", $"{consoleMessage.Message()} ({consoleMessage.LineNumber()})");
            }

            return base.OnConsoleMessage(consoleMessage);
        }

        public override void OnPermissionRequest(PermissionRequest? request)
        {
            if (request?.Origin?.Host == "0.0.0.1")
            {
                request.Grant(request.GetResources());
                return;
            }

            request?.Deny();
        }
    }
#endif
}
