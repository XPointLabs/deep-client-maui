#if WINDOWS
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Deep.Client.Maui.Core.Services;
using Microsoft.Maui.Storage;
using Microsoft.Win32.SafeHandles;
#endif

namespace Deep.Client.Maui;

#if WINDOWS
internal sealed class WindowsRealityTransport : IRealityTransportRuntime
{
    private const string XrayAmd64FileName = "xray-windows-amd64.exe";
    private const string XrayArm64FileName = "xray-windows-arm64.exe";
    private const string XrayAmd64Sha256 = "49FD9EB558FBC2FCE2D1DB7716E031F30C9BC889AA4A33EFA88F85265BDF1F30";
    private const string XrayArm64Sha256 = "D12CB97F501E294D914C75D33EE80EE5A27D0CC835249228F5059A1E9D619811";
    private const int DynamicPortMinimum = 49152;
    private const int DynamicPortMaximumExclusive = 65536;
    private const int PortSelectionAttemptLimit = 128;
    private const int ProcessSelectionAttemptLimit = 6;
    private const int AddressFamilyInterNetwork = 2;
    private const int TcpStateListen = 2;
    private const uint ErrorSuccess = 0;
    private const uint ErrorInsufficientBuffer = 122;
    private const int MaximumTcpTableBytes = 16 * 1024 * 1024;

    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ListenerOwnershipTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly uint LoopbackAddress = BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes());
    private readonly object processSync = new();
    private readonly IReadOnlyList<RealitySeed> bootstrapSeeds;
    private readonly RealityStartupCoordinator startupCoordinator;
    private IReadOnlyList<RealityRouterEndpoint> routerEndpoints;
    private IReadOnlyList<RealitySeed> configuredSeeds;
    private Process? xrayProcess;
    private WindowsJobObject? xrayJob;
    private string? xrayConfigPath;
    private bool runtimeCatalogAttested;
    private int disposed;

    public WindowsRealityTransport()
    {
        var bootstrap = RealityTransportConfiguration.LoadEmbedded(typeof(WindowsRealityTransport).Assembly);
        bootstrapSeeds = bootstrap.Seeds.ToArray();
        configuredSeeds = CreateRuntimeSeeds(bootstrapSeeds);
        routerEndpoints = RealityTransportConfiguration.BuildRouterEndpoints(
            new RealityBootstrap(bootstrap.Version, configuredSeeds));
        startupCoordinator = new RealityStartupCoordinator(
            StartCoreAsync,
            ProbeListenerAsync,
            initialRetryDelay: TimeSpan.FromMilliseconds(250),
            maximumRetryDelay: TimeSpan.FromSeconds(2),
            listenerPollInterval: TimeSpan.FromMilliseconds(100),
            startupFailed: static exception => Debug.WriteLine(
                $"Windows Reality transport startup remains retryable: {exception.GetType().Name}"));
    }

    public IReadOnlyList<RealityRouterEndpoint> RouterEndpoints
    {
        get
        {
            lock (processSync)
            {
                return routerEndpoints;
            }
        }
    }

    public RealityTransportEndpointSource EndpointSource => RealityTransportEndpointSource.Embedded;

    public Task EnsureStartedAsync(CancellationToken cancellationToken = default) =>
        RunWithTimeoutAsync(
            startupCoordinator.EnsureStartedAsync,
            ReadinessTimeout,
            "Windows Reality transport startup did not complete in time.",
            cancellationToken);

    public Task WaitUntilReadyAsync(Uri? requestUri, CancellationToken cancellationToken)
    {
        var seed = RealityTransportConfiguration.FindSeedForRequest(configuredSeeds, requestUri);
        return seed is null
            ? Task.CompletedTask
            : WaitForListenerAsync(seed, cancellationToken);
    }

    public void NotifyNetworkChanged()
    {
        _ = startupCoordinator.TryInvalidateReadiness();
    }

    public void SetForeground(bool isForeground)
    {
    }

    public Task OnForegroundAsync(CancellationToken cancellationToken = default) =>
        EnsureStartedAsync(cancellationToken);

    internal static RealitySeed[] CreateRuntimeSeeds(IReadOnlyList<RealitySeed> seeds)
    {
        var reservations = new List<TcpListener>(seeds.Count);
        var runtimeSeeds = new RealitySeed[seeds.Count];
        try
        {
            for (var index = 0; index < seeds.Count; index++)
            {
                var reservation = ReserveRandomListener();
                reservations.Add(reservation);
                var endpoint = (IPEndPoint)reservation.LocalEndpoint;
                runtimeSeeds[index] = seeds[index] with { LocalPort = endpoint.Port };
            }

            return runtimeSeeds;
        }
        finally
        {
            foreach (var reservation in reservations)
            {
                reservation.Stop();
            }
        }
    }

    private static TcpListener ReserveRandomListener()
    {
        for (var attempt = 0; attempt < PortSelectionAttemptLimit; attempt++)
        {
            var port = RandomNumberGenerator.GetInt32(DynamicPortMinimum, DynamicPortMaximumExclusive);
            var listener = new TcpListener(IPAddress.Loopback, port)
            {
                ExclusiveAddressUse = true
            };
            try
            {
                listener.Start();
                return listener;
            }
            catch (SocketException)
            {
                listener.Stop();
            }
        }

        throw new InvalidOperationException("Could not reserve randomized loopback ports for the Reality transport.");
    }

    private Task StartCoreAsync(
        bool restart,
        CancellationToken cancellationToken) =>
        Task.Factory.StartNew(
            () => StartCore(restart, cancellationToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

    private void StartCore(
        bool restart,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (processSync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsRunning(xrayProcess) && !restart)
            {
                return;
            }

            StopProcessLocked();
            var executablePath = ResolveVerifiedExecutablePath();
            var mayReselectPorts = !runtimeCatalogAttested;
            var selectedSeeds = SelectBoundedCandidate(
                ProcessSelectionAttemptLimit,
                _ => mayReselectPorts ? CreateRuntimeSeeds(bootstrapSeeds) : configuredSeeds.ToArray(),
                candidate => TryStartCandidate(executablePath, candidate, cancellationToken),
                static _ => { },
                mayReselectPorts
                    ? "Windows Reality transport could not claim its loopback listeners after bounded port reselection."
                    : "Windows Reality transport could not reclaim its published loopback listeners; the route catalog remains unchanged and recovery failed closed.",
                cancellationToken);
            configuredSeeds = selectedSeeds;
            routerEndpoints = RealityTransportConfiguration.BuildRouterEndpoints(
                new RealityBootstrap(1, configuredSeeds));
            runtimeCatalogAttested = true;
        }
    }

    private bool TryStartCandidate(
        string executablePath,
        IReadOnlyList<RealitySeed> seeds,
        CancellationToken cancellationToken)
    {
        var configPath = WriteConfig(RealityTransportConfiguration.BuildXrayConfig(seeds));
        var process = CreateProcess(executablePath, configPath);
        try
        {
            process.Start();
            xrayJob ??= WindowsJobObject.CreateKillOnClose();
            xrayJob.AddProcess(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!WaitForOwnedListeners(process, seeds, cancellationToken))
            {
                TryKill(process);
                process.Dispose();
                TryDelete(configPath);
                return false;
            }

            xrayProcess = process;
            xrayConfigPath = configPath;
            return true;
        }
        catch
        {
            TryKill(process);
            process.Dispose();
            TryDelete(configPath);
            throw;
        }
    }

    private static bool WaitForOwnedListeners(
        Process process,
        IReadOnlyList<RealitySeed> seeds,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(ListenerOwnershipTimeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsRunning(process))
            {
                return false;
            }

            if (seeds.All(seed =>
                    TryGetListenerOwnerProcessId(seed.LocalPort, out var processId)
                    && processId == process.Id))
            {
                return true;
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(50));
        }

        return false;
    }

    internal static T SelectBoundedCandidate<T>(
        int attemptLimit,
        Func<int, T> createCandidate,
        Func<T, bool> tryActivate,
        Action<T> rejectCandidate,
        string failureMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptLimit, 1);
        ArgumentNullException.ThrowIfNull(createCandidate);
        ArgumentNullException.ThrowIfNull(tryActivate);
        ArgumentNullException.ThrowIfNull(rejectCandidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureMessage);
        for (var attempt = 0; attempt < attemptLimit; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = createCandidate(attempt);
            if (tryActivate(candidate))
            {
                return candidate;
            }

            rejectCandidate(candidate);
        }

        throw new InvalidOperationException(failureMessage);
    }

    private static Process CreateProcess(string executablePath, string configPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(configPath);

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        process.OutputDataReceived += static (_, _) => { };
        process.ErrorDataReceived += static (_, _) => { };
        return process;
    }

    private static string ResolveVerifiedExecutablePath()
    {
        var (fileName, expectedSha256) = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => (XrayAmd64FileName, XrayAmd64Sha256),
            Architecture.Arm64 => (XrayArm64FileName, XrayArm64Sha256),
            _ => throw new PlatformNotSupportedException(
                $"Windows Reality transport does not support {RuntimeInformation.ProcessArchitecture}.")
        };

        var executablePath = Path.Combine(AppContext.BaseDirectory, "xray", fileName);
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("The bundled Windows Reality transport was not found.", executablePath);
        }

        using var stream = new FileStream(
            executablePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        var actualHash = SHA256.HashData(stream);
        var expectedHash = Convert.FromHexString(expectedSha256);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
        {
            throw new InvalidOperationException("The bundled Windows Reality transport failed integrity validation.");
        }

        return executablePath;
    }

    private static string WriteConfig(string config)
    {
        var directory = Path.Combine(MauiProgram.ResolveAppDataDirectory(), "xray");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "client-reality-v1.json");
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, config, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private async Task WaitForListenerAsync(
        RealitySeed seed,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadinessTimeout);
        try
        {
            await startupCoordinator.WaitUntilReadyAsync(seed.LocalPort, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new InvalidOperationException(
                $"Windows Reality transport did not open the listener for router {seed.RouterId}.",
                exception);
        }
    }

    private Task<bool> ProbeListenerAsync(int localPort, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (processSync)
        {
            var process = xrayProcess;
            if (!IsRunning(process))
            {
                return Task.FromResult(false);
            }

            var belongsToXray = TryGetListenerOwnerProcessId(localPort, out var processId)
                && processId == process!.Id
                && IsRunning(process);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(belongsToXray);
        }
    }

    private static async Task RunWithTimeoutAsync(
        Func<CancellationToken, Task> operation,
        TimeSpan timeout,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);
        try
        {
            await operation(bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new InvalidOperationException(timeoutMessage, exception);
        }
    }

    internal static bool TryGetListenerOwnerProcessId(int localPort, out int processId)
    {
        processId = 0;
        var tableSize = 0;
        var table = IntPtr.Zero;
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var result = GetExtendedTcpTable(
                    table,
                    ref tableSize,
                    order: false,
                    AddressFamilyInterNetwork,
                    TcpTableClass.OwnerPidListener,
                    reserved: 0);
                if (result == ErrorSuccess)
                {
                    return TryFindListenerOwner(table, tableSize, localPort, out processId);
                }

                if (result != ErrorInsufficientBuffer
                    || tableSize <= sizeof(uint)
                    || tableSize > MaximumTcpTableBytes)
                {
                    return false;
                }

                if (table != IntPtr.Zero)
                {
                    var previousTable = table;
                    table = IntPtr.Zero;
                    Marshal.FreeHGlobal(previousTable);
                }

                table = Marshal.AllocHGlobal(tableSize);
            }

            return false;
        }
        finally
        {
            if (table != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(table);
            }
        }
    }

    private static bool TryFindListenerOwner(
        IntPtr table,
        int tableSize,
        int localPort,
        out int processId)
    {
        processId = 0;
        if (table == IntPtr.Zero || tableSize <= sizeof(uint))
        {
            return false;
        }

        var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
        var rowCount = Marshal.ReadInt32(table);
        var maximumRows = (tableSize - sizeof(uint)) / rowSize;
        if (rowCount < 0 || rowCount > maximumRows)
        {
            return false;
        }

        var rowPointer = IntPtr.Add(table, sizeof(uint));
        for (var index = 0; index < rowCount; index++)
        {
            var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPointer);
            if (row.State == TcpStateListen
                && row.LocalAddress == LoopbackAddress
                && DecodeTcpPort(row.LocalPort) == localPort
                && row.OwningProcessId <= int.MaxValue)
            {
                processId = (int)row.OwningProcessId;
                return true;
            }

            rowPointer = IntPtr.Add(rowPointer, rowSize);
        }

        return false;
    }

    private static int DecodeTcpPort(uint networkPort) =>
        unchecked((ushort)IPAddress.NetworkToHostOrder((short)networkPort));

    private static bool IsRunning(Process? process)
    {
        try
        {
            return process is not null && !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        Task.Factory.StartNew(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (processSync)
                {
                    StopProcessLocked();
                    xrayJob?.Dispose();
                    xrayJob = null;
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).WaitAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        var coordinatorDisposal = startupCoordinator.DisposeAsync().AsTask();
        var stop = StopAsync();
        try
        {
            await Task.WhenAll(coordinatorDisposal, stop)
                .WaitAsync(ShutdownTimeout)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ObserveLateCompletion(coordinatorDisposal);
            ObserveLateCompletion(stop);
        }
    }

    private static void ObserveLateCompletion(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private void StopProcessLocked()
    {
        var process = xrayProcess;
        xrayProcess = null;
        var configPath = xrayConfigPath;
        xrayConfigPath = null;
        if (process is null)
        {
            if (configPath is not null)
            {
                TryDelete(configPath);
            }
            return;
        }

        TryKill(process);
        process.Dispose();
        if (configPath is not null)
        {
            TryDelete(configPath);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2_000);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int tableSize,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        TcpTableClass tableClass,
        uint reserved);

    private enum TcpTableClass
    {
        OwnerPidListener = 3
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningProcessId;
    }

    private sealed class WindowsJobObject : IDisposable
    {
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private readonly SafeJobHandle handle;

        private WindowsJobObject(SafeJobHandle handle)
        {
            this.handle = handle;
        }

        public static WindowsJobObject CreateKillOnClose()
        {
            var handle = CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the Xray process job.");
            }

            var information = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };
            if (!SetInformationJobObject(
                    handle,
                    JobObjectInfoType.ExtendedLimitInformation,
                    ref information,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "Could not configure the Xray process job.");
            }

            return new WindowsJobObject(handle);
        }

        public void AddProcess(Process process)
        {
            if (!AssignProcessToJobObject(handle, process.Handle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not contain the Xray process.");
            }
        }

        public void Dispose() => handle.Dispose();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeJobHandle CreateJobObject(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            SafeJobHandle job,
            JobObjectInfoType informationClass,
            ref JobObjectExtendedLimitInformation information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

        private enum JobObjectInfoType
        {
            ExtendedLimitInformation = 9
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

        private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            private SafeJobHandle()
                : base(ownsHandle: true)
            {
            }

            protected override bool ReleaseHandle() => CloseHandle(handle);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool CloseHandle(IntPtr handle);
        }
    }
}
#endif
