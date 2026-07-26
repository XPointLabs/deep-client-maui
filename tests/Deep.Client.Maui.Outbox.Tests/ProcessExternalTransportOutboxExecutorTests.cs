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
    public async Task PublicConfigurationRejectsUserWritableDeploymentRoot()
    {
        var executable = WorkerExecutablePath();
        var executableHash = SHA256.HashData(File.ReadAllBytes(executable));
        var bundleHash = await ProcessExternalTransportOutboxExecutor
            .ComputeBundleSha256ForTestsAsync(AppContext.BaseDirectory, executable);
        var options = new ProcessExternalTransportOutboxExecutorOptions(
            executable,
            AppContext.BaseDirectory,
            executableHash,
            bundleHash,
            TimeSpan.FromSeconds(2));

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
    public async Task MaximumCiphertextBundleFitsAuthenticatedRequestFrame()
    {
        Assert.Equal(
            TransportOutboxLimits.MaxCiphertextBundleBytes + 183,
            ExternalTransportOutboxWorkerProtocol.MaximumRequestFrameBytes);
        using var executor = await ReadyExecutorAsync("durable");
        var (result, persisted) = await DispatchAsync(
            executor,
            ciphertextBundleBytes: TransportOutboxLimits.MaxCiphertextBundleBytes);

        Assert.Equal(1, result.DurableCount);
        Assert.Equal(TransportOutboxState.Durable, persisted.Item?.State);
    }

    [Fact]
    public async Task MaximumBinaryRequestRoundTripsAtExactBoundAndContextZerosSecrets()
    {
        var nonce = Bytes(ExternalTransportOutboxWorkerProtocol.NonceBytes, 0xA1);
        var ciphertext = Bytes(TransportOutboxLimits.MaxCiphertextBundleBytes, 0xC1);
        var request = new ExternalTransportOutboxWorkerRequest(
            ExternalTransportOutboxWorkerProtocol.Version,
            ExternalTransportOutboxWorkerOperation.Dispatch,
            nonce,
            Bytes(TransportOutboxLimits.LogicalIdBytes, 0x11),
            Bytes(TransportOutboxLimits.AttemptIdBytes, 0x22),
            Bytes(TransportOutboxLimits.DedupMaterialBytes, 0x33),
            ciphertext,
            Now.AddHours(1).ToUnixTimeMilliseconds());
        var sessionKey = Bytes(ExternalTransportOutboxWorkerProtocol.SessionKeyBytes, 0x55);
        await using var stream = new MemoryStream();

        await ExternalTransportOutboxWorkerProtocol.WriteRequestAsync(
            stream,
            request,
            sessionKey);

        Assert.Equal(
            ExternalTransportOutboxWorkerProtocol.MaximumRequestFrameBytes + sizeof(int),
            stream.Length);
        stream.Position = 0;
        var context = await ExternalTransportOutboxWorkerProtocol.ReadRequestAsync(stream);
        var decodedNonce = context.Request.Nonce;
        var decodedCiphertext = context.Request.CiphertextBundle!;
        var decodedSessionKey = context.SessionKey;
        Assert.Equal(ciphertext, decodedCiphertext);

        context.Dispose();

        Assert.All(decodedNonce, static value => Assert.Equal(0, value));
        Assert.All(decodedCiphertext, static value => Assert.Equal(0, value));
        Assert.All(decodedSessionKey, static value => Assert.Equal(0, value));
        CryptographicOperations.ZeroMemory(sessionKey);
        CryptographicOperations.ZeroMemory(nonce);
        CryptographicOperations.ZeroMemory(ciphertext);
    }

    [Fact]
    public async Task ParentAndRootLocksPreventPrelaunchRenameAndRecreate()
    {
        var parent = CopyWorkerBundle(out var root, out var executable);
        var renameBlocked = false;
        try
        {
            var options = WorkerOptions(
                "durable",
                afterBundleLockedBeforeLaunch: () =>
                {
                    var moved = root + ".moved";
                    var renamed = false;
                    try
                    {
                        Directory.Move(root, moved);
                        renamed = true;
                        Directory.CreateDirectory(root);
                    }
                    catch (IOException)
                    {
                        renameBlocked = true;
                    }
                    finally
                    {
                        if (renamed)
                        {
                            Directory.Delete(root, recursive: true);
                            Directory.Move(moved, root);
                        }
                    }
                    Assert.True(renameBlocked);
                    Assert.True(Directory.Exists(root));
                    Assert.False(Directory.Exists(moved));
                },
                trustedRoot: root,
                executable: executable);

            var result = await ProcessExternalTransportOutboxExecutor.BootstrapAsync(
                enabled: true,
                options);
            using var executor = Assert.IsType<ProcessExternalTransportOutboxExecutor>(
                result.Executor);

            Assert.Equal(ExternalTransportOutboxBootstrapStatus.Ready, result.Status);
            Assert.True(renameBlocked);
        }
        finally
        {
            TryDeleteDirectory(parent);
        }
    }

    [Fact]
    public async Task BundleFilesRemainDenyWriteAndDeleteLockedAcrossLaunchBoundary()
    {
        var lockObserved = false;
        var options = WorkerOptions(
            "durable",
            afterBundleLockedBeforeLaunch: () =>
            {
                AssertDenyWriteDeleteLock(WorkerExecutablePath());
                AssertDenyWriteDeleteLock(
                    Path.Combine(
                        AppContext.BaseDirectory,
                        "Deep.Client.Maui.OutboxWorker.TestHost.runtimeconfig.json"));
                lockObserved = true;
            });

        var result = await ProcessExternalTransportOutboxExecutor.BootstrapAsync(
            enabled: true,
            options);
        using var executor = Assert.IsType<ProcessExternalTransportOutboxExecutor>(
            result.Executor);

        Assert.Equal(ExternalTransportOutboxBootstrapStatus.Ready, result.Status);
        Assert.True(lockObserved);
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
    public async Task FailedJobTerminationFallsBackToProcessKillAndProvesEmptyJob()
    {
        using var executor = await ReadyExecutorAsync(
            "hang",
            maximumDispatchDuration: TimeSpan.FromSeconds(1),
            failTerminateJobObject: true,
            terminationConfirmationTimeout: TimeSpan.FromSeconds(1));

        var (result, _) = await DispatchAsync(executor);

        Assert.Equal(1, result.OutcomeUnknownCount);
        Assert.True(executor.TryAcquire(out var next));
        await next!.DisposeAsync();
    }

    [Fact]
    public async Task UnprovenTerminationReturnsBoundedAndPermanentlyPoisonsCapacity()
    {
        var pidPath = TemporaryPath();
        try
        {
            using var executor = await ReadyExecutorAsync(
                "hang",
                pidPath,
                maximumDispatchDuration: TimeSpan.FromSeconds(1),
                failTerminateJobObject: true,
                failProcessKill: true,
                terminationConfirmationTimeout: TimeSpan.FromMilliseconds(100));
            var stopwatch = Stopwatch.StartNew();

            var (result, _) = await DispatchAsync(executor);
            stopwatch.Stop();

            Assert.Equal(1, result.OutcomeUnknownCount);
            Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(4));
            Assert.False(executor.TryAcquire(out _));
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
    public async Task InstantDescendantIsInJobBeforeWorkerCanExecuteAndIsKilled()
    {
        var childPidPath = TemporaryPath();
        try
        {
            using var executor = await ReadyExecutorAsync(
                "spawn-child-hang",
                maximumDispatchDuration: TimeSpan.FromSeconds(1),
                childPidPath: childPidPath);

            var (result, _) = await DispatchAsync(executor);

            Assert.Equal(1, result.OutcomeUnknownCount);
            await AssertProcessExitedAsync(childPidPath);
        }
        finally
        {
            File.Delete(childPidPath);
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
        TimeSpan? maximumDispatchDuration = null,
        string? childPidPath = null,
        bool failTerminateJobObject = false,
        bool failProcessKill = false,
        TimeSpan? terminationConfirmationTimeout = null)
    {
        var result = await ProcessExternalTransportOutboxExecutor.BootstrapAsync(
            enabled: true,
            WorkerOptions(
                mode,
                pidPath: pidPath,
                maximumDispatchDuration: maximumDispatchDuration,
                childPidPath: childPidPath,
                failTerminateJobObject: failTerminateJobObject,
                failProcessKill: failProcessKill,
                terminationConfirmationTimeout: terminationConfirmationTimeout));
        Assert.Equal(ExternalTransportOutboxBootstrapStatus.Ready, result.Status);
        return Assert.IsType<ProcessExternalTransportOutboxExecutor>(result.Executor);
    }

    private static ProcessExternalTransportOutboxExecutorOptions WorkerOptions(
        string mode,
        byte[]? expectedHash = null,
        string? pidPath = null,
        TimeSpan? maximumDispatchDuration = null,
        Action? afterBundleLockedBeforeLaunch = null,
        string? childPidPath = null,
        string? trustedRoot = null,
        string? executable = null,
        bool failTerminateJobObject = false,
        bool failProcessKill = false,
        TimeSpan? terminationConfirmationTimeout = null)
    {
        trustedRoot ??= AppContext.BaseDirectory;
        executable ??= WorkerExecutablePath();
        expectedHash ??= SHA256.HashData(File.ReadAllBytes(executable));
        var expectedBundleHash = ProcessExternalTransportOutboxExecutor
            .ComputeBundleSha256ForTestsAsync(trustedRoot, executable)
            .GetAwaiter()
            .GetResult();
        var arguments = new List<string> { $"--mode={mode}" };
        if (!string.IsNullOrWhiteSpace(pidPath))
        {
            arguments.Add($"--pid-file={pidPath}");
        }
        if (!string.IsNullOrWhiteSpace(childPidPath))
        {
            arguments.Add($"--child-pid-file={childPidPath}");
        }

        return new ProcessExternalTransportOutboxExecutorOptions(
            executable,
            trustedRoot,
            expectedHash,
            expectedBundleHash,
            maximumDispatchDuration ?? TimeSpan.FromSeconds(2),
            maximumConcurrentExecutions: 1,
            arguments,
            allowWritableTrustedRootForTests: true,
            afterBundleLockedBeforeLaunchForTests: afterBundleLockedBeforeLaunch,
            failTerminateJobObjectForTests: failTerminateJobObject,
            failProcessKillForTests: failProcessKill,
            terminationConfirmationTimeoutForTests: terminationConfirmationTimeout);
    }

    private static string WorkerExecutablePath() =>
        Path.Combine(
            AppContext.BaseDirectory,
            OperatingSystem.IsWindows()
                ? "Deep.Client.Maui.OutboxWorker.TestHost.exe"
                : "Deep.Client.Maui.OutboxWorker.TestHost");

    private static void AssertDenyWriteDeleteLock(string path)
    {
        Assert.True(File.Exists(path));
        _ = Assert.ThrowsAny<IOException>(
            () => new FileStream(
                path,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete));
        _ = Assert.ThrowsAny<IOException>(
            () => File.Move(path, path + ".race", overwrite: true));
    }

    private static string CopyWorkerBundle(out string root, out string executable)
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            $"deep-outbox-root-lock-{Guid.NewGuid():N}");
        root = Path.Combine(parent, "bundle");
        Directory.CreateDirectory(root);
        foreach (var source in Directory.EnumerateFiles(
            AppContext.BaseDirectory,
            "*",
            SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(AppContext.BaseDirectory, source);
            var destination = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }
        executable = Path.Combine(root, Path.GetFileName(WorkerExecutablePath()));
        return parent;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task<(
        TransportOutboxDispatchBatchResult Result,
        TransportOutboxReadSnapshot Persisted)> DispatchAsync(
        ProcessExternalTransportOutboxExecutor executor,
        CancellationToken cancellationToken = default,
        int ciphertextBundleBytes = 96)
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
            Bytes(ciphertextBundleBytes, 0x44),
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
