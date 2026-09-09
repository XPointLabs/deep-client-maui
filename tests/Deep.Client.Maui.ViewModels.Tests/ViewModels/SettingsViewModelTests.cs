using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Core.ViewModels;

namespace Deep.Client.Maui.ViewModels.Tests.ViewModels;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task Load_UsesPermanentDeepIdFromOfflineAccountRuntime()
    {
        await using var localAccounts = new DeepAccountTestRuntime();
        var created = await localAccounts.CreateAsync("Alice");
        var navigation = new AuthNavigationState(localAccounts);
        await navigation.InitializeAsync();
        var viewModel = new SettingsViewModel(
            localAccounts,
            navigation,
            new TestNetworkStatusService(),
            new RecordingLogoutCoordinator(() => Task.CompletedTask));

        await viewModel.LoadAsync();

        Assert.Equal(created.Result.Identity.Account.PermanentId.CanonicalText, viewModel.DeepId);
        Assert.StartsWith("deep1", viewModel.DeepId, StringComparison.Ordinal);
        Assert.Equal("Alice", viewModel.AccountDisplayName);
    }

    [Fact]
    public async Task Logout_UsesCoordinatorBeforeMarkingTheSessionSignedOut()
    {
        await using var localAccounts = new DeepAccountTestRuntime();
        _ = await localAccounts.CreateAsync("Alice");
        var navigation = new AuthNavigationState(localAccounts);
        await navigation.InitializeAsync();
        var coordinator = new RecordingLogoutCoordinator(() => localAccounts.ResetLocalStateAsync());
        var viewModel = new SettingsViewModel(localAccounts, navigation, new TestNetworkStatusService(), coordinator);

        await viewModel.LogoutAsync();

        Assert.Equal(1, coordinator.Calls);
        Assert.False(navigation.IsAuthenticated);
        Assert.Null(await (await localAccounts.GetAccountsAsync()).GetLocalIdentityAsync());
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task Logout_CoordinatorFailureKeepsTheAccountAuthenticatedAndSurfacesTheError()
    {
        await using var localAccounts = new DeepAccountTestRuntime();
        _ = await localAccounts.CreateAsync("Alice");
        var navigation = new AuthNavigationState(localAccounts);
        await navigation.InitializeAsync();
        var coordinator = new RecordingLogoutCoordinator(() =>
            Task.FromException(new InvalidOperationException("Platform account purge failed.")));
        var viewModel = new SettingsViewModel(localAccounts, navigation, new TestNetworkStatusService(), coordinator);

        await viewModel.LogoutAsync();

        Assert.Equal(1, coordinator.Calls);
        Assert.True(navigation.IsAuthenticated);
        Assert.NotNull(await (await localAccounts.GetAccountsAsync()).GetLocalIdentityAsync());
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
