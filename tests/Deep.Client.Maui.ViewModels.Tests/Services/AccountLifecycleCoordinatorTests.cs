using Deep.Client.Maui.Services;
using Deep.Client.Shared.Platform;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class AccountLifecycleCoordinatorTests
{
    [Fact]
    public async Task PushRegistration_IsCoalescedPerAccountAndSurvivesPageCancellation()
    {
        var runtime = ClientRuntime.CreateStubbed();
        await runtime.Accounts.RegisterAsync("Alice");
        var registration = new PushRegistration("token", "fcm", DateTimeOffset.UtcNow);
        var remote = new BlockingRegistrationCoordinator(registration);
        var lifecycle = new PushRegistrationLifecycleCoordinator(
            runtime,
            remote,
            new SyncPollingPolicy(new NullPushNotificationService(registration), runtime));
        using var firstCaller = new CancellationTokenSource();

        var first = lifecycle.EnsureRegisteredAsync(firstCaller.Token);
        var second = lifecycle.EnsureRegisteredAsync();
        await remote.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        firstCaller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first);
        Assert.False(second.IsCompleted);

        remote.Release();
        Assert.Equal(registration, await second);
        Assert.Equal(1, remote.RegisterCalls);
    }

    [Fact]
    public async Task PushRegistration_AccountSwitchStartsANewAccountScopedAttempt()
    {
        var runtime = ClientRuntime.CreateStubbed();
        await runtime.Accounts.RegisterAsync("Alice");
        var registration = new PushRegistration("token", "fcm", DateTimeOffset.UtcNow);
        var remote = new ImmediateRegistrationCoordinator(registration);
        var lifecycle = new PushRegistrationLifecycleCoordinator(
            runtime,
            remote,
            new SyncPollingPolicy(new NullPushNotificationService(registration), runtime));

        await lifecycle.EnsureRegisteredAsync();
        await runtime.Accounts.SignOutAsync();
        await runtime.Accounts.RegisterAsync("Bob");
        await lifecycle.EnsureRegisteredAsync();

        Assert.Equal(2, remote.RegisterCalls);
    }

    [Fact]
    public async Task BackgroundRetrySchedule_IsIdempotentPerAccountAndResetsOnLogout()
    {
        var runtime = ClientRuntime.CreateStubbed();
        await runtime.Accounts.RegisterAsync("Alice");
        var scheduler = new RecordingRegularScheduler();
        var coordinator = new BackgroundSyncSchedulingCoordinator(runtime, scheduler);

        await coordinator.EnsureScheduledForActiveAccountAsync();
        await coordinator.EnsureScheduledForActiveAccountAsync();
        Assert.Equal(1, scheduler.EnsureCalls);

        await runtime.Accounts.SignOutAsync();
        await runtime.Accounts.RegisterAsync("Bob");
        await coordinator.EnsureScheduledForActiveAccountAsync();
        Assert.Equal(2, scheduler.EnsureCalls);

        await coordinator.ResetAsync();
        Assert.Equal(1, scheduler.CancelCalls);
        await coordinator.EnsureScheduledForActiveAccountAsync();
        Assert.Equal(3, scheduler.EnsureCalls);
    }

    [Fact]
    public async Task ColdPushUnsubscribeRetry_BootstrapsRuntimeBeforeResolvingCoordinator()
    {
        await using var bootstrapper = new ClientRuntimeBootstrapper(
            _ => Task.FromResult(ClientRuntime.CreateStubbed()));
        var retry = new RecordingRetryCoordinator(result: true);
        var services = new BootstrapAwareServiceProvider(bootstrapper, retry);

        var completed = await PushUnsubscribeRetryBootstrapper.TryRetryAsync(services);

        Assert.True(completed);
        Assert.True(services.CoordinatorResolvedAfterBootstrap);
        Assert.Equal(1, retry.RetryCalls);
    }

    [Fact]
    public async Task ColdPushUnsubscribeRetry_BootstrapFailureDoesNotResolveCoordinator()
    {
        await using var bootstrapper = new ClientRuntimeBootstrapper(
            _ => Task.FromException<ClientRuntime>(new InvalidOperationException("storage unavailable")));
        var retry = new RecordingRetryCoordinator(result: true);
        var services = new BootstrapAwareServiceProvider(bootstrapper, retry);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => PushUnsubscribeRetryBootstrapper.TryRetryAsync(services));

        Assert.False(services.CoordinatorResolvedAfterBootstrap);
        Assert.Equal(0, retry.RetryCalls);
    }

    private sealed class NullPushNotificationService(PushRegistration registration) : IPushNotificationService
    {
        public Task<PushRegistration?> RegisterAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<PushRegistration?>(registration);

        public Task<PushRegistration?> GetCachedRegistrationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<PushRegistration?>(null);

        public Task UnregisterAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class BlockingRegistrationCoordinator(PushRegistration registration) : IPushRegistrationCoordinator
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RegisterCalls { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PushRegistration?> RegisterAsync(CancellationToken cancellationToken = default)
        {
            RegisterCalls++;
            Started.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return registration;
        }

        public Task UnregisterAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Release() => release.TrySetResult();
    }

    private sealed class ImmediateRegistrationCoordinator(PushRegistration registration) : IPushRegistrationCoordinator
    {
        public int RegisterCalls { get; private set; }

        public Task<PushRegistration?> RegisterAsync(CancellationToken cancellationToken = default)
        {
            RegisterCalls++;
            return Task.FromResult<PushRegistration?>(registration);
        }

        public Task UnregisterAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingRegularScheduler : IRegularBackgroundSyncScheduler
    {
        public int EnsureCalls { get; private set; }
        public int CancelCalls { get; private set; }

        public void EnsureScheduled() => EnsureCalls++;

        public void Cancel() => CancelCalls++;
    }

    private sealed class RecordingRetryCoordinator(bool result) :
        IPushRegistrationCoordinator,
        IPushUnsubscribeRetryCoordinator
    {
        public int RetryCalls { get; private set; }

        public Task<PushRegistration?> RegisterAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<PushRegistration?>(null);

        public Task UnregisterAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> RetryPendingUnsubscribeAsync(CancellationToken cancellationToken = default)
        {
            RetryCalls++;
            return Task.FromResult(result);
        }
    }

    private sealed class BootstrapAwareServiceProvider(
        ClientRuntimeBootstrapper bootstrapper,
        IPushRegistrationCoordinator coordinator) : IServiceProvider
    {
        public bool CoordinatorResolvedAfterBootstrap { get; private set; }

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(ClientRuntimeBootstrapper))
            {
                return bootstrapper;
            }

            if (serviceType == typeof(IPushRegistrationCoordinator))
            {
                Assert.Equal(ClientRuntimeBootstrapState.Ready, bootstrapper.State);
                CoordinatorResolvedAfterBootstrap = true;
                return coordinator;
            }

            return null;
        }
    }
}
