using Microsoft.Maui.Controls;

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
            webView.SetWebChromeClient(new CallWebChromeClient());
        }
#endif
    }

#if ANDROID
    private sealed class CallWebChromeClient : WebChromeClient
    {
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
