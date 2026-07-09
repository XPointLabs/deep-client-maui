using Microsoft.Maui.Storage;

namespace Deep.Client.Maui.Services;

public interface IAppLockService
{
    bool IsSupported { get; }

    bool IsDeviceSecure { get; }

    bool IsEnabled { get; }

    string? UnavailableReason { get; }

    void SetEnabled(bool enabled);

    void MarkAppHidden();

    Task<bool> AuthenticateIfRequiredAsync(CancellationToken cancellationToken = default);

    Task<bool> AuthenticateNowAsync(CancellationToken cancellationToken = default);
}

public sealed class AppLockService : IAppLockService
{
    public bool IsSupported => false;

    public bool IsDeviceSecure => false;

    public bool IsEnabled => false;

    public string? UnavailableReason => "Блокировка приложения поддерживается только на Android.";

    public void SetEnabled(bool enabled)
    {
        Preferences.Default.Set(ClientSettingKeys.PrivacyAppLock, false);
    }

    public void MarkAppHidden()
    {
    }

    public Task<bool> AuthenticateIfRequiredAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public Task<bool> AuthenticateNowAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
