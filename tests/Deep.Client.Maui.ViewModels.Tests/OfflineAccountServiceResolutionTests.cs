using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.State;
using Microsoft.Extensions.DependencyInjection;

namespace Deep.Client.Maui.ViewModels.Tests;

public sealed class OfflineAccountServiceResolutionTests
{
    [Fact]
    public async Task AuthCreateAndRestoreNeverResolvePoisonedNetworkFactory()
    {
        var networkFactoryCalls = 0;
        await using var sourceAccounts = new DeepAccountTestRuntime();
        using var sourceServices = Services(sourceAccounts, () => networkFactoryCalls++);
        var sourceNavigation = sourceServices.GetRequiredService<AuthNavigationState>();
        var welcome = sourceServices.GetRequiredService<WelcomeViewModel>();

        await sourceNavigation.InitializeAsync();
        welcome.DisplayName = "Alice";
        await welcome.PrepareAccountAsync();
        var recoveryPhrase = welcome.GeneratedRecoveryPhrase;
        welcome.RecoveryPhraseConfirmation = recoveryPhrase;
        await welcome.ConfirmAccountAsync();

        Assert.True(sourceNavigation.IsAuthenticated);
        Assert.Equal(0, networkFactoryCalls);

        await using var restoredAccounts = new DeepAccountTestRuntime();
        using var restoredServices = Services(restoredAccounts, () => networkFactoryCalls++);
        var restoredNavigation = restoredServices.GetRequiredService<AuthNavigationState>();
        var onboarding = restoredServices.GetRequiredService<OnboardingViewModel>();
        onboarding.DisplayName = "Alice";
        onboarding.RecoveryPhrase = recoveryPhrase;

        await onboarding.RestoreAsync();

        Assert.True(restoredNavigation.IsAuthenticated);
        Assert.Equal(
            DeepAccountActivationState.RestorePendingActivation,
            onboarding.Account!.ActivationState);
        Assert.Equal(0, networkFactoryCalls);
    }

    private static ServiceProvider Services(
        IDeepAccountRuntimeAccessor accountRuntime,
        Action onNetworkFactoryCall)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDeepAccountRuntimeAccessor>(accountRuntime);
        services.AddSingleton<AuthNavigationState>();
        services.AddTransient<WelcomeViewModel>();
        services.AddTransient<OnboardingViewModel>();
        services.AddSingleton<ClientRuntime>(_ =>
        {
            onNetworkFactoryCall();
            throw new InvalidOperationException("Network factory canary was touched.");
        });
        return services.BuildServiceProvider();
    }
}
