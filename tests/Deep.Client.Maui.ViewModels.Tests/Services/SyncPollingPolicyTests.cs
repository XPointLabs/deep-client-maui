using Deep.Client.Maui.Services;
using Deep.Client.Shared.Platform;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class SyncPollingPolicyTests
{
    [Fact]
    public async Task WnsRegistration_KeepsPollingEnabled()
    {
        var runtime = ClientRuntime.CreateStubbed();
        var push = new FakePushNotificationService(new PushRegistration("wns-token", "wns", DateTimeOffset.UtcNow));
        var policy = new SyncPollingPolicy(push, runtime);

        Assert.False(await policy.IsPushDrivenSyncAvailableAsync());
        Assert.Equal(0, push.RemoveCalls);
    }

    [Fact]
    public async Task MismatchedSessionBinding_InvalidatesRemoteStateAndKeepsPollingEnabled()
    {
        var runtime = ClientRuntime.CreateStubbed();
        await runtime.Accounts.RegisterAsync("Alice");
        var registration = new PushRegistration("token-123", "fcm", DateTimeOffset.UtcNow);
        var push = new FakePushNotificationService(registration)
        {
            State = new PushNotificationKeyState(
                new string('a', 64),
                new string('b', 64),
                "05" + new string('c', 64),
                registration,
                RemoteSubscribed: true)
        };
        var policy = new SyncPollingPolicy(push, runtime);

        Assert.False(await policy.IsPushDrivenSyncAvailableAsync());
        Assert.Equal(1, push.RemoveCalls);
        Assert.Null(push.State);
    }

    [Fact]
    public async Task CurrentRemoteSubscription_DisablesPolling()
    {
        var runtime = ClientRuntime.CreateStubbed();
        var account = await runtime.Accounts.RegisterAsync("Alice");
        var registration = new PushRegistration("token-123", "fcm", DateTimeOffset.UtcNow);
        var push = new FakePushNotificationService(registration)
        {
            State = new PushNotificationKeyState(
                new string('a', 64),
                PushNotificationCrypto.ComputeSubscriptionBinding(account.SessionId.Value, "firebase", registration.Token),
                account.SessionId.Value,
                registration,
                RemoteSubscribed: true)
        };
        var policy = new SyncPollingPolicy(push, runtime);

        Assert.True(await policy.IsPushDrivenSyncAvailableAsync());
        Assert.Equal(0, push.RemoveCalls);
    }

    [Fact]
    public async Task AccountSwitchWithinCacheTtl_DoesNotReusePreviousSessionAvailability()
    {
        var runtime = ClientRuntime.CreateStubbed(
            clock: new FrozenClock(DateTimeOffset.Parse("2026-07-11T00:00:00Z")));
        var firstAccount = await runtime.Accounts.RegisterAsync("Alice");
        var registration = new PushRegistration("token-123", "fcm", DateTimeOffset.UtcNow);
        var push = new FakePushNotificationService(registration)
        {
            State = CurrentState(firstAccount.SessionId.Value, registration)
        };
        var policy = new SyncPollingPolicy(push, runtime);

        Assert.True(await policy.IsPushDrivenSyncAvailableAsync());

        await runtime.Accounts.SignOutAsync();
        var secondAccount = await runtime.Accounts.RegisterAsync("Bob");

        Assert.False(await policy.IsPushDrivenSyncAvailableAsync());
        Assert.Equal(1, push.RemoveCalls);

        push.State = CurrentState(secondAccount.SessionId.Value, registration);
        Assert.True(await policy.IsPushDrivenSyncAvailableAsync());
    }

    private static PushNotificationKeyState CurrentState(
        string sessionId,
        PushRegistration registration) =>
        new(
            new string('a', 64),
            PushNotificationCrypto.ComputeSubscriptionBinding(sessionId, "firebase", registration.Token),
            sessionId,
            registration,
            RemoteSubscribed: true);

    private sealed class FakePushNotificationService(PushRegistration registration)
        : IPushNotificationService, IPushNotificationEncryptionKeyStore
    {
        public PushNotificationKeyState? State { get; set; }
        public int RemoveCalls { get; private set; }

        public Task<PushRegistration?> RegisterAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<PushRegistration?>(registration);

        public Task<PushRegistration?> GetCachedRegistrationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<PushRegistration?>(State?.RemoteSubscribed == true ? State.Registration : registration);

        public Task UnregisterAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<PushNotificationKeyState?> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);

        public Task SetAsync(PushNotificationKeyState state, CancellationToken cancellationToken = default)
        {
            State = state;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(CancellationToken cancellationToken = default)
        {
            RemoveCalls++;
            State = null;
            return Task.CompletedTask;
        }
    }
}
