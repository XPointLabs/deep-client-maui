using System.Diagnostics;
using System.Globalization;
using Deep.Client.Shared.Persistence;

namespace Deep.Client.Maui.Services;

// Diagnostic composition only. Measures work, never a trust/timeout decision.
internal static class Did2QaWorkDiagnostics
{
    private static readonly AsyncLocal<Scope?> Current = new();

    internal static Scope Begin(string stage, Action<string> report)
    {
        var scope = new Scope(ClosedStage(stage), report, Current.Value);
        Current.Value = scope;
        return scope;
    }

    internal static IDeepSecretProtector Observe(IDeepSecretProtector inner) => new ObservedProtector(inner);

    private static string ClosedStage(string stage) => stage switch
    {
        "VerificationTotal" or "BootstrapAuthority" or "DirectoryFloor" or "AccountProof" or
        "NetworkFloor" or "NetworkVerification" or "PreKeyStaging" or "OnionCustody" or
        "PreKeyPublication" or "ContactPublication" or "ContactResolution" => stage,
        _ => "Unclassified",
    };

    internal sealed class Scope : IDisposable
    {
        private readonly string stage;
        private readonly Action<string> report;
        private readonly Scope? parent;
        private readonly long started = Stopwatch.GetTimestamp();
        private long protects, unprotects, protectionTicks;
        private int disposed;

        internal Scope(string stage, Action<string> report, Scope? parent)
        { this.stage = stage; this.report = report; this.parent = parent; }

        internal void Record(bool protect, long ticks)
        {
            // The current composition nests only total + stage. Bound diagnostics
            // independently so an accidental deeper composition cannot add work.
            Scope? scope = this;
            for (var depth = 0; scope is not null && depth < 8; depth++, scope = scope.parent)
            {
                if (protect) Interlocked.Increment(ref scope.protects);
                else Interlocked.Increment(ref scope.unprotects);
                Interlocked.Add(ref scope.protectionTicks, ticks);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            if (ReferenceEquals(Current.Value, this)) Current.Value = parent;
            try
            {
                var elapsed = Math.Clamp((long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0, 3_600_000);
                var protection = Math.Clamp((long)(Interlocked.Read(ref protectionTicks) * 1000d / Stopwatch.Frequency), 0, 3_600_000);
                report(string.Create(CultureInfo.InvariantCulture,
                    $"stage={stage}; elapsedMs={elapsed}; protects={Math.Clamp(Interlocked.Read(ref protects), 0, 1_000_000)}; unprotects={Math.Clamp(Interlocked.Read(ref unprotects), 0, 1_000_000)}; protectionMs={protection}"));
            }
            catch { /* Observability cannot alter the original operation. */ }
        }
    }

    private sealed class ObservedProtector(IDeepSecretProtector inner) : IDeepSecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            var scope = Current.Value;
            var started = Stopwatch.GetTimestamp();
            try { return inner.Protect(plaintext); }
            finally { scope?.Record(true, Stopwatch.GetTimestamp() - started); }
        }

        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
        {
            var scope = Current.Value;
            var started = Stopwatch.GetTimestamp();
            try { return inner.Unprotect(protectedBytes); }
            finally { scope?.Record(false, Stopwatch.GetTimestamp() - started); }
        }
    }
}
