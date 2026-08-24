using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.ViewModels.Tests.Navigation;

public sealed class AuthNavigationStateTests
{
    [Fact]
    public async Task MissingActiveAccountOpensOnboarding()
    {
        using var runtime = ClientRuntime.CreateStubbed();
        var navigation = new AuthNavigationState(runtime);

        await navigation.InitializeAsync();

        Assert.False(navigation.IsAuthenticated);
        Assert.True(navigation.IsInitialized);
    }

    [Fact]
    public async Task CanonicalCredentialBoundToActiveAccountAuthenticates()
    {
        using var runtime = ClientRuntime.CreateStubbed();
        await runtime.Accounts.RegisterAsync("Alice");
        var navigation = new AuthNavigationState(runtime);

        await navigation.InitializeAsync();

        Assert.True(navigation.IsAuthenticated);
        Assert.True(navigation.IsInitialized);
    }

    [Fact]
    public async Task ActiveAccountWithoutSecureCredentialRequiresExplicitReset()
    {
        using var runtime = ClientRuntime.CreateStubbed();
        await runtime.Accounts.RegisterAsync("Alice");
        await runtime.Store.DeleteAsync(SessionAccountService.ActiveRecoveryPhraseKey);
        var navigation = new AuthNavigationState(runtime);

        var exception = await Assert.ThrowsAsync<ProtectedIdentityResetRequiredException>(
            () => navigation.InitializeAsync());

        Assert.Equal(ProtectedIdentityResetRequiredReason.Missing, exception.Reason);
        Assert.False(navigation.IsInitialized);
    }

    [Fact]
    public async Task ActiveAccountWithLegacyTwelveWordCredentialRequiresExplicitReset()
    {
        using var runtime = ClientRuntime.CreateStubbed();
        await runtime.Accounts.RegisterAsync("Alice");
        await runtime.Store.SetAsync(
            SessionAccountService.ActiveRecoveryPhraseKey,
            "amber anchor april arrow atom aurora autumn badge bamboo beacon berry blade");
        var navigation = new AuthNavigationState(runtime);

        var exception = await Assert.ThrowsAsync<ProtectedIdentityResetRequiredException>(
            () => navigation.InitializeAsync());

        Assert.Equal(ProtectedIdentityResetRequiredReason.Incompatible, exception.Reason);
        Assert.False(navigation.IsInitialized);
    }

    [Fact]
    public async Task CanonicalCredentialForAnotherAccountRequiresExplicitReset()
    {
        using var runtime = ClientRuntime.CreateStubbed();
        await runtime.Accounts.RegisterAsync("Alice");
        using var other = ClientRuntime.CreateStubbed();
        await other.Accounts.RegisterAsync("Bob");
        var otherPhrase = await other.Accounts.GetRecoveryPhraseAsync();
        await runtime.Store.SetAsync(
            SessionAccountService.ActiveRecoveryPhraseKey,
            otherPhrase);
        var navigation = new AuthNavigationState(runtime);

        var exception = await Assert.ThrowsAsync<ProtectedIdentityResetRequiredException>(
            () => navigation.InitializeAsync());

        Assert.Equal(ProtectedIdentityResetRequiredReason.AccountMismatch, exception.Reason);
        Assert.False(navigation.IsInitialized);
    }
}
