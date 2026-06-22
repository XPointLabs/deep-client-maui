using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Platform;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.ViewModels.Tests.ViewModels;

public sealed class NotificationRegistrationViewModelTests
{
    [Fact]
    public async Task NotificationRegistrationStoresProviderToken()
    {
        var coordinator = new FakePushRegistrationCoordinator();
        var viewModel = new NotificationRegistrationViewModel(coordinator);

        await viewModel.RegisterAsync();

        Assert.Equal("test-provider", viewModel.Provider);
        Assert.Equal("token-123", viewModel.Token);
        Assert.Equal(1, coordinator.RegisterCalls);
    }

    private sealed class FakePushRegistrationCoordinator : IPushRegistrationCoordinator
    {
        public int RegisterCalls { get; private set; }

        public Task<PushRegistration?> RegisterAsync(CancellationToken cancellationToken = default)
        {
            RegisterCalls += 1;
            return Task.FromResult<PushRegistration?>(new PushRegistration("token-123", "test-provider", DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        }

        public Task UnregisterAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
