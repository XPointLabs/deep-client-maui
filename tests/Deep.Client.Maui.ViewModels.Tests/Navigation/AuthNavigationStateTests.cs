using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Shared.Domain;

namespace Deep.Client.Maui.ViewModels.Tests.Navigation;

public sealed class AuthNavigationStateTests
{
    [Fact]
    public async Task MissingLocalDeepAccountOpensOnboardingWithoutNetworkRuntime()
    {
        await using var accounts = new DeepAccountTestRuntime();
        var navigation = new AuthNavigationState(accounts);

        await navigation.InitializeAsync();

        Assert.False(navigation.IsAuthenticated);
        Assert.True(navigation.IsInitialized);
        Assert.Null(navigation.Account);
        Assert.Equal(1, accounts.AccessCalls);
    }

    [Fact]
    public async Task CommittedLocalDeepAccountAuthenticatesFromDurableIdentity()
    {
        await using var accounts = new DeepAccountTestRuntime();
        var created = await accounts.CreateAsync();
        var navigation = new AuthNavigationState(accounts);

        await navigation.InitializeAsync();

        Assert.True(navigation.IsAuthenticated);
        Assert.True(navigation.IsInitialized);
        Assert.Equal(created.Result.Identity.Account, navigation.Account);
        Assert.Equal(DeepAccountActivationState.ActiveLocal, navigation.Account!.ActivationState);
    }

    [Fact]
    public async Task RefreshObservesAccountOnlyAfterCommit()
    {
        await using var accounts = new DeepAccountTestRuntime();
        var navigation = new AuthNavigationState(accounts);
        using var draft = accounts.Accounts.PrepareCreate("Alice");

        await navigation.InitializeAsync();

        Assert.False(navigation.IsAuthenticated);
        _ = await accounts.CreateAsync("Bob");
        await navigation.RefreshAsync();
        Assert.True(navigation.IsAuthenticated);
        Assert.Equal("Bob", navigation.Account!.DisplayName);
    }
}
