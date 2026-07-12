using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.ViewModels.Tests.ViewModels;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task Logout_UsesCoordinatorBeforeMarkingTheSessionSignedOut()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        await runtime.Accounts.RegisterAsync("Alice");
        var navigation = new AuthNavigationState(runtime);
        await navigation.InitializeAsync();
        var coordinator = new RecordingLogoutCoordinator(() => runtime.Accounts.SignOutAsync());
        var viewModel = new SettingsViewModel(runtime, navigation, new TestNetworkStatusService(), coordinator);

        await viewModel.LogoutAsync();

        Assert.Equal(1, coordinator.Calls);
        Assert.False(navigation.IsAuthenticated);
        Assert.Null(await runtime.Accounts.GetActiveAccountAsync());
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task Logout_CoordinatorFailureKeepsTheAccountAuthenticatedAndSurfacesTheError()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        await runtime.Accounts.RegisterAsync("Alice");
        var navigation = new AuthNavigationState(runtime);
        await navigation.InitializeAsync();
        var coordinator = new RecordingLogoutCoordinator(() =>
            Task.FromException(new InvalidOperationException("Platform account purge failed.")));
        var viewModel = new SettingsViewModel(runtime, navigation, new TestNetworkStatusService(), coordinator);

        await viewModel.LogoutAsync();

        Assert.Equal(1, coordinator.Calls);
        Assert.True(navigation.IsAuthenticated);
        Assert.NotNull(await runtime.Accounts.GetActiveAccountAsync());
        Assert.Contains("purge failed", viewModel.ErrorMessage, StringComparison.Ordinal);
    }

    private sealed class RecordingLogoutCoordinator(Func<Task> logout) : IAccountLogoutCoordinator
    {
        public int Calls { get; private set; }

        public async Task LogoutAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            await logout();
        }
    }

    private sealed class TestNetworkStatusService : INetworkStatusService
    {
        public bool IsConnected => true;

        public string ConnectionLabel => "Connected";

        public event EventHandler? StatusChanged
        {
            add { }
            remove { }
        }
    }
}
