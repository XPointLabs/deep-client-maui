using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Outbox;
using Deep.Client.Maui.Pages;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Features;
using Deep.Client.Shared.Platform;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;
using Microsoft.Maui.Storage;

#if ANDROID
using Android.Content.Res;
using Android.Graphics.Drawables;
using Android.Widget;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
#endif
#if WINDOWS
using Microsoft.Maui.Handlers;
using Microsoft.UI.Xaml;
#endif

namespace Deep.Client.Maui;

internal sealed record ApplicationServiceInputs(
    ClientFeatureFlags FeatureFlags,
    IRealityTransportRuntime RealityTransportRuntime,
    RuntimeTransportMode TransportMode,
    HttpServiceTransportFactory ServiceTransportFactory,
    HttpServiceClientOptions ServiceTransportClientOptions,
    RuntimeEnvironmentOptions RuntimeEnvironment,
    Func<IServiceProvider, IIpCountryLookup> CountryLookupFactory,
    Func<IServiceProvider, IAvatarProfileTransport> AvatarTransportFactory,
    Func<IServiceProvider, IAttachmentFileTransport> AttachmentTransportFactory,
    Func<IServiceProvider, ClientRuntimeBootstrapper> RuntimeBootstrapperFactory,
    Func<IServiceProvider, ClientRuntime> RuntimeFactory,
    Func<IServiceProvider, PushClientMetadata> PushMetadataFactory,
    Func<IServiceProvider, IPushSubscriptionTransport> PushTransportFactory,
    Func<IServiceProvider, ICallSignalingTransport> CallTransportFactory,
    Func<IServiceProvider, ICallIceConfigurationProvider> IceConfigurationFactory,
    Func<IServiceProvider, DesktopWorkspaceViewModel>? DesktopWorkspaceFactory);

public sealed class DeferredApplicationServiceInputs : IAsyncDisposable
{
    private readonly Lazy<ApplicationServiceInputs> value;

    internal DeferredApplicationServiceInputs(Func<ApplicationServiceInputs> factory)
    {
        value = new Lazy<ApplicationServiceInputs>(
            factory ?? throw new ArgumentNullException(nameof(factory)),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal ApplicationServiceInputs Value => value.Value;

    internal bool IsValueCreated => value.IsValueCreated;

    public ValueTask DisposeAsync() =>
        value.IsValueCreated
            ? value.Value.RealityTransportRuntime.DisposeAsync()
            : ValueTask.CompletedTask;
}

public static class MauiProgram
{
    internal const string TransportBaseUrlEnv = "DEEP_TRANSPORT_BASE_URL";
    internal const string RouterBaseUrlsEnv = "XNODE_URLS";
    internal const string StorageBaseUrlEnv = "DEEP_STORAGE_URL";
    internal const string CallSignalingBaseUrlEnv = "DEEP_CALL_SIGNALING_BASE_URL";
    internal const string FileBaseUrlEnv = "DEEP_FILE_URL";
    internal const string FileConnectIpsEnv = "DEEP_FILE_CONNECT_IPS";
    internal const string PushBaseUrlEnv = "DEEP_PUSH_URL";
    internal const string WindowsPushRemoteIdEnv = "DEEP_WINDOWS_PUSH_REMOTE_ID";
    internal const string RegistryBaseUrlEnv = "DEEP_REGISTRY_URL";
    internal const string StakingBackendBaseUrlEnv = "DEEP_STAKING_BACKEND_URL";
    internal const string StakingPortalBaseUrlEnv = "DEEP_STAKING_PORTAL_URL";
    internal const string E2eBootstrapEnv = "DEEP_E2E_BOOTSTRAP";
    internal const string E2eAppDataRootEnv = "DEEP_E2E_APPDATA_ROOT";
    internal const string E2eStrictWindowsEnv = "DEEP_STRICT_WINDOWS_UI";
    internal const string SurvivalEnvironmentEnv = "SURVIVAL_ENV";
    internal const string PersistentTransportOutboxEnv = "DEEP_PERSISTENT_TRANSPORT_OUTBOX";
    internal const string TransportOwnershipEnv = "DEEP_TRANSPORT_OWNERSHIP";
    internal const string TransportProtocolEnv = "DEEP_TRANSPORT_PROTOCOL";
    internal const string ExternalOutboxWorkerSha256Env = "DEEP_OUTBOX_WORKER_SHA256";
    internal const string ExternalOutboxWorkerBundleSha256Env =
        "DEEP_OUTBOX_WORKER_BUNDLE_SHA256";
    private const string ExternalOutboxWorkerDirectory = "outbox-worker";
    private const string ExternalOutboxWorkerFileName = "Deep.Client.Maui.OutboxWorker.exe";
    private const string ReleaseRuntimeEnvFile = "deep.release.env";
    private const string WindowsReleaseRuntimeEnvFile = "deep.windows.release.env";

    public static MauiApp CreateMauiApp()
    {
        ValidateReleaseProcess();
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
#if ANDROID
        ConfigureAndroidHandlers();
#endif
#if WINDOWS
        ConfigureWindowsHandlers();
#endif

        ConfigureApplicationServices(builder.Services, ResolveApplicationServiceInputs);
        return builder.Build();
    }

    internal static void ConfigureApplicationServices(
        IServiceCollection services,
        ApplicationServiceInputs inputs,
        ProductionContactResolveVerifiedHostCapabilities? contactResolveCapabilities = null) =>
        ConfigureApplicationServices(services, () => inputs, contactResolveCapabilities);

    internal static void ConfigureApplicationServices(
        IServiceCollection services,
        Func<ApplicationServiceInputs> inputsFactory,
        ProductionContactResolveVerifiedHostCapabilities? contactResolveCapabilities = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(inputsFactory);
        services.AddSingleton(_ => new DeferredApplicationServiceInputs(inputsFactory));
        services.AddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<DeferredApplicationServiceInputs>().Value.RuntimeEnvironment);
        services.AddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<DeferredApplicationServiceInputs>().Value.TransportMode);
        services.AddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<DeferredApplicationServiceInputs>().Value.ServiceTransportFactory);
        services.AddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<DeferredApplicationServiceInputs>().Value.ServiceTransportClientOptions);
        services.AddSingleton<IRealityTransportRuntime>(serviceProvider => serviceProvider
            .GetRequiredService<DeferredApplicationServiceInputs>().Value.RealityTransportRuntime);
        services.AddSingleton<PrivacyMailboxRouteDiagnostics>();
#if DEBUG && DEEP_PHYSICAL_E2E
        services.AddSingleton<PrivacyMailboxRouteSelectionBridge>();
#endif
        services.AddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<DeferredApplicationServiceInputs>().Value.FeatureFlags);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<DeepAccountRuntimeAccessor>(serviceProvider =>
            new DeepAccountRuntimeAccessor(
                ResolveAppDataDirectory(),
                serviceProvider.GetRequiredService<IClock>(),
                ActiveBuildNetworkId.Load,
                PlatformDeepSecureStorage.Create));
        services.AddSingleton<IDeepAccountRuntimeAccessor>(serviceProvider =>
            serviceProvider.GetRequiredService<DeepAccountRuntimeAccessor>());
        services.AddProductionContactResolveRuntimePrerequisites(
            host: null,
            hostOptionsSourceFactory: serviceProvider =>
                new ProductionMailboxPrivacyRouteBootstrap(
                    serviceProvider.GetRequiredService<DeepAccountRuntimeAccessor>(),
                    contactResolveCapabilities,
                    ActiveBuildNetworkId.Load));
        services.AddSingleton<DeepContactResolveRuntimeAccessor>();
        services.AddSingleton<IDeepContactRuntimeAccessor>(serviceProvider =>
            serviceProvider.GetRequiredService<DeepContactResolveRuntimeAccessor>());
        services.AddTransient<IGroupV1Composer>(serviceProvider =>
            new Deep.Client.Maui.Services.GroupV1.AccountScopedGroupV1Composer(
                serviceProvider.GetRequiredService<DeepAccountRuntimeAccessor>(),
                serviceProvider.GetRequiredService<IContactResolveRuntimePrerequisitesSource>(),
                serviceProvider.GetRequiredService<Deep.Protocol.DeepExtension.PrivacyRouting.IOnionMonotonicClock>(),
                serviceProvider.GetRequiredService<IClock>()));
        services.AddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<DeferredApplicationServiceInputs>().Value.CountryLookupFactory(serviceProvider));
        services.AddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<DeferredApplicationServiceInputs>().Value.AvatarTransportFactory(serviceProvider));
        services.AddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<DeferredApplicationServiceInputs>().Value.AttachmentTransportFactory(serviceProvider));
        services.AddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<DeferredApplicationServiceInputs>().Value.RuntimeBootstrapperFactory(serviceProvider));
        services.AddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<DeferredApplicationServiceInputs>().Value.RuntimeFactory(serviceProvider));

        services.AddSingleton<IPushNotificationService, MauiPushNotificationService>();
        services.AddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<DeferredApplicationServiceInputs>().Value.PushMetadataFactory(serviceProvider));
        services.AddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<DeferredApplicationServiceInputs>().Value.PushTransportFactory(serviceProvider));
        services.AddSingleton<IPushRegistrationCoordinator, PushRegistrationCoordinator>();
        services.AddSingleton<SyncPollingPolicy>();
        services.AddSingleton<PushRegistrationLifecycleCoordinator>();
        services.AddSingleton<ChatOpenUiCache>();
        services.AddSingleton<IActiveConversationTracker, ActiveConversationTracker>();
#if DEBUG && DEEP_PHYSICAL_E2E
        services.AddSingleton<PhysicalMailboxRouteUsageTracker>();
        services.AddSingleton<IMailboxDispatchRouteUsageObserver>(serviceProvider =>
            serviceProvider.GetRequiredService<PhysicalMailboxRouteUsageTracker>());
#endif
        services.AddSingleton<IAccountLogoutCoordinator, MauiAccountLogoutCoordinator>();
        services.AddSingleton<IMediaCodecService, MauiMediaCodecService>();
        services.AddSingleton<IPermissionsService, MauiPermissionsService>();
        services.AddSingleton<IBackgroundTaskService, MauiBackgroundTaskService>();
        services.AddSingleton<IRegularBackgroundSyncScheduler, MauiRegularBackgroundSyncScheduler>();
        services.AddSingleton<BackgroundSyncSchedulingCoordinator>();
        services.AddSingleton<IShareExtensionBridge, MauiShareExtensionBridge>();
        services.AddSingleton<INotificationScheduler, MauiNotificationScheduler>();
        services.AddSingleton<IPrivacyScreenService, MauiPrivacyScreenService>();
#if ANDROID
        services.AddSingleton<IAppLockService, AndroidAppLockService>();
#elif WINDOWS
        services.AddSingleton<IAppLockService, WindowsAppLockService>();
#else
        services.AddSingleton<IAppLockService, AppLockService>();
#endif
        services.AddSingleton<IAppearanceService, MauiAppearanceService>();
        services.AddSingleton<IAppIconService, AppIconService>();
        services.AddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<DeferredApplicationServiceInputs>().Value.CallTransportFactory(serviceProvider));
        services.AddSingleton<RealtimeCallService>();
        services.AddSingleton<ICallService, MauiRealtimeCallService>();
        services.AddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<DeferredApplicationServiceInputs>().Value.IceConfigurationFactory(serviceProvider));
        services.AddSingleton<CallSessionCoordinator>();
        services.AddSingleton<IAttachmentPickerService, MauiAttachmentPickerService>();
        services.AddSingleton<IVoiceMessageRecorder, MauiVoiceMessageRecorder>();
        services.AddSingleton<INetworkStatusService, MauiConnectivityStatusService>();
        services.AddSingleton<AuthNavigationState>();
#if DEBUG && !DEEP_PHYSICAL_E2E
        services.AddSingleton<IContactMailboxOnboarding,
            SessionIdContactMailboxOnboarding>();
        services.AddSingleton<IContactInvitationProvider,
            SessionIdContactInvitationProvider>();
#else
        services.AddSingleton(serviceProvider => new ProductionMailboxRuntimeCoordinator(
            () => RequireRegistryOrigin(serviceProvider
                .GetRequiredService<DeferredApplicationServiceInputs>().Value.RuntimeEnvironment.RegistryUrl),
            FileSystem.AppDataDirectory,
            serviceProvider.GetRequiredService<HttpServiceTransportFactory>(),
            serviceProvider.GetRequiredService<HttpServiceClientOptions>()));
        services.AddSingleton<IContactMailboxOnboarding>(serviceProvider =>
            ResolveProductionMailboxOnboarding(
                serviceProvider,
                serviceProvider.GetRequiredService<DeferredApplicationServiceInputs>().Value));
        services.AddSingleton<IContactInvitationProvider>(serviceProvider =>
            ResolveProductionContactInvitationProvider(
                serviceProvider,
                serviceProvider.GetRequiredService<DeferredApplicationServiceInputs>().Value));
        services.AddSingleton<IGroupMailboxRouteExchange>(serviceProvider =>
            serviceProvider.GetRequiredService<ProductionMailboxRuntimeCoordinator>());
#endif

        services.AddTransient<OnboardingViewModel>();
        services.AddTransient<WelcomeViewModel>();
        services.AddTransient<NewConversationViewModel>();
        services.AddTransient<ConversationsViewModel>();
        services.AddTransient<ChatViewModel>();
        services.AddTransient<GroupChatViewModel>();
        services.AddTransient<GroupsViewModel>();
        services.AddTransient<AttachmentPickerViewModel>();
        services.AddTransient<NotificationRegistrationViewModel>();
        services.AddTransient<SettingsViewModel>();
#if WINDOWS
        services.AddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<DeferredApplicationServiceInputs>().Value.DesktopWorkspaceFactory!(serviceProvider));
#endif

        services.AddSingleton<AppShell>();
        services.AddTransient<WelcomePage>();
        services.AddTransient<OnboardingPage>();
        services.AddTransient<ConversationsPage>();
        services.AddTransient<NetworkUnavailablePage>();
        services.AddTransient<StartConversationPage>();
        services.AddTransient<NewConversationPage>();
        services.AddTransient<ChatPage>();
        services.AddTransient<ContactProfilePage>();
        services.AddTransient<GroupChatPage>();
        services.AddTransient<GroupsPage>();
        services.AddTransient<SettingsPage>();
        services.AddTransient<SettingsDetailPage>();
        services.AddTransient<CallPage>();
#if WINDOWS
        services.AddSingleton<DesktopWorkspacePage>();
#endif
    }

#if ANDROID
    private static void ConfigureAndroidHandlers()
    {
        ConfigureAndroidShellToolbar();
        ConfigureAndroidTextInputChrome();
    }

    private static void ConfigureAndroidShellToolbar()
    {
        ToolbarHandler.Mapper.ModifyMapping(
            nameof(IToolbar.BackButtonVisible),
            static (handler, toolbar, action) =>
            {
                try
                {
                    action?.Invoke(handler, toolbar);
                }
                catch (Resources.NotFoundException)
                {
                    // Deep renders custom in-page headers, so a missing native Shell toolbar
                    // accessibility resource should not prevent the app from starting.
                }
            });
    }

    private static void ConfigureAndroidTextInputChrome()
    {
        EntryHandler.Mapper.AppendToMapping("DeepBorderlessEntry", static (handler, _) =>
            RemoveAndroidTextInputUnderline(handler.PlatformView, handler.VirtualView));
        EditorHandler.Mapper.AppendToMapping("DeepBorderlessEditor", static (handler, _) =>
            RemoveAndroidTextInputUnderline(handler.PlatformView, handler.VirtualView));
    }

    private static void RemoveAndroidTextInputUnderline(EditText textInput, IView view)
    {
        textInput.BackgroundTintList = ColorStateList.ValueOf(Android.Graphics.Color.Transparent);
        textInput.Background = CreateAndroidTextInputBackground(view);
        textInput.SetIncludeFontPadding(false);
        textInput.SetMinHeight(0);
        textInput.SetPadding(textInput.PaddingLeft, 0, textInput.PaddingRight, 0);
    }

    private static Drawable? CreateAndroidTextInputBackground(IView view)
    {
        if (view.Background is not SolidPaint { Color: { } color } || color.Alpha <= 0)
        {
            return null;
        }

        var density = Android.App.Application.Context.Resources?.DisplayMetrics?.Density ?? 1f;
        var radius = 10 * density;
        var drawable = new GradientDrawable();
        drawable.SetShape(ShapeType.Rectangle);
        drawable.SetCornerRadius(radius);
        drawable.SetColor(color.ToPlatform());
        return drawable;
    }
#endif

#if WINDOWS
    private static void ConfigureWindowsHandlers()
    {
        static void ApplyAutomationId(IElementHandler handler, IView view)
        {
            if (handler.PlatformView is FrameworkElement element &&
                !string.IsNullOrWhiteSpace(view.AutomationId))
            {
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                    element,
                    view.AutomationId);
            }
        }

        ViewHandler.ViewMapper.AppendToMapping(
            "DeepAutomationId",
            ApplyAutomationId);
        LayoutHandler.Mapper.AppendToMapping(
            "DeepAutomationId",
            (handler, view) =>
            {
                ApplyAutomationId(handler, view);
                if (handler.PlatformView is FrameworkElement element &&
                    !string.IsNullOrWhiteSpace(view.AutomationId))
                {
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(
                        element,
                        Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Content);
                }
            });
    }
#endif

    private static void ValidateReleaseProcess()
    {
#if !DEBUG
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(E2eBootstrapEnv)) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(E2eAppDataRootEnv)) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(E2eStrictWindowsEnv)))
        {
            throw new InvalidOperationException("E2E bootstrap and app-data overrides are forbidden in Release builds.");
        }
#endif
    }

    private static ApplicationServiceInputs ResolveApplicationServiceInputs()
    {
        var survivalDevelopment = IsSurvivalDevelopmentProfile();
        var transportMode = ResolveRuntimeTransportMode();
        var directP2p = transportMode.Protocol == RuntimeTransportProtocol.DirectP2p;
        var routedEndpointPolicy = RoutedRuntimeEndpointPolicy.Production;
        var transportFactory = new HttpServiceTransportFactory(
            HttpServiceEndpointPolicy.Production);
        var featureFlags = BuildFeatureFlags(survivalDevelopment, transportMode);
        var realityBinding = directP2p
            ? new RealityTransportBinding(new UnsupportedRealityTransportRuntime(), [])
            : ResolveRealityTransportBinding(routedEndpointPolicy);
        var realityTransportRuntime = realityBinding.Runtime;
        var routerBaseUrls = realityBinding.RouterEndpoints;
        var fileBaseUrl = directP2p ? null : ResolveRuntimeSettingForComposition(
            FileBaseUrlEnv, routedEndpointPolicy);
        var pushBaseUrl = directP2p ? null : ResolveRuntimeSettingForComposition(
            PushBaseUrlEnv, routedEndpointPolicy);
        var callSignalingBaseUrl = directP2p ? null : ResolveRuntimeSettingForComposition(
            CallSignalingBaseUrlEnv, routedEndpointPolicy);
#if !DEBUG
        if (!directP2p && string.IsNullOrWhiteSpace(fileBaseUrl))
        {
            throw new InvalidOperationException(
                "DEEP_FILE_URL is required in non-Debug builds. " +
                "Remote file transports cannot be disabled for release startup.");
        }
        if (featureFlags.PushNotificationsEnabled && string.IsNullOrWhiteSpace(pushBaseUrl))
        {
            throw new InvalidOperationException(
                "DEEP_PUSH_URL is required when push notifications are enabled in non-Debug builds.");
        }
        if (featureFlags.CallsEnabled && string.IsNullOrWhiteSpace(callSignalingBaseUrl))
        {
            throw new InvalidOperationException(
                "DEEP_CALL_SIGNALING_BASE_URL is required when calls are enabled in non-Debug builds.");
        }
#endif
        var fileConnectIps = directP2p
            ? []
            : ParseIpAddresses(ResolveRuntimeSetting(FileConnectIpsEnv));
        Func<IServiceProvider, IIpCountryLookup> countryLookupFactory =
            _ => new IpCountryLookup(
                _ => Task.FromResult(OpenEmbeddedResource("geolite2_country_blocks_ipv4")),
                _ => Task.FromResult(OpenEmbeddedResource("geolite2_country_codes.json")));
        var httpTransportFactories = ApplicationHttpTransportComposition.Create(
            BindPhysicalUatTrust(
                transportFactory.WithPreferredConnectAddresses(fileConnectIps)),
            BindPhysicalUatTrust(transportFactory),
            fileBaseUrl,
            pushBaseUrl,
            callSignalingBaseUrl,
            CreateFileTransportClientOptions(),
            CreateServiceTransportClientOptions());
        Func<IServiceProvider, ClientRuntimeBootstrapper> runtimeBootstrapperFactory =
            serviceProvider => new ClientRuntimeBootstrapper(
                cancellationToken => CreateClientRuntimeAsync(
                    serviceProvider,
                    cancellationToken));
        Func<IServiceProvider, ClientRuntime> runtimeFactory =
            serviceProvider => serviceProvider
                .GetRequiredService<ClientRuntimeBootstrapper>()
                .InitializeAsync()
                .GetAwaiter()
                .GetResult();
        Func<IServiceProvider, ICallIceConfigurationProvider> iceConfigurationFactory =
            serviceProvider =>
                (ICallIceConfigurationProvider)serviceProvider
                    .GetRequiredService<ICallSignalingTransport>();
        var appVersion = AppInfo.Current.VersionString;
        Func<IServiceProvider, PushClientMetadata> pushMetadataFactory =
            _ => new PushClientMetadata(
                PushNotificationCrypto.PackageName,
                appVersion);
#if WINDOWS
        Func<IServiceProvider, DesktopWorkspaceViewModel>? desktopWorkspaceFactory =
            serviceProvider => new DesktopWorkspaceViewModel(
                serviceProvider.GetRequiredService<ClientRuntime>(),
                () => serviceProvider.GetRequiredService<ConversationsViewModel>(),
                () => serviceProvider.GetRequiredService<ChatViewModel>(),
                () => serviceProvider.GetRequiredService<GroupChatViewModel>());
#else
        Func<IServiceProvider, DesktopWorkspaceViewModel>? desktopWorkspaceFactory = null;
#endif
        return new ApplicationServiceInputs(
            featureFlags,
            realityTransportRuntime,
            transportMode,
            httpTransportFactories.ServiceTransportFactory,
            httpTransportFactories.ServiceClientOptions,
            directP2p
                ? new RuntimeEnvironmentOptions(
                    null, null, null, null, null, null, null, null, null)
                : RuntimeEnvironmentOptions.FromRuntimeSettings(
                    key => ResolveRuntimeSettingForComposition(
                        key,
                        routedEndpointPolicy)),
            countryLookupFactory,
            httpTransportFactories.Avatar,
            httpTransportFactories.Attachment,
            runtimeBootstrapperFactory,
            runtimeFactory,
            pushMetadataFactory,
            httpTransportFactories.Push,
            httpTransportFactories.Calls,
            iceConfigurationFactory,
            desktopWorkspaceFactory);
    }

    private static RuntimeTransportMode ResolveRuntimeTransportMode() =>
        RuntimeTransportMode.Parse(
            ResolveRuntimeSetting(TransportProtocolEnv),
            ResolveRuntimeSetting(TransportOwnershipEnv));

    private static bool IsSurvivalDevelopmentProfile()
    {
#if DEEP_PHYSICAL_E2E
        return string.Equals(
            ResolveRuntimeSetting(SurvivalEnvironmentEnv),
            "Development",
            StringComparison.Ordinal);
#else
        return false;
#endif
    }

    private static bool IsDebugBuild()
    {
#if DEBUG
        return true;
#else
        return false;
#endif
    }

    private static ClientFeatureFlags BuildFeatureFlags(
        bool survivalDevelopment,
        RuntimeTransportMode transportMode)
    {
#if DEBUG
        var featureFlags = ClientFeatureFlags.Defaults with
        {
            CallsEnabled = false,
            AttachmentEncryptionEnabled =
                transportMode.Protocol != RuntimeTransportProtocol.DirectP2p,
            PushNotificationsEnabled =
                transportMode.Protocol != RuntimeTransportProtocol.DirectP2p,
            PersistentTransportOutboxEnabled = IsPersistentTransportOutboxRequested(),
            TransportRequired = true,
            StubTransportAllowed = false,
            MetadataPrivateTransportRequired =
                transportMode.Protocol == RuntimeTransportProtocol.AuthenticatedMau2,
            ClientMailboxAdapterEnabled =
                transportMode.Protocol == RuntimeTransportProtocol.AuthenticatedMau2
        };
        return featureFlags;
#else
        return ClientFeatureFlags.ReleaseDefaults with
        {
            CallsEnabled = transportMode.Protocol != RuntimeTransportProtocol.DirectP2p,
            AttachmentEncryptionEnabled =
                transportMode.Protocol != RuntimeTransportProtocol.DirectP2p,
            PushNotificationsEnabled =
                transportMode.Protocol != RuntimeTransportProtocol.DirectP2p,
            PersistentTransportOutboxEnabled = IsPersistentTransportOutboxRequested(),
            MetadataPrivateTransportRequired =
                transportMode.Protocol == RuntimeTransportProtocol.AuthenticatedMau2,
            ClientMailboxAdapterEnabled =
                transportMode.Protocol == RuntimeTransportProtocol.AuthenticatedMau2
        };
#endif
    }

    private static bool IsPersistentTransportOutboxRequested() =>
        string.Equals(
            ResolveRuntimeSetting(PersistentTransportOutboxEnv),
            "1",
            StringComparison.Ordinal);

    private static void DeleteFileForWipe(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    internal static string? ResolveRuntimeSetting(string key)
    {
#if DEBUG && DEEP_PHYSICAL_E2E
        // Physical UAT is production-like: its signed build output is the authority.
        // Process variables and adjacent files must not redirect transport after signing.
        return ValidateRuntimeSetting(
            key,
            ResolveEmbeddedRuntimeSetting(ReleaseRuntimeEnvFile, key));
#else
#if DEBUG
        var value = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return ValidateRuntimeSetting(key, value);
        }

        var filePath = Path.Combine(AppContext.BaseDirectory, ReleaseRuntimeEnvFile);
        if (File.Exists(filePath))
        {
            var fileValue = ResolveRuntimeSettingFromLines(key, File.ReadLines(filePath));
            if (!string.IsNullOrWhiteSpace(fileValue))
            {
                return ValidateRuntimeSetting(key, fileValue);
            }
        }
#endif

#if WINDOWS
        var windowsValue = ResolveEmbeddedRuntimeSetting(WindowsReleaseRuntimeEnvFile, key);
        if (!string.IsNullOrWhiteSpace(windowsValue))
        {
            return ValidateRuntimeSetting(key, windowsValue);
        }
#endif

        return ValidateRuntimeSetting(key, ResolveEmbeddedRuntimeSetting(ReleaseRuntimeEnvFile, key));
#endif
    }

    private static string? ResolveEmbeddedRuntimeSetting(string resourceName, string key)
    {
        using var embeddedStream = typeof(MauiProgram).Assembly.GetManifestResourceStream(resourceName);
        if (embeddedStream is null)
        {
            return null;
        }

        using var reader = new StreamReader(embeddedStream);
        return ResolveRuntimeSettingFromLines(key, ReadLines(reader));
    }

    private static RealityTransportBinding ResolveRealityTransportBinding(
        RoutedRuntimeEndpointPolicy endpointPolicy)
    {
#if DEBUG
        if (string.Equals(
                Environment.GetEnvironmentVariable(E2eBootstrapEnv),
                "stub",
                StringComparison.OrdinalIgnoreCase))
        {
            var unsupported = new UnsupportedRealityTransportRuntime();
            return new RealityTransportBinding(unsupported, []);
        }
#endif
        return RealityTransportBindingResolver.Resolve(
            ResolveRuntimeSetting(RouterBaseUrlsEnv),
            endpointPolicy,
            RealityTransportRuntimeFactory.CreateEmbedded);
    }

    private static string? ResolveRuntimeSettingForComposition(
        string key,
        RoutedRuntimeEndpointPolicy endpointPolicy)
    {
        var value = ResolveRuntimeSetting(key);
        if (string.IsNullOrWhiteSpace(value) ||
            !IsRuntimeUrlKey(key))
        {
            return value;
        }

        return RoutedRuntimeConfiguration.RequireLiveServiceUrl(
                key,
                value,
                endpointPolicy)
            .AbsoluteUri;
    }

    private static Stream OpenEmbeddedResource(string name) =>
        typeof(MauiProgram).Assembly.GetManifestResourceStream(name)
        ?? throw new FileNotFoundException($"Embedded resource '{name}' was not found.", name);

    private static async Task<ClientRuntime> CreateClientRuntimeAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
#if DEBUG
        if (string.Equals(
                Environment.GetEnvironmentVariable(E2eBootstrapEnv),
                "stub",
                StringComparison.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = ResolveAppDataDirectory();
            var stubFeatureFlags = services.GetRequiredService<ClientFeatureFlags>() with
            {
                PersistentTransportOutboxEnabled = false,
                TransportRequired = false,
                MetadataPrivateTransportRequired = false
            };
            return ClientRuntime.CreateStubbed(
                stubFeatureFlags,
                services.GetRequiredService<IClock>(),
                avatarProfiles: services.GetRequiredService<IAvatarProfileTransport>());
        }
#endif
        var appDataDirectory = ResolveAppDataDirectory();
        var stateDbPath = Path.Combine(appDataDirectory, "client-state.db");
        var stateDbKeySlot = LocalStateDatabaseKeySlot.Active;
        var stateDbKey = await stateDbKeySlot.ResolveAsync(cancellationToken).ConfigureAwait(false);

        if (Preferences.Default.Get(StartupLocalStateReset.WipeLocalDataOnNextLaunchKey, false))
        {
            await PrelaunchPlaintextStateArtifactPurger
                .PurgeAsync(appDataDirectory, cancellationToken)
                .ConfigureAwait(false);
            DeleteFileForWipe(stateDbPath + "-wal");
            DeleteFileForWipe(stateDbPath + "-shm");
            DeleteFileForWipe(stateDbPath);
            stateDbKey = await stateDbKeySlot.ResetAsync(cancellationToken).ConfigureAwait(false);
            Preferences.Default.Remove(StartupLocalStateReset.WipeLocalDataOnNextLaunchKey);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var requestedFeatureFlags = services.GetRequiredService<ClientFeatureFlags>();
        var outboxActivation = await ExternalTransportOutboxRuntimeActivation.ResolveAsync(
                requestedFeatureFlags,
                ResolveExternalOutboxWorkerOptions(requestedFeatureFlags),
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var runtime = await PersistentClientRuntimeComposer.CreateAsync(
                stateDbPath,
                outboxActivation.EffectiveFeatureFlags,
                services.GetRequiredService<IClock>(),
                services.GetRequiredService<IAvatarProfileTransport>(),
                stateDbKey,
                (sqlite, secureStore) => CreateStoreBoundTransportComposition(
                    services,
                    sqlite,
                    secureStore,
                    appDataDirectory,
                    outboxActivation.EffectiveFeatureFlags),
                outboxActivation.Executor,
                services.GetRequiredService<DeepAccountRuntimeAccessor>(),
                services.GetService<IGroupMailboxRouteExchange>(),
                cancellationToken).ConfigureAwait(false);
            return runtime;
        }
        catch
        {
            (outboxActivation.Executor as IDisposable)?.Dispose();
            throw;
        }
    }

    private static StoreBoundRuntimeTransportComposition
        CreateStoreBoundTransportComposition(
            IServiceProvider services,
            SqliteSessionStore sqlite,
        SecureRecoverySessionStore secureStore,
        string appDataDirectory,
        ClientFeatureFlags featureFlags)
    {
#if DEBUG && !DEEP_PHYSICAL_E2E
        // Opening the local account and conversation stores must not depend on
        // bootstrap configuration or connectivity. Ordinary Debug keeps the
        // legacy transport dormant; its first network operation fails closed.
        var dormant = new StoreBoundNativeMau2Transport(
            sqlite,
            secureStore,
            new DevelopmentMailboxRuntimeProvisioningSource(),
            MailboxInfrastructureOwnership.OfficialManaged,
            featureFlags);
        return new StoreBoundRuntimeTransportComposition(dormant, dormant);
#else
        var mode = services.GetRequiredService<RuntimeTransportMode>();
        mode.Validate();
        if (mode.Protocol == RuntimeTransportProtocol.DirectP2p)
        {
            var direct = services.GetRequiredService<ISessionMessageTransport>();
            if (direct is not IDirectP2pSessionMessageTransport)
                throw new InvalidOperationException(
                    "Direct-P2P mode requires an explicit direct transport capability.");
            return new StoreBoundRuntimeTransportComposition(
                direct,
                new DirectP2pMailboxDeliveryPolicy());
        }

        var native = new StoreBoundNativeMau2Transport(
            sqlite,
            secureStore,
            services.GetRequiredService<ProductionMailboxRuntimeCoordinator>(),
            mode.Ownership,
            featureFlags,
            services.GetService<IMailboxDispatchRouteUsageObserver>());
        return new StoreBoundRuntimeTransportComposition(native, native);
#endif
    }

    private static Uri RequireRegistryOrigin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var origin) ||
            origin.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(origin.UserInfo) ||
            origin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(origin.Query) ||
            !string.IsNullOrEmpty(origin.Fragment))
            throw new InvalidOperationException(
                "DEEP_REGISTRY_URL must be a clean HTTPS origin for authenticated MAU2.");
        return origin;
    }

    private static IContactMailboxOnboarding ResolveProductionMailboxOnboarding(
        IServiceProvider services,
        ApplicationServiceInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return services.GetRequiredService<ProductionMailboxRuntimeCoordinator>();
    }

    private static IContactInvitationProvider ResolveProductionContactInvitationProvider(
        IServiceProvider services,
        ApplicationServiceInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return services.GetRequiredService<ProductionMailboxRuntimeCoordinator>();
    }

    private static ProcessExternalTransportOutboxExecutorOptions? ResolveExternalOutboxWorkerOptions(
        ClientFeatureFlags featureFlags)
    {
        if (!featureFlags.PersistentTransportOutboxEnabled)
        {
            return null;
        }
#if WINDOWS
        var rawHash = ResolveRuntimeSetting(ExternalOutboxWorkerSha256Env);
        var rawBundleHash = ResolveRuntimeSetting(ExternalOutboxWorkerBundleSha256Env);
        if (string.IsNullOrWhiteSpace(rawHash)
            || rawHash.Length != 64
            || string.IsNullOrWhiteSpace(rawBundleHash)
            || rawBundleHash.Length != 64)
        {
            return null;
        }

        byte[] expectedHash;
        byte[] expectedBundleHash;
        try
        {
            expectedHash = Convert.FromHexString(rawHash);
            expectedBundleHash = Convert.FromHexString(rawBundleHash);
        }
        catch (FormatException)
        {
            return null;
        }

        var trustedRoot = Path.Combine(AppContext.BaseDirectory, ExternalOutboxWorkerDirectory);
        return new ProcessExternalTransportOutboxExecutorOptions(
            Path.Combine(trustedRoot, ExternalOutboxWorkerFileName),
            trustedRoot,
            expectedHash,
            expectedBundleHash,
            TransportOutboxDispatcher.DefaultAttemptTimeout,
            maximumConcurrentExecutions: 1);
#else
        return null;
#endif
    }

    internal static string ResolveAppDataDirectory() => AppDataPath.Resolve();

    private static HttpServiceTransportFactory BindPhysicalUatTrust(
        HttpServiceTransportFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
#if DEBUG && DEEP_PHYSICAL_E2E && ANDROID
        return factory.WithAppScopedPrivateCertificateAuthority(
            LoadPhysicalUatRootCertificateBytes());
#else
        return factory;
#endif
    }

#if DEBUG && DEEP_PHYSICAL_E2E && ANDROID
    private const string PhysicalUatRootResource =
        "Deep.Client.Maui.PhysicalUatRootCa";
    private static byte[] LoadPhysicalUatRootCertificateBytes()
    {
        using var stream = typeof(MauiProgram).Assembly.GetManifestResourceStream(
            PhysicalUatRootResource)
            ?? throw new InvalidOperationException(
                "The physical UAT root certificate resource is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
#endif

    private static HttpServiceClientOptions CreateServiceTransportClientOptions() =>
        new(
            Timeout: TimeSpan.FromSeconds(15),
            ConnectTimeout: TimeSpan.FromSeconds(5),
            PooledConnectionIdleTimeout: TimeSpan.FromSeconds(15),
            PooledConnectionLifetime: TimeSpan.FromMinutes(2));

    private static HttpServiceClientOptions CreateFileTransportClientOptions() =>
        new(
            Timeout: TimeSpan.FromMinutes(2),
            ConnectTimeout: TimeSpan.FromSeconds(10),
            PooledConnectionIdleTimeout: TimeSpan.FromSeconds(30),
            PooledConnectionLifetime: TimeSpan.FromMinutes(5),
            UserAgent: $"Deep/{AppInfo.Current.VersionString}");

    private static IReadOnlyList<System.Net.IPAddress> ParseIpAddresses(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return SplitRuntimeValues(raw)
            .Select(static value => System.Net.IPAddress.TryParse(value, out var address) ? address : null)
            .Where(static address => address is not null)
            .Cast<System.Net.IPAddress>()
            .Distinct()
            .ToArray();
    }

    private static string? ValidateRuntimeSetting(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

#if DEBUG
        return value;
#else
        if (string.Equals(key, RouterBaseUrlsEnv, StringComparison.Ordinal))
        {
            _ = RoutedRuntimeConfiguration.ParseAtLeastThree(value);
            return value;
        }

        if (IsRuntimeUrlKey(key))
        {
            ValidateRuntimeUrl(key, value);
        }

        return value;
#endif
    }

    private static bool IsRuntimeUrlKey(string key) =>
        string.Equals(key, TransportBaseUrlEnv, StringComparison.Ordinal)
        || string.Equals(key, StorageBaseUrlEnv, StringComparison.Ordinal)
        || string.Equals(key, CallSignalingBaseUrlEnv, StringComparison.Ordinal)
        || string.Equals(key, FileBaseUrlEnv, StringComparison.Ordinal)
        || string.Equals(key, PushBaseUrlEnv, StringComparison.Ordinal)
        || string.Equals(key, RegistryBaseUrlEnv, StringComparison.Ordinal)
        || string.Equals(key, StakingBackendBaseUrlEnv, StringComparison.Ordinal)
        || string.Equals(key, StakingPortalBaseUrlEnv, StringComparison.Ordinal);

    private static void ValidateRuntimeUrl(string key, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"{key} must be an absolute URL.");
        }

        if (uri.Scheme == Uri.UriSchemeHttps || IsExplicitLoopbackHttp(uri))
        {
            return;
        }

        throw new InvalidOperationException($"{key} must use HTTPS in non-Debug builds.");
    }

    private static bool IsExplicitLoopbackHttp(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;

    private static string[] SplitRuntimeValues(string raw) =>
        raw.Split([';', ',', '\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? ResolveRuntimeSettingFromLines(string key, IEnumerable<string> lines)
    {
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var candidateKey = line[..separatorIndex].Trim();
            if (!string.Equals(candidateKey, key, StringComparison.Ordinal))
            {
                continue;
            }

            var candidateValue = line[(separatorIndex + 1)..].Trim();
            return string.IsNullOrWhiteSpace(candidateValue) ? null : candidateValue;
        }

        return null;
    }

    private static IEnumerable<string> ReadLines(TextReader reader)
    {
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

}
