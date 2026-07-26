using System.Diagnostics;
using System.Security.Cryptography;
using Deep.Client.Maui.Outbox;
using Deep.Client.Shared.Features;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.Outbox.Tests;

public sealed class ProcessExternalTransportOutboxExecutorTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-26T08:00:00Z");

    [Fact]
    public async Task DisabledBootstrapDoesNotInspectOrStartAWorker()
    {
        var result = await ProcessExternalTransportOutboxExecutor.BootstrapAsync(
            enabled: false,
            options: null);

        Assert.Equal(ExternalTransportOutboxBootstrapStatus.Disabled, result.Status);
        Assert.Null(result.Executor);
    }

    [Fact]
    public async Task RuntimeActivationKeepsDirectRuntimeWhenSupervisorIsUnavailable()
    {
        var requested = ClientFeatureFlags.ReleaseDefaults with
        {
            PersistentTransportOutboxEnabled = true
        };

        var activation = await ExternalTransportOutboxRuntimeActivation.ResolveAsync(
            requested,
            options: null);

        Assert.False(activation.EffectiveFeatureFlags.PersistentTransportOutboxEnabled);
        Assert.Null(activation.Executor);
        Assert.Equal(
            ExternalTransportOutboxBootstrapStatus.InvalidConfiguration,
            activation.Status);
    }

    [Fact]
    public async Task RuntimeActivationEnablesOutboxOnlyAfterAttestationAndProbe()
    {
        var requested = ClientFeatureFlags.ReleaseDefaults with
        {
            PersistentTransportOutboxEnabled = true
        };
        var activation = await ExternalTransportOutboxRuntimeActivation.ResolveAsync(
            requested,
            WorkerOptions("durable"));
        using var executor = Assert.IsType<ProcessExternalTransportOutboxExecutor>(
            activation.Executor);

        Assert.True(activation.EffectiveFeatureFlags.PersistentTransportOutboxEnabled);
        Assert.Equal(ExternalTransportOutboxBootstrapStatus.Ready, activation.Status);
    }

    [Fact]
    public async Task HashMismatchFailsAttestationBeforeProbe()
    {
        var options = WorkerOptions("durable", expectedHash: new byte[32]);

        var result = await ProcessExternalTransportOutboxExecutor.BootstrapAsync(
            enabled: true,
            options);

        Assert.Equal(ExternalTransportOutboxBootstrapStatus.AttestationFailed, result.Status);
        Assert.Null(result.Executor);
    }

    [Fact]
    public async Task AttestedProbeAndDurableDispatchSucceed()
    {
        using var executor = await ReadyExecutorAsync("durable");
        var (result, persisted) = await DispatchAsync(executor);

        Assert.Equal(1, result.DurableCount);
        Assert.Equal(0, result.OutcomeUnknownCount);
        Assert.Equal(TransportOutboxState.Durable, persisted.Item?.State);
        Assert.Equal(
            TransportOutboxAttemptState.Durable,
            persisted.Item?.Attempts.Single().State);
    }

    [Fact]
    public async Task WorkerCrashIsOutcomeUnknownAndLeaseCapacityRecovers()
    {
        using var executor = await ReadyExecutorAsync("crash");
        var (result, persisted) = await DispatchAsync(executor);

        Assert.Equal(1, result.OutcomeUnknownCount);
        Assert.Equal(TransportOutboxState.Attempted, persisted.Item?.State);
        Assert.True(executor.TryAcquire(out var next));
        await next!.DisposeAsync();
    }

    [Fact]
    public async Task MalformedReceiptIsRejectedAndWorkerIsKilled()
    {
        var pidPath = TemporaryPath();
        try
        {
            using var executor = await ReadyExecutorAsync("malformed", pidPath);
            var (result, _) = await DispatchAsync(executor);

            Assert.Equal(1, result.OutcomeUnknownCount);
            await AssertProcessExitedAsync(pidPath);
        }
        finally
        {
            File.Delete(pidPath);
        }
    }

    [Fact]
    public async Task HostileHangIsHardKilledAtTimeout()
    {
        var pidPath = TemporaryPath();
        try
        {
            using var executor = await ReadyExecutorAsync(
                "hang",
                pidPath,
                maximumDispatchDuration: TimeSpan.FromSeconds(1));
            var stopwatch = Stopwatch.StartNew();
            var (result, _) = await DispatchAsync(executor);
            stopwatch.Stop();

            Assert.Equal(1, result.OutcomeUnknownCount);
            Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
            await AssertProcessExitedAsync(pidPath);
        }
        finally
        {
            File.Delete(pidPath);
        }
    }

    [Fact]
    public async Task CallerCancellationReturnsOnlyAfterWorkerExit()
    {
        var pidPath = TemporaryPath();
        try
        {
            using var executor = await ReadyExecutorAsync(
                "hang",
                pidPath,
                maximumDispatchDuration: TimeSpan.FromSeconds(10));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => DispatchAsync(executor, cancellation.Token));
            await AssertProcessExitedAsync(pidPath);
        }
        finally
        {
            File.Delete(pidPath);
        }
    }

    [Fact]
    public async Task ExecutorDisposalKillsActiveWorkerAndLeavesNoCapacityAdmission()
    {
        var pidPath = TemporaryPath();
        try
        {
            var executor = await ReadyExecutorAsync(
                "hang",
                pidPath,
                maximumDispatchDuration: TimeSpan.FromSeconds(30));
            var dispatch = DispatchAsync(executor);
            await WaitForPidAsync(pidPath);

            executor.Dispose();
            var (result, _) = await dispatch.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, result.OutcomeUnknownCount);
            Assert.False(executor.TryAcquire(out _));
            await AssertProcessExitedAsync(pidPath);
        }
        finally
        {
            File.Delete(pidPath);
        }
    }

    [Fact]
    public async Task CapacityReservationIsNonBlockingAndBounded()
    {
        using var executor = await ReadyExecutorAsync("durable");

        Assert.True(executor.TryAcquire(out var first));
        Assert.False(executor.TryAcquire(out var second));
        Assert.Null(second);

        await first!.DisposeAsync();
        Assert.True(executor.TryAcquire(out var afterRelease));
        await afterRelease!.DisposeAsync();
    }

    [Fact]
    public async Task WorkerRejectionNeverBecomesAcceptedReceipt()
    {
        using var executor = await ReadyExecutorAsync("rejected");
        var (result, persisted) = await DispatchAsync(executor);

        Assert.Equal(1, result.OutcomeUnknownCount);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(0, result.DurableCount);
        Assert.Equal(TransportOutboxState.Attempted, persisted.Item?.State);
    }

    private static async Task<ProcessExternalTransportOutboxExecutor> ReadyExecutorAsync(
        string mode,
        string? pidPath = null,
        TimeSpan? maximumDispatchDuration = null)
    {
        var result = await ProcessExternalTransportOutboxExecutor.BootstrapAsync(
            enabled: true,
            WorkerOptions(mode, pidPath: pidPath, maximumDispatchDuration: maximumDispatchDuration));
        Assert.Equal(ExternalTransportOutboxBootstrapStatus.Ready, result.Status);
        return Assert.IsType<ProcessExternalTransportOutboxExecutor>(result.Executor);
    }

    private static ProcessExternalTransportOutboxExecutorOptions WorkerOptions(
        string mode,
        byte[]? expectedHash = null,
        string? pidPath = null,
        TimeSpan? maximumDispatchDuration = null)
    {
        var executable = Path.Combine(
            AppContext.BaseDirectory,
            OperatingSystem.IsWindows()
                ? "Deep.Client.Maui.OutboxWorker.TestHost.exe"
                : "Deep.Client.Maui.OutboxWorker.TestHost");
        expectedHash ??= SHA256.HashData(File.ReadAllBytes(executable));
        var arguments = new List<string> { $"--mode={mode}" };
        if (!string.IsNullOrWhiteSpace(pidPath))
        {
            arguments.Add($"--pid-file={pidPath}");
        }

        return new(
            executable,
            AppContext.BaseDirectory,
            expectedHash,
            maximumDispatchDuration ?? TimeSpan.FromSeconds(2),
            maximumConcurrentExecutions: 1,
            arguments);
    }

    private static async Task<(
        TransportOutboxDispatchBatchResult Result,
        TransportOutboxReadSnapshot Persisted)> DispatchAsync(
        ProcessExternalTransportOutboxExecutor executor,
        CancellationToken cancellationToken = default)
    {
        var store = new InMemorySessionStore();
        using var runtime = new ClientRuntime(
            store,
            ClientFeatureFlags.Defaults with { PersistentTransportOutboxEnabled = true },
            new FixedClock(Now),
            new StubSessionBackend(),
            transportOutboxExecutor: executor);
        var item = TransportOutboxPreparedItem.Create(
            OutboxAccountScope.FromBytes(Bytes(TransportOutboxLimits.AccountScopeBytes, 0x11)),
            OutboxLogicalId.FromBytes(Bytes(TransportOutboxLimits.LogicalIdBytes, 0x22)),
            OutboxDedupMaterial.FromBytes(Bytes(TransportOutboxLimits.DedupMaterialBytes, 0x33)),
            Bytes(96, 0x44),
            Now,
            Now.AddHours(1),
            Now);

        Assert.Equal(
            TransportOutboxCommitResult.Applied,
            await runtime.TransportOutbox!.PrepareAsync(item, cancellationToken));
        var result = await runtime.TransportOutbox.DispatchReadyAsync(
            item.AccountScope,
            cancellationToken: cancellationToken);
        var persisted = await runtime.TransportOutbox.ReadAsync(
            item.AccountScope,
            item.LogicalId,
            cancellationToken);
        return (result, persisted);
    }

    private static async Task AssertProcessExitedAsync(string pidPath)
    {
        var pid = await WaitForPidAsync(pidPath);
        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.True(process.HasExited);
        }
        catch (ArgumentException)
        {
            // Process no longer exists.
        }
    }

    private static async Task<int> WaitForPidAsync(string pidPath)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(pidPath)
                && int.TryParse(await File.ReadAllTextAsync(pidPath), out var pid))
            {
                return pid;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException("Worker PID evidence was not created.");
    }

    private static string TemporaryPath() =>
        Path.Combine(Path.GetTempPath(), $"deep-outbox-worker-{Guid.NewGuid():N}.pid");

    private static byte[] Bytes(int length, byte value) =>
        Enumerable.Repeat(value, length).ToArray();

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
