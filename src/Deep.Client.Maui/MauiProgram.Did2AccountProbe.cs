#if DEEP_DID2_ACCOUNT_PROBE
using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Services;
using Deep.Protocol.ApplicationCore;

namespace Deep.Client.Maui;

public static partial class MauiProgram
{
    private static MauiApp CreateDid2AccountProbeMauiApp()
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
#if DEEP_DID2_CANARY_ADMISSION
        builder.Services.AddSingleton<IDeepIdV2NetworkAdmission,
            DeepIdV2CanaryNetworkAdmission>();
        builder.Services.AddSingleton<IDeepIdV2ContactDiscovery>(services =>
            (DeepIdV2CanaryNetworkAdmission)services.GetRequiredService<
                IDeepIdV2NetworkAdmission>());
#endif
        builder.Services.AddSingleton<DeepIdV2AccountViewModel>();
        builder.Services.AddSingleton<AppShell>();
        return builder.Build();
    }
}
#endif
