using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Protocol.ApplicationCore;

namespace Deep.Client.Maui.Clean.Tests;

public sealed class DeepIdV2AccountViewModelTests
{
    [Fact]
    public async Task CanaryAdmissionIsExplicitAndNeverReplacesLocalAccount()
    {
        if (!SupportedProvider()) return;
        var directory = Path.Combine(Path.GetTempPath(),
            "deep-did2-admission-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var runtime = new TestRuntime(directory);
            var admission = new TestAdmission();
            var discovery = new TestContactDiscovery();
            var view = new DeepIdV2AccountViewModel(runtime, admission,
                discovery)
            {
                DisplayName = "Alice"
            };
            Assert.False(view.VerifyNetworkCommand.CanExecute(null));
            await view.CreateAccountAsync();
            var permanentId = view.Account!.PermanentId;
            Assert.True(view.VerifyNetworkCommand.CanExecute(null));

            admission.FailNext = true;
            await view.VerifyNetworkAsync();
            Assert.False(view.IsNetworkVerified);
            Assert.NotNull(view.ErrorMessage);
            Assert.Equal(permanentId, view.Account.PermanentId);
            await view.VerifyContactProofAsync("descriptor", "credential");
            Assert.False(view.IsContactProofVerified);
            Assert.Equal(0, discovery.Calls);

            await view.VerifyNetworkAsync();
            Assert.Null(view.ErrorMessage);
            Assert.True(view.IsNetworkVerified);
            Assert.Equal(2, admission.Calls);
            Assert.Equal(permanentId, view.Account.PermanentId);
            discovery.FailNext = true;
            await view.VerifyContactProofAsync("descriptor", "credential");
            Assert.False(view.IsContactProofVerified);
            Assert.NotNull(view.ErrorMessage);
            await view.VerifyContactProofAsync("descriptor", "credential");
            Assert.True(view.IsContactProofVerified);
            Assert.Equal(2, discovery.Calls);
            Assert.Equal(4, admission.Calls);
            view.InvalidateContactProof();
            Assert.False(view.IsContactProofVerified);
            admission.FailNext = true;
            await view.VerifyContactProofAsync("descriptor", "credential");
            Assert.False(view.IsNetworkVerified);
            Assert.False(view.IsContactProofVerified);
            Assert.Equal(2, discovery.Calls);
            Assert.NotNull(view.ErrorMessage);
            await view.VerifyNetworkAsync();
            Assert.True(view.IsNetworkVerified);
            discovery.Entered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            discovery.PendingResult = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var inFlight = view.VerifyContactProofAsync("descriptor", "credential");
            await discovery.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            view.InvalidateContactProof();
            discovery.PendingResult.SetResult();
            await inFlight;
            Assert.False(view.IsContactProofVerified);
            Assert.Contains("changed during", view.ErrorMessage);
            await view.RefreshAsync();
            Assert.False(view.IsNetworkVerified);
            Assert.False(view.IsContactProofVerified);
            Assert.Equal(permanentId, view.Account?.PermanentId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OneClickCreateRestartRevealAndDeleteUseOnlyDid2()
    {
        if (!SupportedProvider()) return;
        var directory = Path.Combine(Path.GetTempPath(),
            "deep-did2-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var runtime = new TestRuntime(directory);
            var first = new DeepIdV2AccountViewModel(runtime)
            {
                DisplayName = " Mr. X "
            };
            await first.RefreshAsync();
            Assert.Null(first.Account);
            Assert.False(first.HasRetainedRecoveryPhrase);
            first.DisplayName = " Mr. X ";
            Assert.True(first.CreateAccountCommand.CanExecute(null));
            await first.CreateAccountAsync();
            Assert.Null(first.ErrorMessage);
            Assert.Equal("Mr. X", first.Account?.DisplayName);
            Assert.True(first.HasRetainedRecoveryPhrase);
            Assert.False(first.IsRecoveryPhraseRevealed);
            Assert.False(first.CreateAccountCommand.CanExecute(null));
            var exactDid2 = first.Account!.PermanentId.CanonicalText;

            var restarted = new DeepIdV2AccountViewModel(runtime);
            await restarted.RefreshAsync();
            Assert.Null(restarted.ErrorMessage);
            Assert.Equal(exactDid2, restarted.Account?.PermanentId.CanonicalText);
            await restarted.RevealRecoveryPhraseAsync();
            Assert.Null(restarted.ErrorMessage);
            Assert.True(restarted.IsRecoveryPhraseRevealed);
            Assert.Equal(24,
                restarted.RevealedRecoveryPhrase.Split(' ',
                    StringSplitOptions.RemoveEmptyEntries).Length);
            restarted.HideRecoveryPhrase();
            Assert.False(restarted.IsRecoveryPhraseRevealed);

            await restarted.DeleteRecoveryPhraseAsync();
            Assert.Null(restarted.ErrorMessage);
            Assert.False(restarted.HasRetainedRecoveryPhrase);
            await restarted.RefreshAsync();
            Assert.Equal(exactDid2, restarted.Account?.PermanentId.CanonicalText);
            Assert.False(restarted.HasRetainedRecoveryPhrase);
            Assert.False(restarted.RevealRecoveryPhraseCommand.CanExecute(null));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(directory))
                File.Delete(file);
            Directory.Delete(directory);
        }
    }

    private static bool SupportedProvider() =>
        OperatingSystem.IsWindows() &&
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture is
            (System.Runtime.InteropServices.Architecture.X64 or
             System.Runtime.InteropServices.Architecture.Arm64) ||
        OperatingSystem.IsLinux() &&
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
            System.Runtime.InteropServices.Architecture.X64;

    private sealed class TestRuntime : IDeepIdV2AccountRuntimeAccessor
    {
        private readonly InMemoryDeepSecureStorage storage = new();
        private readonly DeepIdV2AccountService accounts;

        internal TestRuntime(string directory)
        {
            var network = Enumerable.Range(1, 16).Select(static value =>
                (byte)value).ToArray();
            accounts = new DeepIdV2AccountService(storage, directory,
                network, 1,
                new FrozenClock(DateTimeOffset.FromUnixTimeSeconds(1_900_000_000)),
                DeepMlDsa65CandidateVerifierFactory.OpenForCurrentProcess);
        }

        public Task<DeepIdV2AccountService> GetAccountsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(accounts);
        }

        public ValueTask DisposeAsync()
        {
            storage.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestAdmission : IDeepIdV2NetworkAdmission
    {
        internal bool FailNext { get; set; }
        internal int Calls { get; private set; }

        public Task VerifyAsync(DeepIdV2AccountService accounts,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(accounts);
            Calls++;
            if (FailNext)
            {
                FailNext = false;
                throw new IOException("Diagnostic network unavailable.");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class TestContactDiscovery : IDeepIdV2ContactDiscovery
    {
        internal bool FailNext { get; set; }
        internal int Calls { get; private set; }
        internal TaskCompletionSource? Entered { get; set; }
        internal TaskCompletionSource? PendingResult { get; set; }

        public Task VerifyAsync(DeepIdV2AccountService accounts,
            string compactDescriptor, string exactDid2Hex,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(accounts);
            Assert.Equal("descriptor", compactDescriptor);
            Assert.Equal("credential", exactDid2Hex);
            Calls++;
            Entered?.TrySetResult();
            if (FailNext)
            {
                FailNext = false;
                throw new IOException("Diagnostic peer proof unavailable.");
            }
            return PendingResult?.Task ?? Task.CompletedTask;
        }
    }
}
