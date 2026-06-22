using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Platform;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.DeviceTests;

public sealed class DeviceIntegrationSmokeTests
{
    [Fact]
    public async Task DeviceTargetCanCreateSessionAndRegisterPushBoundary()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var onboarding = new OnboardingViewModel(runtime) { DisplayName = "Device" };
        var notifications = new NotificationRegistrationViewModel(new PushRegistrationCoordinator(
            runtime,
            new DevicePushProbe(),
            new DisabledPushSubscriptionTransport(),
            runtime.Clock));

        await onboarding.RegisterAsync();
        await notifications.RegisterAsync();

        Assert.True(onboarding.IsLoggedIn);
        Assert.NotNull(notifications.Token);
    }

    private sealed class DevicePushProbe : IPushNotificationService
    {
        private PushRegistration? cachedRegistration;

        public Task<PushRegistration?> RegisterAsync(CancellationToken cancellationToken = default)
        {
#if ANDROID
            const string provider = "android-device-probe";
#elif IOS
            const string provider = "ios-device-probe";
#else
            const string provider = "device-probe";
#endif
            cachedRegistration = new PushRegistration("device-token", provider, DateTimeOffset.UtcNow);
            return Task.FromResult<PushRegistration?>(cachedRegistration);
        }

        public Task<PushRegistration?> GetCachedRegistrationAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(cachedRegistration);
        }

        public Task UnregisterAsync(CancellationToken cancellationToken = default)
        {
            cachedRegistration = null;
            return Task.CompletedTask;
        }
    }
}
