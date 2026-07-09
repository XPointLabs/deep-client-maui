#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using AndroidX.Biometric;
using AndroidX.Core.Content;
using AndroidX.Fragment.App;
using Java.Lang;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui.Services;

public sealed class AndroidAppLockService : IAppLockService
{
    private const int DeviceCredentialRequestCode = 47091;

    private readonly SemaphoreSlim authenticationGate = new(1, 1);
    private TaskCompletionSource<bool>? credentialResultSource;
    private BiometricPrompt? activePrompt;
    private bool authenticationInProgress;
    private bool unlockedForForeground;

    public bool IsSupported => true;

    public bool IsDeviceSecure => GetKeyguardManager()?.IsKeyguardSecure == true;

    public bool IsEnabled => Preferences.Default.Get(ClientSettingKeys.PrivacyAppLock, false) && IsDeviceSecure;

    public string? UnavailableReason => IsDeviceSecure
        ? null
        : "Сначала включите PIN-код, пароль, графический ключ или биометрию в настройках Android.";

    public void SetEnabled(bool enabled)
    {
        if (enabled && !IsDeviceSecure)
        {
            Preferences.Default.Set(ClientSettingKeys.PrivacyAppLock, false);
            throw new InvalidOperationException(UnavailableReason);
        }

        Preferences.Default.Set(ClientSettingKeys.PrivacyAppLock, enabled);
        unlockedForForeground = enabled;
        SetContentHidden(false);
    }

    public void MarkAppHidden()
    {
        if (authenticationInProgress || !Preferences.Default.Get(ClientSettingKeys.PrivacyAppLock, false))
        {
            return;
        }

        unlockedForForeground = false;
        SetContentHidden(true);
    }

    public async Task<bool> AuthenticateIfRequiredAsync(CancellationToken cancellationToken = default)
    {
        if (!Preferences.Default.Get(ClientSettingKeys.PrivacyAppLock, false))
        {
            unlockedForForeground = true;
            SetContentHidden(false);
            return true;
        }

        if (!IsDeviceSecure)
        {
            Preferences.Default.Set(ClientSettingKeys.PrivacyAppLock, false);
            unlockedForForeground = true;
            SetContentHidden(false);
            return true;
        }

        if (unlockedForForeground)
        {
            SetContentHidden(false);
            return true;
        }

        return await AuthenticateAsync(sendToBackgroundOnFailure: true, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> AuthenticateNowAsync(CancellationToken cancellationToken = default) =>
        AuthenticateAsync(sendToBackgroundOnFailure: false, cancellationToken);

    public bool HandleActivityResult(int requestCode, Result resultCode)
    {
        if (requestCode != DeviceCredentialRequestCode)
        {
            return false;
        }

        var resultSource = credentialResultSource;
        credentialResultSource = null;
        resultSource?.TrySetResult(resultCode == Result.Ok);
        return true;
    }

    private async Task<bool> AuthenticateAsync(bool sendToBackgroundOnFailure, CancellationToken cancellationToken)
    {
        if (!IsDeviceSecure)
        {
            return false;
        }

        await authenticationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            authenticationInProgress = true;
            SetContentHidden(true);

            var authenticated = await MainThread.InvokeOnMainThreadAsync(
                () => StartAuthenticationOnMainThreadAsync(cancellationToken)).ConfigureAwait(false);

            unlockedForForeground = authenticated;
            SetContentHidden(!authenticated);

            if (!authenticated && sendToBackgroundOnFailure)
            {
                MainThread.BeginInvokeOnMainThread(() => Platform.CurrentActivity?.MoveTaskToBack(true));
            }

            return authenticated;
        }
        finally
        {
            authenticationInProgress = false;
            activePrompt = null;
            authenticationGate.Release();
        }
    }

    private Task<bool> StartAuthenticationOnMainThreadAsync(CancellationToken cancellationToken)
    {
        if (Platform.CurrentActivity is not FragmentActivity activity)
        {
            return Task.FromResult(false);
        }

        if (TryStartBiometricPrompt(activity, cancellationToken, out var biometricTask))
        {
            return biometricTask;
        }

        return StartDeviceCredentialConfirmationAsync(activity, cancellationToken);
    }

    private bool TryStartBiometricPrompt(
        FragmentActivity activity,
        CancellationToken cancellationToken,
        out Task<bool> authenticationTask)
    {
        authenticationTask = Task.FromResult(false);

        var biometricManager = BiometricManager.From(activity);
        var strongBiometric = BiometricManager.Authenticators.BiometricStrong;
        var deviceCredential = BiometricManager.Authenticators.DeviceCredential;

        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            var allowedAuthenticators = strongBiometric | deviceCredential;
            if (biometricManager.CanAuthenticate(allowedAuthenticators) != BiometricManager.BiometricSuccess)
            {
                return false;
            }

            authenticationTask = StartBiometricPromptAsync(
                activity,
                allowedAuthenticators,
                fallbackToCredential: null,
                cancellationToken);
            return true;
        }

        if (biometricManager.CanAuthenticate(strongBiometric) != BiometricManager.BiometricSuccess)
        {
            return false;
        }

        authenticationTask = StartBiometricPromptAsync(
            activity,
            allowedAuthenticators: strongBiometric,
            fallbackToCredential: () => StartDeviceCredentialConfirmationAsync(activity, cancellationToken),
            cancellationToken);
        return true;
    }

    private async Task<bool> StartBiometricPromptAsync(
        FragmentActivity activity,
        int allowedAuthenticators,
        Func<Task<bool>>? fallbackToCredential,
        CancellationToken cancellationToken)
    {
        var resultSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callback = new AppLockAuthenticationCallback(resultSource, fallbackToCredential);
        var executor = ContextCompat.GetMainExecutor(activity);
        if (executor is null)
        {
            return false;
        }

        var prompt = new BiometricPrompt(activity, executor, callback);
        var promptBuilder = new BiometricPrompt.PromptInfo.Builder()
            .SetTitle("Разблокировать Deep")
            .SetSubtitle("Подтвердите личность, чтобы открыть сообщения.");

        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            promptBuilder.SetAllowedAuthenticators(allowedAuthenticators);
        }
        else
        {
            promptBuilder.SetNegativeButtonText("PIN-код");
        }

        activePrompt = prompt;
        using var registration = cancellationToken.Register(() =>
        {
            prompt.CancelAuthentication();
            resultSource.TrySetCanceled(cancellationToken);
        });

        prompt.Authenticate(promptBuilder.Build());
        return await resultSource.Task.ConfigureAwait(false);
    }

    private async Task<bool> StartDeviceCredentialConfirmationAsync(
        Activity activity,
        CancellationToken cancellationToken)
    {
        var keyguardManager = GetKeyguardManager();
#pragma warning disable CA1422
        var intent = keyguardManager?.CreateConfirmDeviceCredentialIntent(
            "Разблокировать Deep",
            "Введите PIN-код, пароль или графический ключ устройства.");
#pragma warning restore CA1422
        if (intent is null)
        {
            return false;
        }

        var resultSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        credentialResultSource = resultSource;

        using var registration = cancellationToken.Register(() =>
        {
            credentialResultSource = null;
            resultSource.TrySetCanceled(cancellationToken);
        });

#pragma warning disable CA1422
        activity.StartActivityForResult(intent, DeviceCredentialRequestCode);
#pragma warning restore CA1422
        return await resultSource.Task.ConfigureAwait(false);
    }

    private static KeyguardManager? GetKeyguardManager()
    {
        var context = Platform.AppContext ?? Android.App.Application.Context;
        return context.GetSystemService(Context.KeyguardService) as KeyguardManager;
    }

    private static void SetContentHidden(bool hidden)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (Platform.CurrentActivity?.Window?.DecorView is { } decorView)
            {
                decorView.Alpha = hidden ? 0f : 1f;
                decorView.Visibility = hidden ? ViewStates.Invisible : ViewStates.Visible;
            }
        });
    }

    private sealed class AppLockAuthenticationCallback : BiometricPrompt.AuthenticationCallback
    {
        private readonly TaskCompletionSource<bool> resultSource;
        private readonly Func<Task<bool>>? fallbackToCredential;

        public AppLockAuthenticationCallback(
            TaskCompletionSource<bool> resultSource,
            Func<Task<bool>>? fallbackToCredential)
        {
            this.resultSource = resultSource;
            this.fallbackToCredential = fallbackToCredential;
        }

        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult result)
        {
            resultSource.TrySetResult(true);
        }

        public override void OnAuthenticationError(int errorCode, ICharSequence errString)
        {
            if (errorCode == BiometricPrompt.ErrorNegativeButton && fallbackToCredential is not null)
            {
                _ = CompleteFromFallbackAsync();
                return;
            }

            resultSource.TrySetResult(false);
        }

        public override void OnAuthenticationFailed()
        {
        }

        private async Task CompleteFromFallbackAsync()
        {
            try
            {
                resultSource.TrySetResult(await fallbackToCredential!().ConfigureAwait(false));
            }
            catch
            {
                resultSource.TrySetResult(false);
            }
        }
    }
}
#endif
