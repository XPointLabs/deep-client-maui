using Deep.Client.Maui.CleanUi;
using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.Services.ContactV1;
using Deep.Protocol.DeepExtension.PrivacyRouting;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deep.Client.Maui;

public static class MauiProgram
{
    internal const string TransportBaseUrlEnv = "DEEP_TRANSPORT_BASE_URL";
    internal const string RouterBaseUrlsEnv = "XNODE_URLS";
    internal const string StorageBaseUrlEnv = "DEEP_STORAGE_URL";
    internal const string CallSignalingBaseUrlEnv = "DEEP_CALL_SIGNALING_BASE_URL";
    internal const string FileBaseUrlEnv = "DEEP_FILE_URL";
    internal const string PushBaseUrlEnv = "DEEP_PUSH_URL";
    internal const string WindowsPushRemoteIdEnv = "DEEP_WINDOWS_PUSH_REMOTE_ID";
    internal const string RegistryBaseUrlEnv = "DEEP_REGISTRY_URL";
    internal const string StakingBackendBaseUrlEnv = "DEEP_STAKING_BACKEND_URL";
    internal const string StakingPortalBaseUrlEnv = "DEEP_STAKING_PORTAL_URL";
    private const string ReleaseRuntimeEnvFile = "deep.release.env";
    private const string WindowsReleaseRuntimeEnvFile = "deep.windows.release.env";

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        var environment = RuntimeEnvironmentOptions.FromRuntimeSettings(ResolveRuntimeSetting);
        var transportFactory = new HttpServiceTransportFactory(HttpServiceEndpointPolicy.Production);
        var clientOptions = new HttpServiceClientOptions(
            Timeout: TimeSpan.FromSeconds(15),
            ConnectTimeout: TimeSpan.FromSeconds(5),
            PooledConnectionIdleTimeout: TimeSpan.FromSeconds(15),
            PooledConnectionLifetime: TimeSpan.FromMinutes(2));
        var realityBinding = RealityTransportBindingResolver.Resolve(
            environment.RouterUrls,
            RoutedRuntimeEndpointPolicy.Production,
            RealityTransportRuntimeFactory.CreateEmbedded);

        builder.Services.AddSingleton(environment);
        builder.Services.AddSingleton(transportFactory);
        builder.Services.AddSingleton(clientOptions);
        builder.Services.AddSingleton(realityBinding.Runtime);
        builder.Services.AddSingleton<IRealityTransportRuntime>(realityBinding.Runtime);
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<DeepAccountRuntimeAccessor>(serviceProvider =>
            new DeepAccountRuntimeAccessor(
                ResolveAppDataDirectory(),
                serviceProvider.GetRequiredService<IClock>(),
                ActiveBuildNetworkId.Load,
                PlatformDeepSecureStorage.Create));
        builder.Services.AddSingleton<IDeepAccountRuntimeAccessor>(serviceProvider =>
            serviceProvider.GetRequiredService<DeepAccountRuntimeAccessor>());
        builder.Services.AddSingleton<ContactRouteAuthorityClient>(serviceProvider =>
        {
            var registryUrl = serviceProvider
                .GetRequiredService<RuntimeEnvironmentOptions>().RegistryUrl;
            if (string.IsNullOrWhiteSpace(registryUrl))
                throw new InvalidOperationException(
                    "Contact route authority requires DEEP_REGISTRY_URL.");
            var options = ContactRouteAuthorityClient.CreateTransportOptions(registryUrl);
            var transport = serviceProvider
                .GetRequiredService<HttpServiceTransportFactory>()
                .CreateRequestTransport(
                    options,
                    serviceProvider.GetRequiredService<HttpServiceClientOptions>());
            return new ContactRouteAuthorityClient(transport);
        });
        builder.Services.AddSingleton<ContactPublicationAuthorityClient>(serviceProvider =>
        {
            var registryUrl = serviceProvider
                .GetRequiredService<RuntimeEnvironmentOptions>().RegistryUrl;
            if (string.IsNullOrWhiteSpace(registryUrl))
                throw new InvalidOperationException(
                    "Contact publication authority requires DEEP_REGISTRY_URL.");
            var options = ContactPublicationAuthorityClient.CreateTransportOptions(registryUrl);
            var transport = serviceProvider
                .GetRequiredService<HttpServiceTransportFactory>()
                .CreateRequestTransport(
                    options,
                    serviceProvider.GetRequiredService<HttpServiceClientOptions>());
            return new ContactPublicationAuthorityClient(transport);
        });
        builder.Services.AddSingleton<AccountOwnedContactRouteAdvertisementAuthor>();
        builder.Services.AddSingleton<IDeepAccountDirectoryAdmissionCoordinator>(serviceProvider =>
            new DeepAccountDirectoryAdmissionCoordinator(
                serviceProvider.GetRequiredService<IDeepAccountRuntimeAccessor>(),
                serviceProvider.GetRequiredService<HttpServiceTransportFactory>(),
                serviceProvider.GetRequiredService<HttpServiceClientOptions>(),
                serviceProvider.GetRequiredService<RuntimeEnvironmentOptions>().RegistryUrl));
        builder.Services.AddSingleton<IProductionContactResolveVerifiedHostCapabilitiesSource>(
            serviceProvider => new ProductionContactResolveVerifiedHostCapabilitiesSource(
                serviceProvider.GetRequiredService<DeepAccountRuntimeAccessor>(),
                serviceProvider.GetRequiredService<HttpServiceTransportFactory>(),
                serviceProvider.GetRequiredService<HttpServiceClientOptions>(),
                serviceProvider.GetRequiredService<IOnionMonotonicClock>(),
                serviceProvider.GetRequiredService<RuntimeEnvironmentOptions>().RegistryUrl,
                serviceProvider.GetRequiredService<IRealityTransportRuntime>(),
                serviceProvider.GetRequiredService<IDeepAccountDirectoryAdmissionCoordinator>()));
        builder.Services.AddProductionContactResolveRuntimePrerequisites(
            host: null,
            hostOptionsSourceFactory: serviceProvider =>
                new ProductionMailboxPrivacyRouteBootstrap(
                    serviceProvider.GetRequiredService<DeepAccountRuntimeAccessor>(),
                    serviceProvider.GetRequiredService<IProductionContactResolveVerifiedHostCapabilitiesSource>(),
                    ActiveBuildNetworkId.Load));
        builder.Services.AddSingleton<DeepContactResolveRuntimeAccessor>(serviceProvider =>
            new DeepContactResolveRuntimeAccessor(
                serviceProvider.GetRequiredService<DeepAccountRuntimeAccessor>(),
                serviceProvider.GetRequiredService<IContactResolveRuntimePrerequisitesSource>()));
        builder.Services.AddSingleton<IDeepContactRuntimeAccessor>(serviceProvider =>
            serviceProvider.GetRequiredService<DeepContactResolveRuntimeAccessor>());
        builder.Services.AddSingleton<ProductionContactPublicationBootstrap>(serviceProvider =>
            new ProductionContactPublicationBootstrap(
                serviceProvider.GetRequiredService<DeepAccountRuntimeAccessor>(),
                serviceProvider.GetRequiredService<AccountOwnedContactRouteAdvertisementAuthor>(),
                serviceProvider.GetRequiredService<IClock>()));
        builder.Services.AddTransient<IGroupV1Composer>(serviceProvider =>
            new Services.GroupV1.AccountScopedGroupV1Composer(
                serviceProvider.GetRequiredService<DeepAccountRuntimeAccessor>(),
                serviceProvider.GetRequiredService<IContactResolveRuntimePrerequisitesSource>(),
                serviceProvider.GetRequiredService<IOnionMonotonicClock>(),
                serviceProvider.GetRequiredService<IClock>()));
        builder.Services.TryAddSingleton<INetworkStatusService, MauiConnectivityStatusService>();
        builder.Services.AddSingleton<IAccountLogoutCoordinator>(serviceProvider =>
            new CleanAccountLogoutCoordinator(
                serviceProvider.GetRequiredService<IDeepAccountRuntimeAccessor>()));
        builder.Services.AddSingleton<AuthNavigationState>();
        builder.Services.AddTransient<WelcomeViewModel>();
        builder.Services.AddTransient<OnboardingViewModel>();
        builder.Services.AddTransient<NewConversationViewModel>();
        builder.Services.AddTransient<GroupsViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddSingleton<AppShell>();

        return builder.Build();
    }

    internal static string ResolveAppDataDirectory() => AppDataPath.Resolve();

    internal static string? ResolveRuntimeSetting(string key)
    {
#if DEBUG && !DEEP_PHYSICAL_E2E
        var processValue = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(processValue))
        {
            return processValue.Trim();
        }

        var adjacent = Path.Combine(AppContext.BaseDirectory, ReleaseRuntimeEnvFile);
        if (File.Exists(adjacent) && TryReadSetting(File.ReadLines(adjacent), key, out var fileValue))
        {
            return fileValue;
        }
#endif
#if WINDOWS
        if (TryReadEmbeddedSetting(WindowsReleaseRuntimeEnvFile, key, out var windowsValue))
        {
            return windowsValue;
        }
#endif
        return TryReadEmbeddedSetting(ReleaseRuntimeEnvFile, key, out var embeddedValue)
            ? embeddedValue
            : null;
    }

    private static bool TryReadEmbeddedSetting(string resourceName, string key, out string? value)
    {
        using var stream = typeof(MauiProgram).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            value = null;
            return false;
        }
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }
        return TryReadSetting(lines, key, out value);
    }

    private static bool TryReadSetting(IEnumerable<string> lines, string key, out string? value)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            var separator = line.IndexOf('=');
            if (separator <= 0 || !string.Equals(line[..separator].Trim(), key, StringComparison.Ordinal))
            {
                continue;
            }
            value = line[(separator + 1)..].Trim();
            return value.Length > 0;
        }
        value = null;
        return false;
    }
}
