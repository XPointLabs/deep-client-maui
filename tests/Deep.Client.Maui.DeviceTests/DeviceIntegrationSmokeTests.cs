using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Protocol.ApplicationCore;

namespace Deep.Client.Maui.DeviceTests;

public sealed class DeviceIntegrationSmokeTests
{
    [Fact]
    public async Task DeviceTargetCreatesAndReopensDid2Account()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            "deep-did2-device-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var storage = new InMemoryDeepSecureStorage();
            var networkId = Enumerable.Range(1, 16)
                .Select(static value => (byte)value).ToArray();
            var clock = new FrozenClock(
                DateTimeOffset.FromUnixTimeSeconds(1_900_000_000));
            var accounts = new DeepIdV2AccountService(storage, directory,
                networkId, 1, clock,
                DeepMlDsa65CandidateVerifierFactory.OpenForCurrentProcess);
            await using var runtime = new TestAccountRuntimeAccessor(accounts);
            var onboarding = new DeepIdV2AccountViewModel(runtime)
            {
                DisplayName = "Device"
            };

            await onboarding.CreateAccountAsync();

            Assert.Null(onboarding.ErrorMessage);
            Assert.Equal("Device", onboarding.Account?.DisplayName);
            Assert.True(onboarding.HasRetainedRecoveryPhrase);
            var permanentId = onboarding.Account!.PermanentId.CanonicalText;

            var reopened = new DeepIdV2AccountService(storage, directory,
                networkId, 1, clock,
                DeepMlDsa65CandidateVerifierFactory.OpenForCurrentProcess);
            Assert.Equal(permanentId,
                (await reopened.GetCurrentAsync())?.PermanentId.CanonicalText);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class TestAccountRuntimeAccessor(
        DeepIdV2AccountService accounts) : IDeepIdV2AccountRuntimeAccessor
    {
        public Task<DeepIdV2AccountService> GetAccountsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(accounts);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
