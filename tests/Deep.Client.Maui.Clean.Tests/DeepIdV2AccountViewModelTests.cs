using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Protocol.ApplicationCore;

namespace Deep.Client.Maui.Clean.Tests;

public sealed class DeepIdV2AccountViewModelTests
{
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
}
