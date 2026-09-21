using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Clean.Tests;

public sealed class WelcomeViewModelTests
{
    [Fact]
    public async Task OneClickCreateCompletesUsingOnlyLocalServices()
    {
        await using var local = new LocalAccountRuntime();
        var navigation = new AuthNavigationState(local);
        var viewModel = new WelcomeViewModel(local, navigation)
        {
            DisplayName = "Mr. X"
        };

        await viewModel.CreateAccountAsync();

        Assert.True(local.ActivationCalled);
        Assert.True(navigation.IsAuthenticated);
        Assert.Equal("Mr. X", viewModel.Account?.DisplayName);
        Assert.True(await local.Accounts.HasRetainedRecoveryPhraseAsync());
    }

    private sealed class LocalAccountRuntime : IDeepAccountRuntimeAccessor
    {
        private readonly InMemoryDeepAccountStore store = new();
        private readonly InMemoryDeepSecureStorage secureStorage = new();

        public LocalAccountRuntime()
        {
            var networkId = new byte[16];
            networkId[0] = 1;
            Accounts = new DeepAccountService(store, secureStorage, new SystemClock(), networkId);
        }

        public DeepAccountService Accounts { get; }
        public bool ActivationCalled { get; private set; }

        public Task<DeepAccountService> GetAccountsAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(Accounts);

        public Task EnsureLocalIdentityActivatedAsync(
            CancellationToken cancellationToken = default)
        {
            ActivationCalled = true;
            return Task.CompletedTask;
        }

        public Task ResetLocalStateAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async ValueTask DisposeAsync()
        {
            await store.DisposeAsync();
            secureStorage.Dispose();
        }
    }
}
