#if ANDROID
using Android.Content;
using Android.Views.InputMethods;
#endif
using Microsoft.Maui.ApplicationModel;

namespace Deep.Client.Maui.Services;

internal static class KeyboardDismissal
{
    public static void Dismiss(VisualElement? focusedElement = null)
    {
        focusedElement?.Unfocus();

#if ANDROID
        var activity = Platform.CurrentActivity;
        var view = activity?.CurrentFocus ?? activity?.Window?.DecorView;
        var manager = activity?.GetSystemService(Context.InputMethodService) as InputMethodManager;
        if (view?.WindowToken is not null)
        {
            manager?.HideSoftInputFromWindow(view.WindowToken, HideSoftInputFlags.None);
        }
#endif
    }
}
