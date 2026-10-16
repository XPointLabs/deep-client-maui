using Deep.Client.Maui.Services;
using Deep.Client.Shared.Persistence;

namespace Deep.Client.Maui.Clean.Tests;

public sealed class Did2QaWorkDiagnosticsTests
{
    [Fact]
    public void WrapperPreservesBytesAndDoesNotRequireObservation()
    {
        var inner = new Protector(); var observed = Did2QaWorkDiagnostics.Observe(inner);
        Assert.Equal(new byte[] { 1, 2, 3 }, observed.Protect([1, 2, 3]));
        Assert.Equal(new byte[] { 4, 5 }, observed.Unprotect([4, 5]));
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public void NestedStagesCountActualCallsOnceAndRestoreParent()
    {
        var lines = new List<string>(); var observed = Did2QaWorkDiagnostics.Observe(new Protector());
        var total = Did2QaWorkDiagnostics.Begin("VerificationTotal", lines.Add);
        observed.Protect([1]);
        using (Did2QaWorkDiagnostics.Begin("AccountProof", lines.Add)) observed.Unprotect([2]);
        observed.Protect([3]); total.Dispose(); total.Dispose();
        observed.Unprotect([4]);
        Assert.Equal(2, lines.Count);
        AssertClosed(lines[0], "AccountProof", 0, 1);
        AssertClosed(lines[1], "VerificationTotal", 2, 1);
    }

    [Fact]
    public async Task AsyncCallsStayInTheirOwnObservation()
    {
        var observed = Did2QaWorkDiagnostics.Observe(new Protector());
        var first = Task.Run(async () => {
            var lines = new List<string>();
            using (Did2QaWorkDiagnostics.Begin("PreKeyPublication", lines.Add))
            { await Task.Yield(); observed.Protect([1]); }
            return Assert.Single(lines);
        });
        var second = Task.Run(async () => {
            var lines = new List<string>();
            using (Did2QaWorkDiagnostics.Begin("ContactPublication", lines.Add))
            { await Task.Yield(); observed.Unprotect([1]); observed.Unprotect([2]); }
            return Assert.Single(lines);
        });
        AssertClosed(await first, "PreKeyPublication", 1, 0);
        AssertClosed(await second, "ContactPublication", 0, 2);
    }

    [Fact]
    public void FailedProtectionAndThrowingObserverPreserveOriginalException()
    {
        var original = new PrivateFailure(); var lines = new List<string>();
        var observed = Did2QaWorkDiagnostics.Observe(new Protector { Failure = original });
        using (Did2QaWorkDiagnostics.Begin("OnionCustody", line => { lines.Add(line); throw new PrivateFailure(); }))
            Assert.Same(original, Assert.Throws<PrivateFailure>(() => observed.Unprotect([1])));
        AssertClosed(Assert.Single(lines), "OnionCustody", 0, 1);
    }

    [Theory]
    [InlineData("private-address")]
    [InlineData("AccountProof\nprivate")]
    public void UnknownStageNeverEntersOutput(string privateStage)
    {
        var lines = new List<string>();
        using (Did2QaWorkDiagnostics.Begin(privateStage, lines.Add)) { }
        AssertClosed(Assert.Single(lines), "Unclassified", 0, 0);
        Assert.DoesNotContain(privateStage, lines[0]);
    }

    [Fact]
    public async Task ConcurrentChildrenAreCountedWithoutPerOperationOutput()
    {
        var lines = new List<string>(); var observed = Did2QaWorkDiagnostics.Observe(new Protector());
        using (Did2QaWorkDiagnostics.Begin("VerificationTotal", lines.Add))
            await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => observed.Unprotect([1]))));
        AssertClosed(Assert.Single(lines), "VerificationTotal", 0, 64);
    }

    private static void AssertClosed(string line, string stage, int protects, int unprotects) =>
        Assert.Matches($"^stage={stage}; elapsedMs=[0-9]{{1,7}}; protects={protects}; unprotects={unprotects}; protectionMs=[0-9]{{1,7}}$", line);

    private sealed class Protector : IDeepSecretProtector
    {
        internal int Calls; internal Exception? Failure;
        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        { Interlocked.Increment(ref Calls); if (Failure is not null) throw Failure; return plaintext.ToArray(); }
        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
        { Interlocked.Increment(ref Calls); if (Failure is not null) throw Failure; return protectedBytes.ToArray(); }
    }

    private sealed class PrivateFailure : Exception
    {
        public override string Message => throw new InvalidOperationException("Do not read private messages.");
        public override string ToString() => throw new InvalidOperationException("Do not format private failures.");
    }
}
