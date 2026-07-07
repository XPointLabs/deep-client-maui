#if ANDROID
using Android.Views;

namespace Deep.Client.Maui.Services;

public sealed record AndroidVoiceGestureEventArgs(MotionEventActions Action, float RawX, float RawY);

public static class AndroidVoiceGestureRouter
{
    public static event EventHandler<AndroidVoiceGestureEventArgs>? Touch;

    public static void Dispatch(MotionEvent? motion)
    {
        if (motion is null || Touch is null)
        {
            return;
        }

        Touch.Invoke(
            null,
            new AndroidVoiceGestureEventArgs(motion.ActionMasked, motion.RawX, motion.RawY));
    }
}
#endif
