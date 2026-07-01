#if ANDROID
using Firebase.Messaging;
using IOnFailureListener = Android.Gms.Tasks.IOnFailureListener;
using IOnSuccessListener = Android.Gms.Tasks.IOnSuccessListener;

namespace Deep.Client.Maui.Services;

internal static class FirebaseTokenProvider
{
    public static System.Threading.Tasks.Task<string?> GetTokenAsync(System.Threading.CancellationToken cancellationToken)
    {
        var cached = PushTokenBridge.Take("fcm");
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return System.Threading.Tasks.Task.FromResult<string?>(cached);
        }

        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = new TokenListener(completion);
        FirebaseMessaging.Instance.GetToken()
            .AddOnSuccessListener(listener)
            .AddOnFailureListener(listener);

        cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return completion.Task;
    }

    private sealed class TokenListener(TaskCompletionSource<string?> completion) : Java.Lang.Object, IOnSuccessListener, IOnFailureListener
    {
        public void OnSuccess(Java.Lang.Object? result)
        {
            var token = result?.ToString();
            if (!string.IsNullOrWhiteSpace(token))
            {
                PushTokenBridge.Set("fcm", token);
            }

            completion.TrySetResult(token);
        }

        public void OnFailure(Java.Lang.Exception exception) =>
            completion.TrySetException(new InvalidOperationException("Firebase не вернул токен устройства.", exception));
    }
}
#endif
