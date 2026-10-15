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
                try
                {
                    var accounts = await services.GetRequiredService<IDeepIdV2AccountRuntimeAccessor>().GetAccountsAsync(ct);
                    if (await accounts.GetCurrentAsync(ct) is null) throw new DeepIdV2AccountUnavailableException();
                    // Owned proof/history/closure/ONION connections and attempt
                    // contexts are recreated and disposed by this exact pipeline.
                    await services.GetRequiredService<IDeepIdV2NetworkAdmission>().VerifyAsync(accounts,ct);
                }
                catch (Exception error)
                {
#if DEEP_DID2_HTTPS_ADMISSION
                    // The existing bounded QA observer cannot alter scheduling,
                    // cancellation, authority or the original exception.
                    try {
                        ObserveDid2QaFailure("Did2.NetworkFailure",error);
                    } catch { }
#endif
                    throw;
                }
            },services.GetRequiredService<INetworkStatusService>(),MainThread.BeginInvokeOnMainThread));
#endif
        builder.Services.AddSingleton<DeepIdV2AccountViewModel>();
#if DEEP_DID2_HTTPS_ADMISSION
        builder.Services.AddSingleton<DeepIdV2MessagingViewModel>(services => new(
            services.GetRequiredService<IDeepIdV2AccountRuntimeAccessor>(),
            services.GetRequiredService<DeepIdV2AccountViewModel>(),
            services.GetRequiredService<IDeepIdV2ConversationRuntime>(),
            services.GetRequiredService<DeepIdV2NetworkReconnect>(),
            error => ObserveDid2QaFailure("Did2.BusinessFailure",error)));
        builder.Services.AddSingleton<IDeepIdV2ConversationRuntime>(services =>
            (DeepIdV2CanaryNetworkAdmission)services.GetRequiredService<IDeepIdV2NetworkAdmission>());
#else
        builder.Services.AddSingleton<DeepIdV2MessagingViewModel>();
#endif
        builder.Services.AddSingleton<AppShell>();
        return builder.Build();
    }

#if DEEP_DID2_HTTPS_ADMISSION
    private static void ObserveDid2QaFailure(string channel, Exception error)
    {
        if (channel is not ("Did2.BusinessFailure" or "Did2.NetworkFailure"))
            throw new ArgumentException("Unknown QA diagnostic channel.", nameof(channel));
        foreach (var location in Did2BusinessFailureDiagnostics.Capture(error))
        {
            CrashDiagnostics.LogInfo(channel,location);
#if ANDROID
            // Optimized QA need not be debuggable. Emit only the existing
            // closed code metadata, never export or read private log contents.
            global::Android.Util.Log.Info("Deep.Did2Qa",channel + " " + location);
#endif
        }
    }
#endif
}
