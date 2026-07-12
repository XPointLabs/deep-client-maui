#if WINDOWS
using Microsoft.Maui.Storage;
using Windows.Security.Credentials.UI;

namespace Deep.Client.Maui.Services;

public sealed class WindowsAppLockService : IAppLockService
{
    private readonly SemaphoreSlim authenticationGate = new(1, 1);
    private readonly object stateGate = new();
    private UserConsentVerifierAvailability? lastAvailability;
    private bool authenticationInProgress;
    private bool unlockedForForeground;

    // IUserConsentVerifierInterop is available to desktop apps starting with Windows 11.
    public bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    public bool IsDeviceSecure =>
        IsSupported && GetLastAvailability() == UserConsentVerifierAvailability.Available;

    public bool IsEnabled => Preferences.Default.Get(ClientSettingKeys.PrivacyAppLock, false);

    public string? UnavailableReason => IsSupported
        ? DescribeAvailability(GetLastAvailability())
        : "Эта версия Windows не поддерживает безопасный диалог Windows Hello для настольных приложений.";

    public void SetEnabled(bool enabled)
    {
        if (enabled && !unlockedForForeground)
        {
            throw new InvalidOperationException(
                UnavailableReason ?? "Подтвердите личность через Windows Hello перед включением блокировки Deep.");
        }

        Preferences.Default.Set(ClientSettingKeys.PrivacyAppLock, enabled);
        if (!enabled)
        {
            unlockedForForeground = true;
            SetContentHidden(false);
        }
    }

    public void MarkAppHidden()
    {
        if (authenticationInProgress || !IsEnabled)
        {
            return;
        }

        unlockedForForeground = false;
        SetContentHidden(true);
    }

    public async Task<bool> AuthenticateIfRequiredAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            unlockedForForeground = true;
            SetContentHidden(false);
            return true;
        }

        if (unlockedForForeground)
        {
            SetContentHidden(false);
            return true;
        }

        // Once the user enabled the lock, every non-verified outcome is fail-closed.
        SetContentHidden(true);
        return await AuthenticateAsync(failClosed: true, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> AuthenticateNowAsync(CancellationToken cancellationToken = default) =>
        AuthenticateAsync(failClosed: IsEnabled, cancellationToken);

    private async Task<bool> AuthenticateAsync(bool failClosed, CancellationToken cancellationToken)
    {
        await authenticationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            authenticationInProgress = true;
            SetContentHidden(true);

            if (!IsSupported)
            {
                unlockedForForeground = false;
                SetContentHidden(failClosed);
                return false;
            }

            var availability = await CheckAvailabilityAsync(cancellationToken).ConfigureAwait(false);
            SetLastAvailability(availability);
            if (availability != UserConsentVerifierAvailability.Available)
            {
                unlockedForForeground = false;
                SetContentHidden(failClosed);
                return false;
            }

            var result = await RequestVerificationForWindowAsync(cancellationToken).ConfigureAwait(false);
            var authenticated = result == UserConsentVerificationResult.Verified;
            unlockedForForeground = authenticated;
            SetContentHidden(failClosed && !authenticated);
            return authenticated;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            unlockedForForeground = false;
            SetContentHidden(failClosed);
            throw;
        }
        catch (Exception exception)
        {
            unlockedForForeground = false;
            SetContentHidden(failClosed);
            CrashDiagnostics.LogException("Windows.AppLock.Authentication", exception);
            return false;
        }
        finally
        {
            authenticationInProgress = false;
            authenticationGate.Release();
        }
    }

    private static async Task<UserConsentVerificationResult> RequestVerificationForWindowAsync(
        CancellationToken cancellationToken)
    {
        return await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var window = Microsoft.Maui.Controls.Application.Current?
                .Windows
                .FirstOrDefault()?
                .Handler?
                .PlatformView as Microsoft.UI.Xaml.Window
                ?? throw new InvalidOperationException("The WinUI app window is unavailable.");
            var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (windowHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("The WinUI app window does not have an HWND.");
            }

            return await UserConsentVerifierInterop
                .RequestVerificationForWindowAsync(
                    windowHandle,
                    "Подтвердите личность, чтобы открыть сообщения Deep.")
                .AsTask(cancellationToken)
                .ConfigureAwait(true);
        }).ConfigureAwait(false);
    }

    private static async Task<UserConsentVerifierAvailability> CheckAvailabilityAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await UserConsentVerifier
                .CheckAvailabilityAsync()
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return UserConsentVerifierAvailability.DeviceBusy;
        }
    }

    private UserConsentVerifierAvailability? GetLastAvailability()
    {
        lock (stateGate)
        {
            return lastAvailability;
        }
    }

    private void SetLastAvailability(UserConsentVerifierAvailability availability)
    {
        lock (stateGate)
        {
            lastAvailability = availability;
        }
    }

    private static string? DescribeAvailability(UserConsentVerifierAvailability? availability) => availability switch
    {
        UserConsentVerifierAvailability.Available => null,
        UserConsentVerifierAvailability.NotConfiguredForUser =>
            "Сначала настройте Windows Hello, PIN-код или биометрию в параметрах Windows.",
        UserConsentVerifierAvailability.DisabledByPolicy =>
            "Windows Hello отключён политикой безопасности этого компьютера.",
        UserConsentVerifierAvailability.DeviceNotPresent =>
            "На этом компьютере недоступна проверка Windows Hello.",
        UserConsentVerifierAvailability.DeviceBusy =>
            "Windows Hello временно занят. Повторите попытку.",
        null => "Доступность Windows Hello будет проверена при включении блокировки.",
        _ => "Не удалось проверить доступность Windows Hello."
    };

    private static void SetContentHidden(bool hidden)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page is not null)
            {
                page.Opacity = hidden ? 0 : 1;
                page.InputTransparent = hidden;
            }
        });
    }
}
#endif
