using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Deep.Protocol.DeepExtension.PrivacyRouting;

namespace Deep.Client.Maui.Services;

internal enum PlatformMonotonicScope
{
    OperatingSystemBoot = 1,
    Process = 2,
}

internal sealed record PlatformMonotonicSample(
    ReadOnlyMemory<byte> ScopeId,
    ulong ElapsedSeconds,
    PlatformMonotonicScope Scope);

internal interface IPlatformMonotonicSource
{
    PlatformMonotonicSample Read();
}

/// <summary>
/// Adapts an OS-boot monotonic source where the platform exposes one. The
/// conservative fallback is process-scoped: a restart rotates the scope ID
/// instead of pretending monotonic continuity that the host cannot prove.
/// </summary>
internal sealed class PlatformOnionMonotonicClock(IPlatformMonotonicSource source)
    : IOnionMonotonicClock
{
    private readonly IPlatformMonotonicSource source = source
        ?? throw new ArgumentNullException(nameof(source));
    private readonly object gate = new();
    private byte[]? observedScopeId;
    private ulong observedSeconds;

    public ValueTask<OnionMonotonicReading> ReadAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sample = source.Read()
            ?? throw new InvalidOperationException("The platform monotonic source returned no sample.");
        var scopeId = sample.ScopeId.Span;
        if (scopeId.Length != 16 || scopeId.IndexOfAnyExcept((byte)0) < 0)
        {
            throw new InvalidOperationException(
                "The platform monotonic source returned an invalid scope ID.");
        }

        lock (gate)
        {
            if (observedScopeId is not null
                && CryptographicOperations.FixedTimeEquals(observedScopeId, scopeId)
                && sample.ElapsedSeconds < observedSeconds)
            {
                throw new InvalidOperationException(
                    "The platform monotonic source moved backwards within one scope.");
            }

            if (observedScopeId is null
                || !CryptographicOperations.FixedTimeEquals(observedScopeId, scopeId))
            {
                if (observedScopeId is not null)
                {
                    CryptographicOperations.ZeroMemory(observedScopeId);
                }
                observedScopeId = scopeId.ToArray();
            }
            observedSeconds = sample.ElapsedSeconds;
            return ValueTask.FromResult(
                new OnionMonotonicReading(scopeId, sample.ElapsedSeconds));
        }
    }
}

internal static class PlatformMonotonicSourceFactory
{
    internal static IPlatformMonotonicSource Create()
    {
#if ANDROID
        return new AndroidBootMonotonicSource();
#elif WINDOWS
        return new WindowsBootMonotonicSource();
#else
        return new ProcessMonotonicSource();
#endif
    }
}

#if ANDROID
internal sealed class AndroidBootMonotonicSource : IPlatformMonotonicSource
{
    private const string LinuxBootIdPath = "/proc/sys/kernel/random/boot_id";
    private readonly byte[] bootId = ReadBootId();

    public PlatformMonotonicSample Read()
    {
        var elapsedMilliseconds = Android.OS.SystemClock.ElapsedRealtime();
        if (elapsedMilliseconds < 0)
        {
            throw new InvalidOperationException("Android elapsed realtime is invalid.");
        }
        return new PlatformMonotonicSample(
            bootId.ToArray(),
            checked((ulong)(elapsedMilliseconds / 1000)),
            PlatformMonotonicScope.OperatingSystemBoot);
    }

    private static byte[] ReadBootId()
    {
        var canonical = File.ReadAllText(LinuxBootIdPath).Trim();
        if (!Guid.TryParseExact(canonical, "D", out var parsed) || parsed == Guid.Empty)
        {
            throw new InvalidOperationException("Android did not expose a valid kernel boot ID.");
        }
        return parsed.ToByteArray();
    }
}
#endif

#if WINDOWS
internal sealed class WindowsBootMonotonicSource : IPlatformMonotonicSource
{
    private const int SystemBootEnvironmentInformation = 90;
    private readonly byte[] bootId = ReadBootId();

    public PlatformMonotonicSample Read()
    {
        var elapsedMilliseconds = Environment.TickCount64;
        if (elapsedMilliseconds < 0)
        {
            throw new InvalidOperationException("Windows system uptime is invalid.");
        }
        return new PlatformMonotonicSample(
            bootId.ToArray(),
            checked((ulong)(elapsedMilliseconds / 1000)),
            PlatformMonotonicScope.OperatingSystemBoot);
    }

    private static byte[] ReadBootId()
    {
        var size = Marshal.SizeOf<SystemBootEnvironment>();
        var status = NtQuerySystemInformation(
            SystemBootEnvironmentInformation,
            out var information,
            size,
            out var returnedLength);
        if (status < 0 || returnedLength < 16 || information.BootIdentifier == Guid.Empty)
        {
            throw new InvalidOperationException(
                $"Windows did not expose a valid boot identifier (NTSTATUS 0x{status:X8}).");
        }
        return information.BootIdentifier.ToByteArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemBootEnvironment
    {
        internal Guid BootIdentifier;
        internal int FirmwareType;
        internal ulong BootFlags;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int systemInformationClass,
        out SystemBootEnvironment systemInformation,
        int systemInformationLength,
        out int returnLength);
}
#endif

internal sealed class ProcessMonotonicSource : IPlatformMonotonicSource
{
    private static readonly byte[] ProcessScopeId = CreateScopeId();
    private static readonly long ProcessStartTimestamp = Stopwatch.GetTimestamp();

    public PlatformMonotonicSample Read()
    {
        var elapsed = Stopwatch.GetElapsedTime(ProcessStartTimestamp);
        if (elapsed < TimeSpan.Zero)
        {
            throw new InvalidOperationException("Process monotonic time is invalid.");
        }
        return new PlatformMonotonicSample(
            ProcessScopeId.ToArray(),
            checked((ulong)elapsed.TotalSeconds),
            PlatformMonotonicScope.Process);
    }

    private static byte[] CreateScopeId()
    {
        byte[] value;
        do
        {
            value = RandomNumberGenerator.GetBytes(16);
        }
        while (value.AsSpan().IndexOfAnyExcept((byte)0) < 0);
        return value;
    }
}
