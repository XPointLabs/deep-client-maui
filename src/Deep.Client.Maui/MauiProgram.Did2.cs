using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Services;
using Deep.Protocol.ApplicationCore;

namespace Deep.Client.Maui;

// The default client and the isolated UAT package share one DID2-only graph.
public static partial class MauiProgram
{
    private static MauiApp CreateDid2MauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<DeepIdV2AccountRuntimeAccessor>(services =>
            new DeepIdV2AccountRuntimeAccessor(
                ResolveAppDataDirectory(), ActiveBuildNetworkId.Load,
                deploymentProfileId: 1, services.GetRequiredService<IClock>(),
                DeepMlDsa65CandidateVerifierFactory.OpenForCurrentProcess));
        builder.Services.AddSingleton<IDeepIdV2AccountRuntimeAccessor>(services =>
            services.GetRequiredService<DeepIdV2AccountRuntimeAccessor>());
#if DEEP_DID2_CANARY_ADMISSION || DEEP_DID2_HTTPS_ADMISSION
        builder.Services.AddSingleton<IDeepIdV2NetworkAdmission,
            DeepIdV2CanaryNetworkAdmission>();
        builder.Services.AddSingleton<IDeepIdV2ContactDiscovery>(services =>
            (DeepIdV2CanaryNetworkAdmission)services.GetRequiredService<
                IDeepIdV2NetworkAdmission>());
        builder.Services.AddSingleton<INetworkStatusService,MauiConnectivityStatusService>();
        builder.Services.AddSingleton<DeepIdV2NetworkReconnect>(services => new(
            async ct => {
                var accounts = await services.GetRequiredService<IDeepIdV2AccountRuntimeAccessor>().GetAccountsAsync(ct);
                if (await accounts.GetCurrentAsync(ct) is null) throw new DeepIdV2AccountUnavailableException();
                // Owned proof/history/closure/ONION connections and attempt
                // contexts are recreated and disposed by this exact pipeline.
                await services.GetRequiredService<IDeepIdV2NetworkAdmission>().VerifyAsync(accounts,ct);
            },services.GetRequiredService<INetworkStatusService>(),MainThread.BeginInvokeOnMainThread));
#endif
        builder.Services.AddSingleton<DeepIdV2AccountViewModel>();
        builder.Services.AddSingleton<DeepIdV2MessagingViewModel>();
#if DEEP_DID2_HTTPS_ADMISSION
        builder.Services.AddSingleton<IDeepIdV2ConversationRuntime>(services =>
            (DeepIdV2CanaryNetworkAdmission)services.GetRequiredService<IDeepIdV2NetworkAdmission>());
#endif
        builder.Services.AddSingleton<AppShell>();
        return builder.Build();
    }
}
