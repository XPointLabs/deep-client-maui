using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.DeviceTests;

public sealed class DeviceIntegrationSmokeTests
{
    [Fact]
    public async Task DeviceTargetCanCreateLocalDeepAccountWithoutClientRuntime()
    {
        await using var store = new InMemoryDeepAccountStore();
        using var secureStorage = new InMemoryDeepSecureStorage();
        var service = new DeepAccountService(
            store,
            secureStorage,
            new FrozenClock(DateTimeOffset.Parse("2026-09-07T00:00:00Z")),
            Enumerable.Range(1, 16).Select(static value => (byte)value).ToArray());
        await using var accessor = new TestAccountRuntimeAccessor(service);
        var navigation = new AuthNavigationState(accessor);
        using var onboarding = new WelcomeViewModel(accessor, navigation)
        {
            DisplayName = "Device"
        };

        await onboarding.PrepareAccountAsync();
        onboarding.RecoveryPhraseConfirmation = onboarding.GeneratedRecoveryPhrase;
        await onboarding.ConfirmAccountAsync();

        Assert.NotNull(onboarding.Account);
        Assert.True(navigation.IsAuthenticated);
        Assert.NotNull(await service.GetLocalIdentityAsync());
    }

    private sealed class TestAccountRuntimeAccessor(DeepAccountService accounts) :
        IDeepAccountRuntimeAccessor
    {
        public Task<DeepAccountService> GetAccountsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(accounts);
        }

        public Task ResetLocalStateAsync(CancellationToken cancellationToken = default) =>
            accounts.ResetLocalAccountAsync(cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
