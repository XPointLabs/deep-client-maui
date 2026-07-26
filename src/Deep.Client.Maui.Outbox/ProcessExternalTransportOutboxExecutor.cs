using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
    private readonly string[] arguments;
    private readonly ReadOnlyCollection<string> readOnlyArguments;

    public ProcessExternalTransportOutboxExecutorOptions(
        string workerExecutablePath,
        string trustedRootDirectory,
        ReadOnlySpan<byte> expectedSha256,
        TimeSpan maximumDispatchDuration,
        int maximumConcurrentExecutions = 1,
        IEnumerable<string>? arguments = null)
    {
        WorkerExecutablePath = Path.GetFullPath(
            workerExecutablePath ?? throw new ArgumentNullException(nameof(workerExecutablePath)));
        TrustedRootDirectory = Path.GetFullPath(
            trustedRootDirectory ?? throw new ArgumentNullException(nameof(trustedRootDirectory)));
        this.expectedSha256 = expectedSha256.ToArray();
        MaximumDispatchDuration = maximumDispatchDuration;
        MaximumConcurrentExecutions = maximumConcurrentExecutions;
        this.arguments = arguments?.ToArray() ?? [];
        readOnlyArguments = Array.AsReadOnly(this.arguments);
    }

    public string WorkerExecutablePath { get; }

    public string TrustedRootDirectory { get; }

    public TimeSpan MaximumDispatchDuration { get; }

    public int MaximumConcurrentExecutions { get; }

    public byte[] GetExpectedSha256Copy() => expectedSha256.ToArray();

    public IReadOnlyList<string> Arguments => readOnlyArguments;
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
    private readonly ProcessExternalTransportOutboxExecutorOptions options;
    private readonly SemaphoreSlim capacity;
    private readonly CancellationTokenSource shutdown = new();
    private readonly object leasesSync = new();
    private readonly HashSet<Execution> leases = [];
    private readonly TaskCompletionSource disposalCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposed;

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
        if (!await VerifyAttestationAsync(options, cancellationToken).ConfigureAwait(false))
        {
            return new(ExternalTransportOutboxBootstrapStatus.AttestationFailed, null);
        }

        var executor = new ProcessExternalTransportOutboxExecutor(options);
        try
        {
            await executor.InvokeWorkerAsync(
                    request: CreateProbeRequest(),
                    hardTimeout: Min(ProbeTimeout, options.MaximumDispatchDuration),
                    cancellationToken)
                .ConfigureAwait(false);
            return new(ExternalTransportOutboxBootstrapStatus.Ready, executor);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
            if (disposed != 0 || !capacity.Wait(0))
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
        CancellationToken cancellationToken)
    {
        if (hardTimeout <= TimeSpan.Zero || hardTimeout > MaximumDispatchDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(hardTimeout));
        }
        if (!await VerifyAttestationAsync(options, cancellationToken).ConfigureAwait(false))
        {
            throw new ExternalTransportOutboxExecutionException();
        }

        using var process = StartWorker(options);
        using var job = WindowsKillOnCloseJob.CreateAndAssign(process);
        var stderrDrain = DrainWithoutRetentionAsync(process.StandardError.BaseStream);
        var sessionKey = RandomNumberGenerator.GetBytes(
            ExternalTransportOutboxWorkerProtocol.SessionKeyBytes);
        try
        {
            var responseTask = ExchangeAsync(process, request, sessionKey);
            var exitTask = process.WaitForExitAsync();
            var timeoutTask = Task.Delay(hardTimeout);
            var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var winner = await Task.WhenAny(
                    responseTask,
                    exitTask,
                    timeoutTask,
                    cancellationTask)
                .ConfigureAwait(false);

            if (winner == cancellationTask)
            {
                await StopWorkerConfirmedAsync(process, job, stderrDrain).ConfigureAwait(false);
                throw new OperationCanceledException(cancellationToken);
            }
            if (winner == timeoutTask || winner == exitTask)
            {
                await StopWorkerConfirmedAsync(process, job, stderrDrain).ConfigureAwait(false);
                throw new ExternalTransportOutboxExecutionException();
            }

            ExternalTransportOutboxWorkerResponse response;
            try
            {
                response = await responseTask.ConfigureAwait(false);
            }
            catch
            {
                await StopWorkerConfirmedAsync(process, job, stderrDrain).ConfigureAwait(false);
                throw new ExternalTransportOutboxExecutionException();
            }

            await StopWorkerConfirmedAsync(process, job, stderrDrain).ConfigureAwait(false);
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
            CryptographicOperations.ZeroMemory(sessionKey);
        }
    }

    private static async Task<ExternalTransportOutboxWorkerResponse> ExchangeAsync(
        Process process,
        ExternalTransportOutboxWorkerRequest request,
        byte[] sessionKey)
    {
        await ExternalTransportOutboxWorkerProtocol.WriteRequestAsync(
                process.StandardInput.BaseStream,
                request,
                sessionKey)
            .ConfigureAwait(false);
        process.StandardInput.Close();
        return await ExternalTransportOutboxWorkerProtocol.ReadResponseAsync(
                process.StandardOutput.BaseStream,
                sessionKey)
            .ConfigureAwait(false);
    }

    private static Process StartWorker(ProcessExternalTransportOutboxExecutorOptions options)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.WorkerExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = options.TrustedRootDirectory
        };
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        var windowsDirectory = Environment.GetEnvironmentVariable("WINDIR");
        startInfo.Environment.Clear();
        if (!string.IsNullOrWhiteSpace(systemRoot))
        {
            startInfo.Environment["SystemRoot"] = systemRoot;
        }
        if (!string.IsNullOrWhiteSpace(windowsDirectory))
        {
            startInfo.Environment["WINDIR"] = windowsDirectory;
        }
        foreach (var argument in options.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            return Process.Start(startInfo) ?? throw new ExternalTransportOutboxExecutionException();
        }
        catch (ExternalTransportOutboxExecutionException)
        {
            throw;
        }
        catch
        {
            throw new ExternalTransportOutboxExecutionException();
        }
    }

    private static async Task StopWorkerConfirmedAsync(
        Process process,
        WindowsKillOnCloseJob job,
        Task stderrDrain)
    {
        try
        {
            if (!process.HasExited)
            {
                job.Terminate();
            }
        }
        catch
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // WaitForExit remains the only completion boundary below.
            }
        }

        await process.WaitForExitAsync().ConfigureAwait(false);
        try
        {
            await stderrDrain.ConfigureAwait(false);
        }
        catch
        {
            // Stderr is never retained or surfaced.
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
        try
        {
            return expectedHash.Length == 32
                && options.MaximumDispatchDuration > TimeSpan.Zero
                && options.MaximumDispatchDuration <= TimeSpan.FromMinutes(5)
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
        }
    }

    private static async Task<bool> VerifyAttestationAsync(
        ProcessExternalTransportOutboxExecutorOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!ValidateOptions(options))
            {
                return false;
            }

            var expectedHash = options.GetExpectedSha256Copy();
            await using var stream = new FileStream(
                options.WorkerExecutablePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actualHash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            try
            {
                return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expectedHash);
                CryptographicOperations.ZeroMemory(actualHash);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
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
        if (Volatile.Read(ref disposed) == 0)
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
                    // Dispatch owns the typed outcome; disposal only proves no
                    // child process remains before capacity is released.
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

    private sealed class WindowsKillOnCloseJob : IDisposable
    {
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const int JobObjectExtendedLimitInformationClass = 9;
        private readonly SafeFileHandle handle;

        private WindowsKillOnCloseJob(SafeFileHandle handle)
        {
            this.handle = handle;
        }

        public static WindowsKillOnCloseJob CreateAndAssign(Process process)
        {
            var rawHandle = CreateJobObjectW(IntPtr.Zero, null);
            if (rawHandle == IntPtr.Zero)
            {
                TryKill(process);
                throw new ExternalTransportOutboxExecutionException();
            }

            var safeHandle = new SafeFileHandle(rawHandle, ownsHandle: true);
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
                            safeHandle,
                            JobObjectExtendedLimitInformationClass,
                            pointer,
                            (uint)size)
                        || !AssignProcessToJobObject(safeHandle, process.Handle))
                    {
                        TryKill(process);
                        throw new ExternalTransportOutboxExecutionException();
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pointer);
                }

                return new WindowsKillOnCloseJob(safeHandle);
            }
            catch
            {
                safeHandle.Dispose();
                throw;
            }
        }

        public void Terminate()
        {
            if (!TerminateJobObject(handle, 0xDE))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        public void Dispose() => handle.Dispose();

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                }
            }
            catch
            {
                // The caller fails closed and never treats this as a receipt.
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
            IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

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
