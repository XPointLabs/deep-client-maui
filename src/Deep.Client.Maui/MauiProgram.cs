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
    IReadOnlyList<PinnedRouterEndpoint> RouterBaseUrls,
    IRealityTransportRuntime RealityTransportRuntime,
    RuntimeTransportMode TransportMode,
    string? StorageBaseUrl,
    HttpClient RouterHttpClient,
    RoutedSessionStorageTransportOptions RoutedTransportOptions,
    RoutedRuntimeEndpointPolicy RoutedEndpointPolicy,
    HttpServiceTransportFactory ServiceTransportFactory,
    HttpServiceClientOptions ServiceTransportClientOptions,
    DeferredVerifiedMembershipRouteCatalogProvider? MembershipRouteCatalogProvider,
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
    internal const string DevLocalMembershipTrustUrlEnv =
        "DEEP_DEV_LOCAL_MEMBERSHIP_TRUST_URL";
    internal const string DevLocalMembershipTrustSha256Env =
        "DEEP_DEV_LOCAL_MEMBERSHIP_TRUST_SHA256";
    private const string ExternalOutboxWorkerDirectory = "outbox-worker";
    private const string ExternalOutboxWorkerFileName = "Deep.Client.Maui.OutboxWorker.exe";
    private const string ReleaseRuntimeEnvFile = "deep.release.env";
    private const string WindowsReleaseRuntimeEnvFile = "deep.windows.release.env";
    private const string LocalStateDatabaseKey = "client-state.sqlcipher-key.v1";

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

        var inputs = ResolveApplicationServiceInputs();
        ConfigureApplicationServices(builder.Services, inputs);
        return builder.Build();
    }

    internal static void ConfigureApplicationServices(
        IServiceCollection services,
        ApplicationServiceInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(inputs);
        services.AddSingleton(inputs.RuntimeEnvironment);
        services.AddSingleton(inputs.TransportMode);
        services.AddSingleton<IRealityTransportRuntime>(inputs.RealityTransportRuntime);
        if (inputs.RouterBaseUrls.Count != 0)
        {
            RegisterRouteProvider(services, inputs);
        }
        else
        {
            services.AddSingleton<ITransportRouteProvider>(_ =>
                new DirectStorageRouteProvider(inputs.StorageBaseUrl));
        }

        if (inputs.TransportMode.Protocol == RuntimeTransportProtocol.DirectP2p)
        {
            services.AddSingleton<ISessionMessageTransport>(_ =>
                throw new InvalidOperationException(
                    "Direct-P2P transport is unavailable until a verified direct peer " +
                    "implementation is installed; generic HTTP endpoints are rejected."));
        }
        if (inputs.MembershipRouteCatalogProvider is not null)
            services.AddSingleton(inputs.MembershipRouteCatalogProvider);
        services.AddSingleton(inputs.FeatureFlags);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(inputs.CountryLookupFactory);
        services.AddSingleton(inputs.AvatarTransportFactory);
        services.AddSingleton(inputs.AttachmentTransportFactory);
        services.AddSingleton(inputs.RuntimeBootstrapperFactory);
        services.AddSingleton(inputs.RuntimeFactory);

        services.AddSingleton<IPushNotificationService, MauiPushNotificationService>();
        services.AddSingleton(inputs.PushMetadataFactory);
        services.AddSingleton(inputs.PushTransportFactory);
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
        services.AddSingleton(inputs.CallTransportFactory);
        services.AddSingleton<RealtimeCallService>();
        services.AddSingleton<ICallService, MauiRealtimeCallService>();
        services.AddSingleton(inputs.IceConfigurationFactory);
        services.AddSingleton<CallSessionCoordinator>();
        services.AddSingleton<IAttachmentPickerService, MauiAttachmentPickerService>();
        services.AddSingleton<IVoiceMessageRecorder, MauiVoiceMessageRecorder>();
        services.AddSingleton<INetworkStatusService, MauiConnectivityStatusService>();
        services.AddSingleton<AuthNavigationState>();

        services.AddTransient<OnboardingViewModel>();
        services.AddTransient<WelcomeViewModel>();
        services.AddTransient<ConversationsViewModel>();
        services.AddTransient<ChatViewModel>();
        services.AddTransient<GroupChatViewModel>();
        services.AddTransient<GroupsViewModel>();
        services.AddTransient<AttachmentPickerViewModel>();
        services.AddTransient<NotificationRegistrationViewModel>();
        services.AddTransient<SettingsViewModel>();
#if WINDOWS
        services.AddSingleton(inputs.DesktopWorkspaceFactory!);
#endif

        services.AddSingleton<AppShell>();
        services.AddTransient<WelcomePage>();
        services.AddTransient<OnboardingPage>();
        services.AddTransient<ConversationsPage>();
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

    private static XNodeRpcClient CreateRouterClient(
        ApplicationServiceInputs inputs)
    {
        var currentRuntimeEndpoints = inputs.RealityTransportRuntime.RouterEndpoints;
        var endpoints = currentRuntimeEndpoints.Count == 0
            ? inputs.RouterBaseUrls
            : currentRuntimeEndpoints;
        var validatedEndpoints = RoutedRuntimeConfiguration.ValidateAtLeastThree(
            endpoints,
            inputs.RoutedEndpointPolicy);
        return new XNodeRpcClient(
            inputs.RouterHttpClient,
            new XNodeRpcClientOptions(
                validatedEndpoints,
                RequireMembershipRouteSelection:
                    inputs.MembershipRouteCatalogProvider is not null),
            membershipRouteCatalogProvider: inputs.MembershipRouteCatalogProvider);
    }

    private static void RegisterRouteProvider(
        IServiceCollection services,
        ApplicationServiceInputs inputs)
    {
        services.AddSingleton(_ => CreateRouterClient(inputs));
        services.AddSingleton<ITransportRouteProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<XNodeRpcClient>());
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
        var membershipConfiguration = directP2p
            ? null
            :
            DevLocalMembershipRouteConfiguration.Resolve(
                ResolveRuntimeSetting(DevLocalMembershipTrustUrlEnv),
                ResolveRuntimeSetting(DevLocalMembershipTrustSha256Env),
                explicitDevelopmentProfile: survivalDevelopment,
                productionBuild: !IsDebugBuild());
        if (membershipConfiguration is not null && routerBaseUrls.Count == 0)
        {
            throw new InvalidOperationException(
                "Development membership routing requires configured routed transport.");
        }
        var storageBaseUrl = directP2p ? null : ResolveRuntimeSettingForComposition(
            StorageBaseUrlEnv, routedEndpointPolicy);
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
        var httpTransportFactories = ApplicationHttpTransportComposition.CreateBoundNetwork(
            transportFactory,
            CreateFileTransportNetworkHooks(fileConnectIps),
            CreateServiceTransportNetworkHooks(),
            fileBaseUrl,
            pushBaseUrl,
            callSignalingBaseUrl,
            CreateFileTransportClientOptions(),
            CreateServiceTransportClientOptions());
        Func<IServiceProvider, ClientRuntimeBootstrapper> runtimeBootstrapperFactory =
            serviceProvider => new ClientRuntimeBootstrapper(async cancellationToken =>
            {
                if (realityTransportRuntime.EndpointSource == RealityTransportEndpointSource.Embedded
                    && routerBaseUrls.Count > 0)
                {
                    await realityTransportRuntime.EnsureStartedAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                return await CreateClientRuntimeAsync(serviceProvider, cancellationToken)
                    .ConfigureAwait(false);
            });
        Func<IServiceProvider, ClientRuntime> runtimeFactory =
            serviceProvider => serviceProvider
                .GetRequiredService<ClientRuntimeBootstrapper>()
                .GetRequiredRuntime();
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
        var routerHttpClient = CreateRouterHttpClient(realityTransportRuntime);
        var membershipRouteCatalogProvider = membershipConfiguration is null
            ? null
            : new DeferredVerifiedMembershipRouteCatalogProvider(
                membershipConfiguration,
                routerHttpClient,
                new FileMembershipRouteArtifactCache(Path.Combine(
                    ResolveAppDataDirectory(),
                    "membership-route",
                    "catalog-v1.json")));
        return new ApplicationServiceInputs(
            featureFlags,
            routerBaseUrls,
            realityTransportRuntime,
            transportMode,
            storageBaseUrl,
            routerHttpClient,
            BuildRoutedTransportOptions(survivalDevelopment),
            routedEndpointPolicy,
            httpTransportFactories.ServiceTransportFactory,
            httpTransportFactories.ServiceClientOptions,
            membershipRouteCatalogProvider,
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

    private static RoutedSessionStorageTransportOptions BuildRoutedTransportOptions(
        bool survivalDevelopment) => new();

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

    private static async Task<string> ResolveLocalStateDatabaseKeyAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = await SecureStorage.GetAsync(LocalStateDatabaseKey).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }

            var key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            await SecureStorage.SetAsync(LocalStateDatabaseKey, key).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return key;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Secure local database key storage is unavailable.", ex);
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
        var stateDbKey = await ResolveLocalStateDatabaseKeyAsync(cancellationToken).ConfigureAwait(false);

        if (Preferences.Default.Get(StartupLocalStateReset.WipeLocalDataOnNextLaunchKey, false))
        {
            await PrelaunchPlaintextStateArtifactPurger
                .PurgeAsync(appDataDirectory, cancellationToken)
                .ConfigureAwait(false);
            DeleteFileForWipe(stateDbPath + "-wal");
            DeleteFileForWipe(stateDbPath + "-shm");
            DeleteFileForWipe(stateDbPath);
            SecureStorage.Remove(LocalStateDatabaseKey);
            stateDbKey = await ResolveLocalStateDatabaseKeyAsync(cancellationToken).ConfigureAwait(false);
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
            return PersistentClientRuntimeComposer.Create(
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
                services.GetService<DeferredVerifiedMembershipRouteCatalogProvider>());
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

#if DEBUG && DEEP_PHYSICAL_E2E
#if ANDROID
        const MailboxClientPlatform platform = MailboxClientPlatform.Android;
#elif WINDOWS
        const MailboxClientPlatform platform = MailboxClientPlatform.Windows;
#else
        throw new PlatformNotSupportedException(
            "DEV-local mailbox pair supports only Android and Windows.");
#endif
        var runtimeRoot = Path.Combine(
            appDataDirectory,
            MailboxRuntimeProvisioning.DirectoryName);
        var startupProvisioning = Directory.Exists(runtimeRoot)
            ? MailboxRuntimeProvisioning.LoadDevelopment(
                appDataDirectory,
                platform,
                PhysicalLabTrustRoot.MrXPublicKeySha256,
                mode.Ownership == MailboxInfrastructureOwnership.OfficialManaged
                    ? static () => false
                    : null)
            : null;
        var native = new StoreBoundNativeMau2Transport(
            sqlite,
            secureStore,
            () => (startupProvisioning ?? MailboxRuntimeProvisioning.LoadDevelopment(
                    appDataDirectory,
                    platform,
                    PhysicalLabTrustRoot.MrXPublicKeySha256,
                    mode.Ownership == MailboxInfrastructureOwnership.OfficialManaged
                        ? static () => false
                        : null))
                .ImportOptions,
            holder => DevelopmentMailboxHolderBootstrap.Publish(
                appDataDirectory,
                platform,
                holder),
            mode.Ownership,
            featureFlags,
            services.GetRequiredService<IMailboxDispatchRouteUsageObserver>(),
            CreatePhysicalUatServerCertificateValidationCallback());
        return new StoreBoundRuntimeTransportComposition(native, native);
#else
        var productionRoot = Path.Combine(
            appDataDirectory,
            "production-mailbox-runtime-v1");
        if (!ProductionMailboxBuildTrustFloor.TryLoad(out _))
            throw new InvalidOperationException("production-credentials-unavailable");
        _ = ProtectedProductionMailboxTrustStateStore.OpenOrCreate(productionRoot);
        _ = ProductionMailboxClientIdentityAttestor.AttestAsync()
            .GetAwaiter().GetResult();
        // The production registry/acquisition seam must supply the exact verified PMA1/PMR1/
        // PMT1/PMS1/MCG2 set. Never fall back to DEV bundles, raw transport, or cloud routes.
        throw new InvalidOperationException("production-credentials-unavailable");
#endif
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

    private static HttpClient CreateRouterHttpClient(IRealityTransportRuntime realityTransportRuntime)
    {
        var socketsHandler = CreateServiceHttpHandler();
        socketsHandler.UseProxy = false;
        HttpMessageHandler handler = socketsHandler;
#if ANDROID || WINDOWS
        handler = new RealityReadinessHandler(handler, realityTransportRuntime);
#endif
        return CreateServiceHttpClient(handler);
    }

    private static SocketsHttpHandler CreateServiceHttpHandler()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        handler.SslOptions.CertificateRevocationCheckMode =
            System.Security.Cryptography.X509Certificates.X509RevocationMode.Online;
        handler.SslOptions.RemoteCertificateValidationCallback =
            CreatePhysicalUatServerCertificateValidationCallback();
        return handler;
    }

    private static System.Net.Security.RemoteCertificateValidationCallback?
        CreatePhysicalUatServerCertificateValidationCallback()
    {
#if DEBUG && DEEP_PHYSICAL_E2E && ANDROID
        using (LoadPhysicalUatRootCertificate())
        {
            // Fail startup if the build lost its exact app-scoped UAT trust root.
        }
        return ValidatePhysicalUatServerCertificate;
#else
        return null;
#endif
    }

#if DEBUG && DEEP_PHYSICAL_E2E && ANDROID
    private const string PhysicalUatRootResource =
        "Deep.Client.Maui.PhysicalUatRootCa";

    private static bool ValidatePhysicalUatServerCertificate(
        object sender,
        System.Security.Cryptography.X509Certificates.X509Certificate? certificate,
        System.Security.Cryptography.X509Certificates.X509Chain? presentedChain,
        System.Net.Security.SslPolicyErrors sslPolicyErrors)
    {
        _ = sender;
        if (certificate is null ||
            (sslPolicyErrors & (System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch |
                System.Net.Security.SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
            return false;

        var platformValidationSucceeded =
            sslPolicyErrors == System.Net.Security.SslPolicyErrors.None;
        if (platformValidationSucceeded)
            return platformValidationSucceeded;
        if (sslPolicyErrors != System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors)
            return false;

        try
        {
            using var leaf = System.Security.Cryptography.X509Certificates.X509CertificateLoader
                .LoadCertificate(certificate.GetRawCertData());
            using var root = LoadPhysicalUatRootCertificate();
            using var chain = new System.Security.Cryptography.X509Certificates.X509Chain();
            var intermediates = new List<
                System.Security.Cryptography.X509Certificates.X509Certificate2>();
            try
            {
                chain.ChainPolicy.TrustMode =
                    System.Security.Cryptography.X509Certificates.X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(root);
                chain.ChainPolicy.RevocationMode =
                    System.Security.Cryptography.X509Certificates.X509RevocationMode.Online;
                chain.ChainPolicy.RevocationFlag =
                    System.Security.Cryptography.X509Certificates.X509RevocationFlag.ExcludeRoot;
                chain.ChainPolicy.VerificationFlags =
                    System.Security.Cryptography.X509Certificates.X509VerificationFlags.NoFlag;
                chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(5);
                chain.ChainPolicy.ApplicationPolicy.Add(
                    new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
                if (presentedChain is not null)
                {
                    foreach (var element in presentedChain.ChainElements)
                    {
                        if (element.Certificate.RawData.AsSpan().SequenceEqual(leaf.RawData) ||
                            element.Certificate.RawData.AsSpan().SequenceEqual(root.RawData))
                            continue;
                        var intermediate = System.Security.Cryptography.X509Certificates
                            .X509CertificateLoader.LoadCertificate(element.Certificate.RawData);
                        intermediates.Add(intermediate);
                        chain.ChainPolicy.ExtraStore.Add(intermediate);
                    }
                }
                var valid = chain.Build(leaf);
                if (!valid)
                {
                    var status = string.Join(",", chain.ChainStatus
                        .Select(static item => item.Status.ToString())
                        .OrderBy(static item => item, StringComparer.Ordinal));
                    Android.Util.Log.Warn(
                        "Deep.UatTls",
                        $"Physical UAT certificate validation rejected: {status}.");
                }
                return valid;
            }
            finally
            {
                foreach (var intermediate in intermediates)
                    intermediate.Dispose();
            }
        }
        catch (System.Security.Cryptography.CryptographicException exception)
        {
            Android.Util.Log.Warn(
                "Deep.UatTls",
                $"Physical UAT certificate validation failed: {exception.GetType().Name}.");
            return false;
        }
    }

    private static System.Security.Cryptography.X509Certificates.X509Certificate2
        LoadPhysicalUatRootCertificate()
    {
        using var stream = typeof(MauiProgram).Assembly.GetManifestResourceStream(
            PhysicalUatRootResource)
            ?? throw new InvalidOperationException(
                "The physical UAT root certificate resource is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return System.Security.Cryptography.X509Certificates.X509CertificateLoader
            .LoadCertificate(buffer.ToArray());
    }
#endif

    private static HttpClient CreateServiceHttpClient(HttpMessageHandler handler)
    {
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    private static HttpServiceNetworkHooks CreateServiceTransportNetworkHooks() =>
        new(ServerCertificateValidationCallback:
            CreatePhysicalUatServerCertificateValidationCallback());

    private static HttpServiceNetworkHooks CreateFileTransportNetworkHooks(
        IReadOnlyList<System.Net.IPAddress> preferredConnectIps)
    {
        Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>?
            connectCallback = null;
#if ANDROID || WINDOWS
        if (preferredConnectIps.Count > 0)
        {
            connectCallback = (context, cancellationToken) =>
                ConnectFileSocketAsync(context, preferredConnectIps, cancellationToken);
        }
#endif
        return new HttpServiceNetworkHooks(
            connectCallback,
            CreatePhysicalUatServerCertificateValidationCallback());
    }

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

#if ANDROID || WINDOWS
    private static readonly TimeSpan FileConnectFallbackDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FileConnectAttemptTimeout = TimeSpan.FromSeconds(5);

    private static async ValueTask<Stream> ConnectFileSocketAsync(
        SocketsHttpConnectionContext context,
        IReadOnlyList<System.Net.IPAddress> preferredConnectIps,
        CancellationToken cancellationToken)
    {
        var addresses = await System.Net.Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken)
            .ConfigureAwait(false);
        var orderedAddresses = preferredConnectIps
            .Concat(addresses)
            .Distinct()
            .ToArray();
        if (orderedAddresses.Length == 0)
        {
            throw new HttpRequestException($"DNS returned no addresses for {context.DnsEndPoint.Host}.");
        }

        using var raceCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = orderedAddresses
            .Select((address, index) => ConnectFileSocketCandidateAsync(
                address,
                context.DnsEndPoint.Port,
                TimeSpan.FromMilliseconds(FileConnectFallbackDelay.TotalMilliseconds * index),
                raceCancellation.Token))
            .ToList();
        Exception? lastError = null;
        System.Net.Sockets.Socket? winner = null;

        try
        {
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(completed);
                var result = await completed.ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    result.Socket?.Dispose();
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (result.Socket is not null)
                {
                    winner = result.Socket;
                    break;
                }

                lastError = result.Error;
            }
        }
        finally
        {
            raceCancellation.Cancel();
            foreach (var attempt in pending)
            {
                try
                {
                    var result = await attempt.ConfigureAwait(false);
                    result.Socket?.Dispose();
                }
                catch (OperationCanceledException)
                {
                    // The race winner or caller cancellation stopped this candidate.
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (winner is not null)
        {
            return new System.Net.Sockets.NetworkStream(winner, ownsSocket: true);
        }

        throw new HttpRequestException($"Unable to connect to {context.DnsEndPoint.Host}.", lastError);
    }

    private static async Task<FileSocketConnectResult> ConnectFileSocketCandidateAsync(
        System.Net.IPAddress address,
        int port,
        TimeSpan startDelay,
        CancellationToken cancellationToken)
    {
        System.Net.Sockets.Socket? socket = null;
        try
        {
            if (startDelay > TimeSpan.Zero)
            {
                await Task.Delay(startDelay, cancellationToken).ConfigureAwait(false);
            }

            socket = new System.Net.Sockets.Socket(
                address.AddressFamily,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Tcp)
            {
                NoDelay = true
            };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(FileConnectAttemptTimeout);
            await socket.ConnectAsync(new System.Net.IPEndPoint(address, port), timeout.Token)
                .ConfigureAwait(false);

            var connected = socket;
            socket = null;
            return new FileSocketConnectResult(connected, null);
        }
        catch (Exception exception) when (exception is System.Net.Sockets.SocketException or OperationCanceledException)
        {
            return new FileSocketConnectResult(null, exception);
        }
        finally
        {
            socket?.Dispose();
        }
    }

    private sealed record FileSocketConnectResult(
        System.Net.Sockets.Socket? Socket,
        Exception? Error);
#endif

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

#if ANDROID || WINDOWS
    private sealed class RealityReadinessHandler(
        HttpMessageHandler innerHandler,
        IRealityTransportRuntime realityTransportRuntime)
        : DelegatingHandler(innerHandler)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var localPort = LocalRealityPort(request.RequestUri);
            if (localPort is not null)
            {
                await EnsureReadyAsync(request.RequestUri!, localPort.Value, cancellationToken).ConfigureAwait(false);
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        private async Task EnsureReadyAsync(Uri requestUri, int localPort, CancellationToken cancellationToken)
        {
            _ = localPort;
            await realityTransportRuntime.WaitUntilReadyAsync(requestUri, cancellationToken)
                .ConfigureAwait(false);
        }

        private static int? LocalRealityPort(Uri? requestUri) =>
            requestUri is { IsAbsoluteUri: true, IsLoopback: true, Scheme: "http" }
                ? requestUri.Port
                : null;

    }
#endif
}
