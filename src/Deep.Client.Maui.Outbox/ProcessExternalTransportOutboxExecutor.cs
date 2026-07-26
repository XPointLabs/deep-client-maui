using System.Buffers.Binary;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Microsoft.Win32.SafeHandles;

namespace Deep.Client.Maui.Outbox;

public enum ExternalTransportOutboxBootstrapStatus
{
    Ready = 1,
    Disabled = 2,
    UnsupportedPlatform = 3,
    InvalidConfiguration = 4,
    AttestationFailed = 5,
    ProbeFailed = 6
}

public sealed record ExternalTransportOutboxBootstrapResult(
    ExternalTransportOutboxBootstrapStatus Status,
    IExternalTransportOutboxExecutor? Executor)
{
    public bool IsReady =>
        Status == ExternalTransportOutboxBootstrapStatus.Ready && Executor is not null;
}

public sealed class ProcessExternalTransportOutboxExecutorOptions
{
    private readonly byte[] expectedSha256;
    private readonly byte[] expectedBundleSha256;
    private readonly string[] arguments;
    private readonly ReadOnlyCollection<string> readOnlyArguments;

    public ProcessExternalTransportOutboxExecutorOptions(
        string workerExecutablePath,
        string trustedRootDirectory,
        ReadOnlySpan<byte> expectedSha256,
        ReadOnlySpan<byte> expectedBundleSha256,
        TimeSpan maximumDispatchDuration,
        int maximumConcurrentExecutions = 1,
        IEnumerable<string>? arguments = null)
        : this(
            workerExecutablePath,
            trustedRootDirectory,
            expectedSha256,
            expectedBundleSha256,
            maximumDispatchDuration,
            maximumConcurrentExecutions,
            arguments,
            allowWritableTrustedRootForTests: false)
    {
    }

    internal ProcessExternalTransportOutboxExecutorOptions(
        string workerExecutablePath,
        string trustedRootDirectory,
        ReadOnlySpan<byte> expectedSha256,
        ReadOnlySpan<byte> expectedBundleSha256,
        TimeSpan maximumDispatchDuration,
        int maximumConcurrentExecutions,
        IEnumerable<string>? arguments,
        bool allowWritableTrustedRootForTests,
        Action? afterBundleLockedBeforeLaunchForTests = null,
        bool failTerminateJobObjectForTests = false,
        bool failProcessKillForTests = false,
        TimeSpan? terminationConfirmationTimeoutForTests = null)
    {
        WorkerExecutablePath = Path.GetFullPath(
            workerExecutablePath ?? throw new ArgumentNullException(nameof(workerExecutablePath)));
        TrustedRootDirectory = Path.GetFullPath(
            trustedRootDirectory ?? throw new ArgumentNullException(nameof(trustedRootDirectory)));
        this.expectedSha256 = expectedSha256.ToArray();
        this.expectedBundleSha256 = expectedBundleSha256.ToArray();
        MaximumDispatchDuration = maximumDispatchDuration;
        MaximumConcurrentExecutions = maximumConcurrentExecutions;
        this.arguments = arguments?.ToArray() ?? [];
        readOnlyArguments = Array.AsReadOnly(this.arguments);
        AllowWritableTrustedRootForTests = allowWritableTrustedRootForTests;
        AfterBundleLockedBeforeLaunchForTests = afterBundleLockedBeforeLaunchForTests;
        FailTerminateJobObjectForTests = failTerminateJobObjectForTests;
        FailProcessKillForTests = failProcessKillForTests;
        TerminationConfirmationTimeout =
            terminationConfirmationTimeoutForTests ?? TimeSpan.FromSeconds(5);
    }

    public string WorkerExecutablePath { get; }

    public string TrustedRootDirectory { get; }

    public TimeSpan MaximumDispatchDuration { get; }

    public int MaximumConcurrentExecutions { get; }

    public byte[] GetExpectedSha256Copy() => expectedSha256.ToArray();

    public byte[] GetExpectedBundleSha256Copy() => expectedBundleSha256.ToArray();

    public IReadOnlyList<string> Arguments => readOnlyArguments;

    internal bool AllowWritableTrustedRootForTests { get; }

    internal Action? AfterBundleLockedBeforeLaunchForTests { get; }

    internal bool FailTerminateJobObjectForTests { get; }

    internal bool FailProcessKillForTests { get; }

    internal TimeSpan TerminationConfirmationTimeout { get; }
}

/// <summary>
/// Windows-only, per-dispatch process supervisor. No process is kept alive
/// between dispatches, so an idle client consumes no worker CPU or wakeups.
/// </summary>
public sealed class ProcessExternalTransportOutboxExecutor :
    IExternalTransportOutboxExecutor,
    IDisposable
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    private const int MaximumBundleFiles = 256;
    private const long MaximumBundleBytes = 512L * 1024 * 1024;
    private const int MaximumRelativePathUtf8Bytes = 512;
    private static readonly byte[] BundleHashDomain =
        "Deep.ExternalTransportOutbox.Bundle.v1\0"u8.ToArray();
    private readonly ProcessExternalTransportOutboxExecutorOptions options;
    private readonly SemaphoreSlim capacity;
    private readonly CancellationTokenSource shutdown = new();
    private readonly object leasesSync = new();
    private readonly HashSet<Execution> leases = [];
    private readonly TaskCompletionSource disposalCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposed;
    private int containmentCompromised;

    private ProcessExternalTransportOutboxExecutor(
        ProcessExternalTransportOutboxExecutorOptions options)
    {
        this.options = options;
        capacity = new SemaphoreSlim(
            options.MaximumConcurrentExecutions,
            options.MaximumConcurrentExecutions);
    }

    public TimeSpan MaximumDispatchDuration => options.MaximumDispatchDuration;

    public static async Task<ExternalTransportOutboxBootstrapResult> BootstrapAsync(
        bool enabled,
        ProcessExternalTransportOutboxExecutorOptions? options,
        CancellationToken cancellationToken = default)
    {
        if (!enabled)
        {
            return new(ExternalTransportOutboxBootstrapStatus.Disabled, null);
        }
        if (!OperatingSystem.IsWindows())
        {
            // Android intentionally stays closed. A Service in the application
            // process is not an independently killable execution boundary.
            return new(ExternalTransportOutboxBootstrapStatus.UnsupportedPlatform, null);
        }
        if (options is null || !ValidateOptions(options))
        {
            return new(ExternalTransportOutboxBootstrapStatus.InvalidConfiguration, null);
        }
        using var verifiedBundle = await VerifyAndLockBundleAsync(
                options,
                cancellationToken)
            .ConfigureAwait(false);
        if (verifiedBundle is null)
        {
            return new(ExternalTransportOutboxBootstrapStatus.AttestationFailed, null);
        }

        var executor = new ProcessExternalTransportOutboxExecutor(options);
        try
        {
            await executor.InvokeWorkerAsync(
                    request: CreateProbeRequest(),
                    hardTimeout: Min(ProbeTimeout, options.MaximumDispatchDuration),
                    cancellationToken,
                    verifiedBundle)
                .ConfigureAwait(false);
            return new(ExternalTransportOutboxBootstrapStatus.Ready, executor);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            executor.Dispose();
            throw;
        }
        catch when (options.AllowWritableTrustedRootForTests)
        {
            executor.Dispose();
            throw;
        }
        catch
        {
            executor.Dispose();
            return new(ExternalTransportOutboxBootstrapStatus.ProbeFailed, null);
        }
    }

    public bool TryAcquire(out IExternalTransportOutboxExecution? execution)
    {
        execution = null;
        lock (leasesSync)
        {
            if (disposed != 0
                || Volatile.Read(ref containmentCompromised) != 0
                || !capacity.Wait(0))
            {
                return false;
            }

            var acquired = new Execution(this);
            leases.Add(acquired);
            execution = acquired;
            return true;
        }
    }

    public void Dispose()
    {
        Execution[] active;
        var ownsDisposal = false;
        lock (leasesSync)
        {
            if (disposed == 0)
            {
                Volatile.Write(ref disposed, 1);
                active = leases.ToArray();
                ownsDisposal = true;
            }
            else
            {
                active = [];
            }
        }
        if (!ownsDisposal)
        {
            disposalCompleted.Task.GetAwaiter().GetResult();
            return;
        }

        try
        {
            shutdown.Cancel();
            foreach (var execution in active)
            {
                execution.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            shutdown.Dispose();
            capacity.Dispose();
            disposalCompleted.TrySetResult();
        }
        catch (Exception exception)
        {
            disposalCompleted.TrySetException(exception);
            throw;
        }
    }

    private async Task<TransportOutboxAdapterReceipt> InvokeWorkerAsync(
        ExternalTransportOutboxWorkerRequest request,
        TimeSpan hardTimeout,
        CancellationToken cancellationToken,
        VerifiedWorkerBundle? bootstrapBundle = null)
    {
        try
        {
            return await InvokeWorkerCoreAsync(
                    request,
                    hardTimeout,
                    cancellationToken,
                    bootstrapBundle)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(request.Nonce);
        }
    }

    private async Task<TransportOutboxAdapterReceipt> InvokeWorkerCoreAsync(
        ExternalTransportOutboxWorkerRequest request,
        TimeSpan hardTimeout,
        CancellationToken cancellationToken,
        VerifiedWorkerBundle? bootstrapBundle)
    {
        if (hardTimeout <= TimeSpan.Zero || hardTimeout > MaximumDispatchDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(hardTimeout));
        }
        using var dispatchBundle = bootstrapBundle is null
            ? await VerifyAndLockBundleAsync(options, cancellationToken).ConfigureAwait(false)
            : null;
        var verifiedBundle = bootstrapBundle ?? dispatchBundle;
        if (verifiedBundle is null)
        {
            throw new ExternalTransportOutboxExecutionException();
        }
        options.AfterBundleLockedBeforeLaunchForTests?.Invoke();

        using var worker = SuspendedJobWorker.Start(options);
        var stderrDrain = DrainWithoutRetentionAsync(worker.StandardError);
        var sessionKey = RandomNumberGenerator.GetBytes(
            ExternalTransportOutboxWorkerProtocol.SessionKeyBytes);
        try
        {
            var responseTask = ExchangeAsync(worker, request, sessionKey);
            var timeoutTask = Task.Delay(hardTimeout);
            var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var winner = await Task.WhenAny(
                    responseTask,
                    timeoutTask,
                    cancellationTask)
                .ConfigureAwait(false);

            if (winner == cancellationTask)
            {
                await StopWorkerConfirmedAsync(
                        worker,
                        stderrDrain,
                        responseTask,
                        request.Operation == ExternalTransportOutboxWorkerOperation.Dispatch)
                    .ConfigureAwait(false);
                throw new OperationCanceledException(cancellationToken);
            }
            if (winner == timeoutTask)
            {
                await StopWorkerConfirmedAsync(
                        worker,
                        stderrDrain,
                        responseTask,
                        request.Operation == ExternalTransportOutboxWorkerOperation.Dispatch)
                    .ConfigureAwait(false);
                throw new ExternalTransportOutboxExecutionException();
            }

            ExternalTransportOutboxWorkerResponse response;
            try
            {
                response = await responseTask.ConfigureAwait(false);
            }
            catch when (options.AllowWritableTrustedRootForTests)
            {
                await StopWorkerConfirmedAsync(
                        worker,
                        stderrDrain,
                        responseTask,
                        request.Operation == ExternalTransportOutboxWorkerOperation.Dispatch)
                    .ConfigureAwait(false);
                throw;
            }
            catch
            {
                await StopWorkerConfirmedAsync(
                        worker,
                        stderrDrain,
                        responseTask,
                        request.Operation == ExternalTransportOutboxWorkerOperation.Dispatch)
                    .ConfigureAwait(false);
                throw new ExternalTransportOutboxExecutionException();
            }

            try
            {
                await StopWorkerConfirmedAsync(
                        worker,
                        stderrDrain,
                        responseTask,
                        request.Operation == ExternalTransportOutboxWorkerOperation.Dispatch)
                    .ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(request.Nonce, response.Nonce)
                    || !response.Success)
                {
                    throw new ExternalTransportOutboxExecutionException();
                }

                return response.Disposition switch
                {
                    TransportOutboxAdapterDisposition.Accepted =>
                        TransportOutboxAdapterReceipt.Accepted(response.AcceptedEvidence!),
                    TransportOutboxAdapterDisposition.Durable =>
                        TransportOutboxAdapterReceipt.Durable(
                            response.AcceptedEvidence!,
                            response.DurableEvidence!),
                    _ => throw new ExternalTransportOutboxExecutionException()
                };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(response.Nonce);
                Zero(response.AcceptedEvidence);
                Zero(response.DurableEvidence);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionKey);
        }
    }

    private static void Zero(byte[]? value)
    {
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    private static async Task<ExternalTransportOutboxWorkerResponse> ExchangeAsync(
        SuspendedJobWorker worker,
        ExternalTransportOutboxWorkerRequest request,
        byte[] sessionKey)
    {
        await ExternalTransportOutboxWorkerProtocol.WriteRequestAsync(
                worker.StandardInput,
                request,
                sessionKey)
            .ConfigureAwait(false);
        worker.CloseStandardInput();
        return await ExternalTransportOutboxWorkerProtocol.ReadResponseAsync(
                worker.StandardOutput,
                sessionKey)
            .ConfigureAwait(false);
    }

    private async Task StopWorkerConfirmedAsync(
        SuspendedJobWorker worker,
        Task stderrDrain,
        Task responseTask,
        bool injectTerminationFailuresForTests)
    {
        var confirmed = worker.TerminateAndConfirm(
            options.TerminationConfirmationTimeout,
            injectTerminationFailuresForTests && options.FailTerminateJobObjectForTests,
            injectTerminationFailuresForTests && options.FailProcessKillForTests);
        worker.CloseOutputStreams();
        await ObserveBoundedAsync(responseTask).ConfigureAwait(false);
        await ObserveBoundedAsync(stderrDrain).ConfigureAwait(false);
        if (!confirmed)
        {
            Volatile.Write(ref containmentCompromised, 1);
            throw new ExternalTransportOutboxExecutionException();
        }
    }

    private static async Task ObserveBoundedAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch
        {
            // Pipe content and failures are intentionally never retained.
        }
    }

    private static async Task DrainWithoutRetentionAsync(Stream stream)
    {
        var buffer = new byte[4096];
        while (await stream.ReadAsync(buffer).ConfigureAwait(false) != 0)
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static ExternalTransportOutboxWorkerRequest CreateProbeRequest() =>
        new(
            ExternalTransportOutboxWorkerProtocol.Version,
            ExternalTransportOutboxWorkerOperation.Probe,
            RandomNumberGenerator.GetBytes(ExternalTransportOutboxWorkerProtocol.NonceBytes),
            null,
            null,
            null,
            null,
            null);

    private static bool ValidateOptions(ProcessExternalTransportOutboxExecutorOptions options)
    {
        var expectedHash = options.GetExpectedSha256Copy();
        var expectedBundleHash = options.GetExpectedBundleSha256Copy();
        try
        {
            return expectedHash.Length == 32
                && expectedBundleHash.Length == 32
                && options.MaximumDispatchDuration > TimeSpan.Zero
                && options.MaximumDispatchDuration <= TimeSpan.FromMinutes(5)
                && options.TerminationConfirmationTimeout > TimeSpan.Zero
                && options.TerminationConfirmationTimeout <= TimeSpan.FromSeconds(5)
                && options.MaximumConcurrentExecutions is >= 1 and <= 4
                && options.Arguments.Count <= 16
                && options.Arguments.All(static argument =>
                    argument.Length <= 256
                    && argument.IndexOfAny('\0', '\r', '\n') < 0)
                && IsPathInsideRoot(options.WorkerExecutablePath, options.TrustedRootDirectory)
                && File.Exists(options.WorkerExecutablePath)
                && !HasReparsePoint(options.WorkerExecutablePath, options.TrustedRootDirectory);
        }
        catch
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedHash);
            CryptographicOperations.ZeroMemory(expectedBundleHash);
        }
    }

    internal static async Task<byte[]> ComputeBundleSha256ForTestsAsync(
        string trustedRootDirectory,
        string workerExecutablePath,
        CancellationToken cancellationToken = default)
    {
        using var bundle = await OpenAndHashBundleAsync(
                Path.GetFullPath(trustedRootDirectory),
                Path.GetFullPath(workerExecutablePath),
                FileShare.ReadWrite | FileShare.Delete,
                cancellationToken,
                lockDirectoryChain: false)
            .ConfigureAwait(false);
        return bundle.GetBundleHashCopy();
    }

    private static async Task<VerifiedWorkerBundle?> VerifyAndLockBundleAsync(
        ProcessExternalTransportOutboxExecutorOptions options,
        CancellationToken cancellationToken)
    {
        VerifiedWorkerBundle? bundle = null;
        try
        {
            if (!ValidateOptions(options))
            {
                return null;
            }
            var parentDirectory = Directory.GetParent(options.TrustedRootDirectory)?.FullName;
            if (parentDirectory is null
                || (File.GetAttributes(parentDirectory) & FileAttributes.ReparsePoint) != 0
                || !options.AllowWritableTrustedRootForTests
                    && (SuspendedJobWorker.CanCurrentProcessModifyDirectory(
                            options.TrustedRootDirectory)
                        || SuspendedJobWorker.CanCurrentProcessModifyDirectory(
                            parentDirectory)))
            {
                return null;
            }

            bundle = await OpenAndHashBundleAsync(
                    options.TrustedRootDirectory,
                    options.WorkerExecutablePath,
                    FileShare.Read,
                    cancellationToken,
                    lockDirectoryChain: true)
                .ConfigureAwait(false);
            var expectedHash = options.GetExpectedSha256Copy();
            var expectedBundleHash = options.GetExpectedBundleSha256Copy();
            var actualHash = bundle.GetExecutableHashCopy();
            var actualBundleHash = bundle.GetBundleHashCopy();
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash)
                    || !CryptographicOperations.FixedTimeEquals(
                        expectedBundleHash,
                        actualBundleHash))
                {
                    bundle.Dispose();
                    bundle = null;
                }
                return bundle;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expectedHash);
                CryptographicOperations.ZeroMemory(expectedBundleHash);
                CryptographicOperations.ZeroMemory(actualHash);
                CryptographicOperations.ZeroMemory(actualBundleHash);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            bundle?.Dispose();
            return null;
        }
    }

    private static async Task<VerifiedWorkerBundle> OpenAndHashBundleAsync(
        string trustedRootDirectory,
        string workerExecutablePath,
        FileShare share,
        CancellationToken cancellationToken,
        bool lockDirectoryChain)
    {
        if (!Directory.Exists(trustedRootDirectory)
            || !File.Exists(workerExecutablePath)
            || !IsPathInsideRoot(workerExecutablePath, trustedRootDirectory)
            || HasReparsePoint(workerExecutablePath, trustedRootDirectory))
        {
            throw new InvalidDataException("The outbox worker bundle is invalid.");
        }

        var directoryLocks = lockDirectoryChain
            ? SuspendedJobWorker.LockParentAndRootDirectories(trustedRootDirectory)
            : [];
        string[] paths;
        try
        {
            paths = EnumerateBundleFiles(trustedRootDirectory)
                .OrderBy(
                    path => Path.GetRelativePath(trustedRootDirectory, path),
                    StringComparer.Ordinal)
                .ToArray();
            if (paths.Length is <= 0 or > MaximumBundleFiles)
            {
                throw new InvalidDataException("The outbox worker bundle is invalid.");
            }
        }
        catch
        {
            foreach (var directoryLock in directoryLocks)
            {
                directoryLock.Dispose();
            }
            throw;
        }

        var streams = new List<FileStream>(paths.Length);
        using var bundleHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var executableHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        bundleHash.AppendData(BundleHashDomain);
        var executableFound = false;
        long totalBytes = 0;
        var buffer = new byte[64 * 1024];
        var metadata = new byte[sizeof(int) + sizeof(long)];
        try
        {
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsPathInsideRoot(path, trustedRootDirectory)
                    || HasReparsePoint(path, trustedRootDirectory))
                {
                    throw new InvalidDataException("The outbox worker bundle is invalid.");
                }

                var relativePath = Path
                    .GetRelativePath(trustedRootDirectory, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                var relativePathBytes = Encoding.UTF8.GetBytes(relativePath);
                if (relativePathBytes.Length is <= 0 or > MaximumRelativePathUtf8Bytes)
                {
                    throw new InvalidDataException("The outbox worker bundle is invalid.");
                }

                var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    share,
                    bufferSize: buffer.Length,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                streams.Add(stream);
                if (stream.Length < 0
                    || (totalBytes = checked(totalBytes + stream.Length)) > MaximumBundleBytes)
                {
                    throw new InvalidDataException("The outbox worker bundle is invalid.");
                }

                BinaryPrimitives.WriteInt32BigEndian(
                    metadata.AsSpan(0, sizeof(int)),
                    relativePathBytes.Length);
                BinaryPrimitives.WriteInt64BigEndian(
                    metadata.AsSpan(sizeof(int)),
                    stream.Length);
                bundleHash.AppendData(metadata);
                bundleHash.AppendData(relativePathBytes);

                var isExecutable = string.Equals(
                    path,
                    workerExecutablePath,
                    StringComparison.OrdinalIgnoreCase);
                executableFound |= isExecutable;
                int read;
                while ((read = await stream
                    .ReadAsync(buffer, cancellationToken)
                    .ConfigureAwait(false)) != 0)
                {
                    bundleHash.AppendData(buffer.AsSpan(0, read));
                    if (isExecutable)
                    {
                        executableHash.AppendData(buffer.AsSpan(0, read));
                    }
                }
                CryptographicOperations.ZeroMemory(relativePathBytes);
            }

            if (!executableFound)
            {
                throw new InvalidDataException("The outbox worker bundle is invalid.");
            }
            return new VerifiedWorkerBundle(
                directoryLocks,
                streams,
                bundleHash.GetHashAndReset(),
                executableHash.GetHashAndReset());
        }
        catch
        {
            foreach (var stream in streams)
            {
                stream.Dispose();
            }
            foreach (var directoryLock in directoryLocks)
            {
                directoryLock.Dispose();
            }
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            CryptographicOperations.ZeroMemory(metadata);
        }
    }

    private static IReadOnlyList<string> EnumerateBundleFiles(string root)
    {
        var files = new List<string>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var directories = 0;
        while (pending.TryPop(out var current))
        {
            if (++directories > MaximumBundleFiles)
            {
                throw new InvalidDataException("The outbox worker bundle is invalid.");
            }
            foreach (var entry in Directory.EnumerateFileSystemEntries(current.Path))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("The outbox worker bundle is invalid.");
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (current.Depth >= 8)
                    {
                        throw new InvalidDataException("The outbox worker bundle is invalid.");
                    }
                    pending.Push((entry, current.Depth + 1));
                    continue;
                }
                files.Add(entry);
                if (files.Count > MaximumBundleFiles)
                {
                    throw new InvalidDataException("The outbox worker bundle is invalid.");
                }
            }
        }
        return files;
    }

    private static bool IsPathInsideRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Length > 0
            && !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool HasReparsePoint(string path, string root)
    {
        var current = new FileInfo(path);
        if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return true;
        }

        for (var directory = current.Directory;
             directory is not null && IsPathInsideRoot(directory.FullName, root);
             directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }
        return (new DirectoryInfo(root).Attributes & FileAttributes.ReparsePoint) != 0;
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) =>
        left <= right ? left : right;

    private void Release(Execution execution)
    {
        lock (leasesSync)
        {
            leases.Remove(execution);
        }
        if (Volatile.Read(ref disposed) == 0
            && Volatile.Read(ref containmentCompromised) == 0)
        {
            capacity.Release();
        }
    }

    private sealed class Execution(ProcessExternalTransportOutboxExecutor owner)
        : IExternalTransportOutboxExecution
    {
        private readonly CancellationTokenSource lifetime = new();
        private readonly object sync = new();
        private Task<TransportOutboxAdapterReceipt>? dispatch;
        private Task? disposal;
        private bool released;

        public Task<TransportOutboxAdapterReceipt> DispatchAsync(
            TransportOutboxDispatchRequest request,
            TimeSpan hardTimeout,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            lock (sync)
            {
                if (released)
                {
                    throw new ObjectDisposedException(nameof(Execution));
                }
                if (dispatch is not null)
                {
                    throw new InvalidOperationException("An outbox execution lease is single-use.");
                }
                dispatch = InvokeAsync(request, hardTimeout, cancellationToken);
                return dispatch;
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (sync)
            {
                if (disposal is not null)
                {
                    return new ValueTask(disposal);
                }
                released = true;
                lifetime.Cancel();
                disposal = DisposeCoreAsync(dispatch);
                return new ValueTask(disposal);
            }
        }

        private async Task DisposeCoreAsync(Task<TransportOutboxAdapterReceipt>? active)
        {
            if (active is not null)
            {
                try
                {
                    await active.ConfigureAwait(false);
                }
                catch
                {
                    // Dispatch owns the typed outcome. Capacity is released only
                    // after native Job accounting proved containment; otherwise
                    // the executor remains permanently poisoned.
                }
            }
            lifetime.Dispose();
            owner.Release(this);
        }

        private async Task<TransportOutboxAdapterReceipt> InvokeAsync(
            TransportOutboxDispatchRequest request,
            TimeSpan hardTimeout,
            CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                lifetime.Token,
                owner.shutdown.Token,
                cancellationToken);
            var workerRequest = new ExternalTransportOutboxWorkerRequest(
                ExternalTransportOutboxWorkerProtocol.Version,
                ExternalTransportOutboxWorkerOperation.Dispatch,
                RandomNumberGenerator.GetBytes(ExternalTransportOutboxWorkerProtocol.NonceBytes),
                request.LogicalId.ToArray(),
                request.AttemptId.ToArray(),
                request.GetDedupMaterialCopy(),
                request.GetCiphertextBundleCopy(),
                request.ExpiresAt.ToUnixTimeMilliseconds());
            try
            {
                return await owner.InvokeWorkerAsync(
                        workerRequest,
                        hardTimeout,
                        linked.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(workerRequest.LogicalId!);
                CryptographicOperations.ZeroMemory(workerRequest.AttemptId!);
                CryptographicOperations.ZeroMemory(workerRequest.DedupMaterial!);
                CryptographicOperations.ZeroMemory(workerRequest.CiphertextBundle!);
            }
        }
    }

    private sealed class VerifiedWorkerBundle(
        IReadOnlyList<SafeFileHandle> lockedDirectories,
        IReadOnlyList<FileStream> lockedFiles,
        byte[] bundleHash,
        byte[] executableHash) : IDisposable
    {
        private readonly IReadOnlyList<SafeFileHandle> lockedDirectories = lockedDirectories;
        private readonly IReadOnlyList<FileStream> lockedFiles = lockedFiles;
        private readonly byte[] bundleHash = bundleHash;
        private readonly byte[] executableHash = executableHash;
        private int disposed;

        public byte[] GetBundleHashCopy() => bundleHash.ToArray();

        public byte[] GetExecutableHashCopy() => executableHash.ToArray();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }
            foreach (var stream in lockedFiles)
            {
                stream.Dispose();
            }
            foreach (var directory in lockedDirectories)
            {
                directory.Dispose();
            }
            CryptographicOperations.ZeroMemory(bundleHash);
            CryptographicOperations.ZeroMemory(executableHash);
        }
    }

    private sealed class SuspendedJobWorker : IDisposable
    {
        private const uint CreateSuspended = 0x00000004;
        private const uint CreateNoWindow = 0x08000000;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const uint StartfUseStdHandles = 0x00000100;
        private const uint HandleFlagInherit = 0x00000001;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const int JobObjectExtendedLimitInformationClass = 9;
        private const uint DeleteAccess = 0x00010000;
        private const uint FileAddFile = 0x00000002;
        private const uint FileAddSubdirectory = 0x00000004;
        private const uint FileDeleteChild = 0x00000040;
        private const uint FileWriteAttributes = 0x00000100;
        private const uint WriteDac = 0x00040000;
        private const uint WriteOwner = 0x00080000;
        private const uint GenericRead = 0x80000000;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareAll = 0x00000007;
        private const uint StillActive = 259;
        private const int JobObjectBasicAccountingInformationClass = 1;

        private readonly SafeFileHandle jobHandle;
        private readonly SafeFileHandle nativeProcessHandle;
        private readonly Stream standardInput;
        private int standardInputClosed;
        private int outputStreamsClosed;
        private int disposed;

        private SuspendedJobWorker(
            Process process,
            SafeFileHandle jobHandle,
            SafeFileHandle nativeProcessHandle,
            Stream standardInput,
            Stream standardOutput,
            Stream standardError)
        {
            Process = process;
            this.jobHandle = jobHandle;
            this.nativeProcessHandle = nativeProcessHandle;
            this.standardInput = standardInput;
            StandardOutput = standardOutput;
            StandardError = standardError;
        }

        public Process Process { get; }

        public Stream StandardInput => standardInput;

        public Stream StandardOutput { get; }

        public Stream StandardError { get; }

        public static bool CanCurrentProcessModifyDirectory(string path)
        {
            return CanOpenDirectoryForAccess(path, FileAddFile)
                || CanOpenDirectoryForAccess(path, FileAddSubdirectory)
                || CanOpenDirectoryForAccess(path, FileDeleteChild)
                || CanOpenDirectoryForAccess(path, FileWriteAttributes)
                || CanOpenDirectoryForAccess(path, DeleteAccess)
                || CanOpenDirectoryForAccess(path, WriteDac)
                || CanOpenDirectoryForAccess(path, WriteOwner);
        }

        private static bool CanOpenDirectoryForAccess(string path, uint desiredAccess)
        {
            using var handle = CreateFileW(
                path,
                desiredAccess,
                FileShareAll,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero);
            return !handle.IsInvalid;
        }

        public static IReadOnlyList<SafeFileHandle> LockParentAndRootDirectories(
            string trustedRootDirectory)
        {
            var parent = Directory.GetParent(trustedRootDirectory)?.FullName
                ?? throw new InvalidDataException("The outbox worker root has no parent.");
            var locks = new List<SafeFileHandle>(2);
            try
            {
                locks.Add(OpenDirectoryLock(parent));
                locks.Add(OpenDirectoryLock(trustedRootDirectory));
                return locks;
            }
            catch
            {
                foreach (var directoryLock in locks)
                {
                    directoryLock.Dispose();
                }
                throw;
            }
        }

        private static SafeFileHandle OpenDirectoryLock(string path)
        {
            var handle = CreateFileW(
                path,
                GenericRead,
                FileShareRead,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return handle;
        }

        public static SuspendedJobWorker Start(
            ProcessExternalTransportOutboxExecutorOptions options)
        {
            SafeFileHandle? job = null;
            SafeFileHandle? processHandle = null;
            SafeFileHandle? threadHandle = null;
            SafePipeHandle? childStdin = null;
            SafePipeHandle? parentStdin = null;
            SafePipeHandle? parentStdout = null;
            SafePipeHandle? childStdout = null;
            SafePipeHandle? parentStderr = null;
            SafePipeHandle? childStderr = null;
            Process? process = null;
            Stream? input = null;
            Stream? output = null;
            Stream? error = null;
            try
            {
                job = CreateKillOnCloseJob();
                CreateAnonymousPipe(out childStdin, out parentStdin, parentIsReadEnd: false);
                CreateAnonymousPipe(out childStdout, out parentStdout, parentIsReadEnd: true);
                CreateAnonymousPipe(out childStderr, out parentStderr, parentIsReadEnd: true);

                var startupInfo = new StartupInfoEx
                {
                    StartupInfo = new StartupInfo
                    {
                        Cb = Marshal.SizeOf<StartupInfoEx>(),
                        Flags = StartfUseStdHandles,
                        StandardInput = childStdin.DangerousGetHandle(),
                        StandardOutput = childStdout.DangerousGetHandle(),
                        StandardError = childStderr.DangerousGetHandle()
                    }
                };
                using var inheritedHandles = ProcessThreadAttributeList.Create(
                    startupInfo.StartupInfo.StandardInput,
                    startupInfo.StartupInfo.StandardOutput,
                    startupInfo.StartupInfo.StandardError);
                startupInfo.AttributeList = inheritedHandles.Pointer;
                var commandLine = BuildCommandLine(
                    options.WorkerExecutablePath,
                    options.Arguments);
                var environment = CreateRestrictedEnvironmentBlock();
                ProcessInformation processInformation;
                try
                {
                    if (!CreateProcessW(
                        options.WorkerExecutablePath,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        inheritHandles: true,
                        CreateSuspended
                            | CreateNoWindow
                            | CreateUnicodeEnvironment
                            | ExtendedStartupInfoPresent,
                        environment,
                        options.TrustedRootDirectory,
                        ref startupInfo,
                        out processInformation))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(environment);
                }

                processHandle = new SafeFileHandle(
                    processInformation.ProcessHandle,
                    ownsHandle: true);
                threadHandle = new SafeFileHandle(
                    processInformation.ThreadHandle,
                    ownsHandle: true);
                childStdin.Dispose();
                childStdin = null;
                childStdout.Dispose();
                childStdout = null;
                childStderr.Dispose();
                childStderr = null;

                if (!AssignProcessToJobObject(job, processHandle))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                process = Process.GetProcessById(unchecked((int)processInformation.ProcessId));
                input = new AnonymousPipeClientStream(PipeDirection.Out, parentStdin);
                parentStdin = null;
                output = new AnonymousPipeClientStream(PipeDirection.In, parentStdout);
                parentStdout = null;
                error = new AnonymousPipeClientStream(PipeDirection.In, parentStderr);
                parentStderr = null;

                if (ResumeThread(threadHandle) == uint.MaxValue)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                threadHandle.Dispose();
                threadHandle = null;

                var result = new SuspendedJobWorker(
                    process,
                    job,
                    processHandle,
                    input,
                    output,
                    error);
                process = null;
                job = null;
                processHandle = null;
                input = null;
                output = null;
                error = null;
                return result;
            }
            catch
            {
                if (job is not null && !job.IsInvalid)
                {
                    _ = TerminateJobObject(job, 0xDE);
                }
                else if (processHandle is not null && !processHandle.IsInvalid)
                {
                    _ = TerminateProcess(processHandle, 0xDE);
                }
                if (processHandle is not null && !processHandle.IsInvalid
                    && GetExitCodeProcess(processHandle, out var exitCode)
                    && exitCode == StillActive)
                {
                    _ = WaitForSingleObject(processHandle, 5000);
                }
                if (options.AllowWritableTrustedRootForTests)
                {
                    throw;
                }
                throw new ExternalTransportOutboxExecutionException();
            }
            finally
            {
                error?.Dispose();
                output?.Dispose();
                input?.Dispose();
                process?.Dispose();
                childStderr?.Dispose();
                parentStderr?.Dispose();
                childStdout?.Dispose();
                parentStdout?.Dispose();
                childStdin?.Dispose();
                parentStdin?.Dispose();
                threadHandle?.Dispose();
                processHandle?.Dispose();
                job?.Dispose();
            }
        }

        public void CloseStandardInput()
        {
            if (Interlocked.Exchange(ref standardInputClosed, 1) == 0)
            {
                standardInput.Dispose();
            }
        }

        public bool TerminateAndConfirm(
            TimeSpan timeout,
            bool failTerminateJobObjectForTests,
            bool failProcessKillForTests)
        {
            if (IsJobEmpty())
            {
                return true;
            }

            var jobTerminationRequested = !failTerminateJobObjectForTests
                && TerminateJobObject(jobHandle, 0xDE);
            if (!jobTerminationRequested && !failProcessKillForTests)
            {
                _ = TerminateProcess(nativeProcessHandle, 0xDE);
            }

            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                if (IsJobEmpty())
                {
                    return true;
                }
                Thread.Sleep(10);
            }
            return IsJobEmpty();
        }

        private bool IsJobEmpty()
        {
            var information = new JobObjectBasicAccountingInformation();
            return QueryInformationJobObject(
                    jobHandle,
                    JobObjectBasicAccountingInformationClass,
                    ref information,
                    (uint)Marshal.SizeOf<JobObjectBasicAccountingInformation>(),
                    out _)
                && information.ActiveProcesses == 0;
        }

        public void CloseOutputStreams()
        {
            if (Interlocked.Exchange(ref outputStreamsClosed, 1) != 0)
            {
                return;
            }
            StandardOutput.Dispose();
            StandardError.Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }
            CloseStandardInput();
            CloseOutputStreams();
            Process.Dispose();
            nativeProcessHandle.Dispose();
            jobHandle.Dispose();
        }

        private static SafeFileHandle CreateKillOnCloseJob()
        {
            var rawHandle = CreateJobObjectW(IntPtr.Zero, null);
            if (rawHandle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            var handle = new SafeFileHandle(rawHandle, ownsHandle: true);
            try
            {
                var information = new JobObjectExtendedLimitInformation
                {
                    BasicLimitInformation = new JobObjectBasicLimitInformation
                    {
                        LimitFlags = JobObjectLimitKillOnJobClose
                    }
                };
                var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
                var pointer = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(information, pointer, fDeleteOld: false);
                    if (!SetInformationJobObject(
                            handle,
                            JobObjectExtendedLimitInformationClass,
                            pointer,
                            (uint)size))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pointer);
                }
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private static void CreateAnonymousPipe(
            out SafePipeHandle childEnd,
            out SafePipeHandle parentEnd,
            bool parentIsReadEnd)
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                InheritHandle = true
            };
            if (!CreatePipe(out var read, out var write, ref attributes, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            if (parentIsReadEnd)
            {
                parentEnd = read;
                childEnd = write;
            }
            else
            {
                childEnd = read;
                parentEnd = write;
            }
            if (!SetHandleInformation(parentEnd, HandleFlagInherit, 0))
            {
                childEnd.Dispose();
                parentEnd.Dispose();
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        private static StringBuilder BuildCommandLine(
            string executablePath,
            IReadOnlyList<string> arguments)
        {
            var commandLine = new StringBuilder();
            AppendQuotedArgument(commandLine, executablePath);
            foreach (var argument in arguments)
            {
                commandLine.Append(' ');
                AppendQuotedArgument(commandLine, argument);
            }
            return commandLine;
        }

        private static void AppendQuotedArgument(StringBuilder builder, string value)
        {
            builder.Append('"');
            var backslashes = 0;
            foreach (var character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (character == '"')
                {
                    builder.Append('\\', checked(backslashes * 2 + 1));
                    builder.Append('"');
                    backslashes = 0;
                    continue;
                }
                builder.Append('\\', backslashes);
                backslashes = 0;
                builder.Append(character);
            }
            builder.Append('\\', checked(backslashes * 2));
            builder.Append('"');
        }

        private static IntPtr CreateRestrictedEnvironmentBlock()
        {
            var entries = new SortedDictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            AddEnvironmentVariable(entries, "SystemRoot");
            AddEnvironmentVariable(entries, "WINDIR");
            var block = string.Concat(
                entries.Select(static pair => $"{pair.Key}={pair.Value}\0")) + "\0";
            return Marshal.StringToHGlobalUni(block);
        }

        private static void AddEnvironmentVariable(
            IDictionary<string, string> entries,
            string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value)
                && value.IndexOf('\0') < 0)
            {
                entries[name] = value;
            }
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
        private static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            SafeFileHandle job,
            int informationClass,
            IntPtr information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(
            SafeFileHandle job,
            SafeFileHandle process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryInformationJobObject(
            SafeFileHandle job,
            int informationClass,
            ref JobObjectBasicAccountingInformation information,
            uint informationLength,
            out uint returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(SafeFileHandle thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(
            SafeFileHandle handle,
            uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetExitCodeProcess(
            SafeFileHandle process,
            out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreatePipe(
            out SafePipeHandle readPipe,
            out SafePipeHandle writePipe,
            ref SecurityAttributes pipeAttributes,
            uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetHandleInformation(
            SafePipeHandle handle,
            uint mask,
            uint flags);

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcessW(
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfoEx startupInfo,
            out ProcessInformation processInformation);

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Length;
            public IntPtr SecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)]
            public bool InheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Cb;
            public string? Reserved;
            public string? Desktop;
            public string? Title;
            public uint X;
            public uint Y;
            public uint XSize;
            public uint YSize;
            public uint XCountChars;
            public uint YCountChars;
            public uint FillAttribute;
            public uint Flags;
            public ushort ShowWindow;
            public ushort Reserved2Length;
            public IntPtr Reserved2;
            public IntPtr StandardInput;
            public IntPtr StandardOutput;
            public IntPtr StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx
        {
            public StartupInfo StartupInfo;
            public IntPtr AttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr ProcessHandle;
            public IntPtr ThreadHandle;
            public uint ProcessId;
            public uint ThreadId;
        }

        private sealed class ProcessThreadAttributeList : IDisposable
        {
            private const nuint ProcThreadAttributeHandleList = 0x00020002;
            private readonly IntPtr handles;
            private int disposed;

            private ProcessThreadAttributeList(IntPtr pointer, IntPtr handles)
            {
                Pointer = pointer;
                this.handles = handles;
            }

            public IntPtr Pointer { get; }

            public static ProcessThreadAttributeList Create(params IntPtr[] inheritedHandles)
            {
                UIntPtr requiredBytes = UIntPtr.Zero;
                _ = InitializeProcThreadAttributeList(
                    IntPtr.Zero,
                    1,
                    0,
                    ref requiredBytes);
                if (requiredBytes == UIntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                var attributeList = Marshal.AllocHGlobal(checked((int)requiredBytes.ToUInt64()));
                var handleList = IntPtr.Zero;
                var initialized = false;
                try
                {
                    handleList = Marshal.AllocHGlobal(
                        checked(inheritedHandles.Length * IntPtr.Size));
                    if (!InitializeProcThreadAttributeList(
                        attributeList,
                        1,
                        0,
                        ref requiredBytes))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    initialized = true;
                    for (var index = 0; index < inheritedHandles.Length; index++)
                    {
                        Marshal.WriteIntPtr(
                            handleList,
                            checked(index * IntPtr.Size),
                            inheritedHandles[index]);
                    }
                    if (!UpdateProcThreadAttribute(
                        attributeList,
                        0,
                        (UIntPtr)ProcThreadAttributeHandleList,
                        handleList,
                        (UIntPtr)checked(inheritedHandles.Length * IntPtr.Size),
                        IntPtr.Zero,
                        IntPtr.Zero))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    return new ProcessThreadAttributeList(attributeList, handleList);
                }
                catch
                {
                    if (initialized)
                    {
                        DeleteProcThreadAttributeList(attributeList);
                    }
                    if (handleList != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(handleList);
                    }
                    Marshal.FreeHGlobal(attributeList);
                    throw;
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                {
                    return;
                }
                DeleteProcThreadAttributeList(Pointer);
                Marshal.FreeHGlobal(handles);
                Marshal.FreeHGlobal(Pointer);
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool InitializeProcThreadAttributeList(
                IntPtr attributeList,
                int attributeCount,
                uint flags,
                ref UIntPtr size);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool UpdateProcThreadAttribute(
                IntPtr attributeList,
                uint flags,
                UIntPtr attribute,
                IntPtr value,
                UIntPtr size,
                IntPtr previousValue,
                IntPtr returnSize);

            [DllImport("kernel32.dll")]
            private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicAccountingInformation
        {
            public long TotalUserTime;
            public long TotalKernelTime;
            public long ThisPeriodTotalUserTime;
            public long ThisPeriodTotalKernelTime;
            public uint TotalPageFaultCount;
            public uint TotalProcesses;
            public uint ActiveProcesses;
            public uint TotalTerminatedProcesses;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}

public sealed class ExternalTransportOutboxExecutionException : IOException
{
    public ExternalTransportOutboxExecutionException()
        : base("The external outbox worker failed without a trusted receipt.")
    {
    }
}
