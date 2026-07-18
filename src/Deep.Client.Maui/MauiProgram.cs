using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Pages;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Features;
using Deep.Client.Shared.Platform;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;
using Microsoft.Maui.Storage;
using System.Security.Cryptography.X509Certificates;

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

public static class MauiProgram
{
    internal const string TransportBaseUrlEnv = "DEEP_TRANSPORT_BASE_URL";
    internal const string RouterBaseUrlsEnv = "XNODE_URLS";
    internal const string StorageBaseUrlEnv = "DEEP_STORAGE_URL";
    internal const string CallSignalingBaseUrlEnv = "DEEP_CALL_SIGNALING_BASE_URL";
    internal const string FileBaseUrlEnv = "DEEP_FILE_URL";
    internal const string FileConnectIpsEnv = "DEEP_FILE_CONNECT_IPS";
    internal const string FileTlsPublicKeyPinsEnv = "DEEP_FILE_TLS_PUBLIC_KEY_PINS";
    internal const string PushBaseUrlEnv = "DEEP_PUSH_URL";
    internal const string WindowsPushRemoteIdEnv = "DEEP_WINDOWS_PUSH_REMOTE_ID";
    internal const string RegistryBaseUrlEnv = "DEEP_REGISTRY_URL";
    internal const string StakingBackendBaseUrlEnv = "DEEP_STAKING_BACKEND_URL";
    internal const string StakingPortalBaseUrlEnv = "DEEP_STAKING_PORTAL_URL";
    internal const string TlsPublicKeyPinsEnv = "DEEP_TLS_PUBLIC_KEY_PINS";
    internal const string E2eBootstrapEnv = "DEEP_E2E_BOOTSTRAP";
    internal const string E2eAppDataRootEnv = "DEEP_E2E_APPDATA_ROOT";
    internal const string E2eStrictWindowsEnv = "DEEP_STRICT_WINDOWS_UI";
    private const string ReleaseRuntimeEnvFile = "deep.release.env";
    private const string WindowsReleaseRuntimeEnvFile = "deep.windows.release.env";
    internal const string WipeLocalDataOnNextLaunchKey = "session.wipe-local-on-next-launch";
    private const string LocalStateDatabaseKey = "client-state.sqlcipher-key.v1";

    public static MauiApp CreateMauiApp()
    {
#if !DEBUG
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(E2eBootstrapEnv)) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(E2eAppDataRootEnv)) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(E2eStrictWindowsEnv)))
        {
            throw new InvalidOperationException("E2E bootstrap and app-data overrides are forbidden in Release builds.");
        }
#endif
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
#if ANDROID
        ConfigureAndroidHandlers();
#endif
#if WINDOWS
        ConfigureWindowsHandlers();
#endif

        var featureFlags = BuildFeatureFlags();
        var pushBaseUrl = ResolveRuntimeSetting(PushBaseUrlEnv);
#if !DEBUG
        if (featureFlags.PushNotificationsEnabled && string.IsNullOrWhiteSpace(pushBaseUrl))
        {
            throw new InvalidOperationException(
                "DEEP_PUSH_URL is required when push notifications are enabled in non-Debug builds.");
        }
#endif
        var routerBaseUrls = ResolveRouterBaseUrls();
        var storageBaseUrl = ResolveRuntimeSetting(StorageBaseUrlEnv);
        if (routerBaseUrls.Count > 0)
        {
            RoutedRuntimeConfiguration.RejectDirectStorageForRoutedComposition(storageBaseUrl);
        }
#if !DEBUG
        if (routerBaseUrls.Count != RoutedRuntimeConfiguration.RequiredRouterCount)
        {
            throw new InvalidOperationException(
                "Production messaging requires exactly three unique pinned XPoint onion routers. Direct storage fallback is disabled.");
        }

        RoutedRuntimeConfiguration.RejectDirectStorageForRoutedComposition(storageBaseUrl);
#endif
        var fileConnectIps = ParseIpAddresses(ResolveRuntimeSetting(FileConnectIpsEnv));

        builder.Services.AddSingleton(RuntimeEnvironmentOptions.FromRuntimeSettings(ResolveRuntimeSetting));
        if (routerBaseUrls.Count > 0)
        {
            builder.Services.AddSingleton(new XNodeRpcClient(
                CreateRouterHttpClient(),
                new XNodeRpcClientOptions(routerBaseUrls)));
            builder.Services.AddSingleton<ITransportRouteProvider>(sp => sp.GetRequiredService<XNodeRpcClient>());
        }
        else
        {
#if DEBUG
            builder.Services.AddSingleton<ITransportRouteProvider>(_ =>
                new DirectStorageRouteProvider(storageBaseUrl));
#else
            throw new InvalidOperationException(
                "Release composition cannot create a direct-storage route provider.");
#endif
        }

        builder.Services.AddSingleton<ISessionMessageTransport>(_ =>
        {
            if (routerBaseUrls.Count > 0)
            {
                return new RoutedSessionStorageMessageTransport(
                    _.GetRequiredService<XNodeRpcClient>(),
                    new RoutedSessionStorageTransportOptions());
            }

#if DEBUG
            if (!string.IsNullOrWhiteSpace(storageBaseUrl))
            {
                return new SessionStorageMessageTransport(
                    CreateServiceHttpClient(),
                    new SessionStorageMessageTransportOptions(storageBaseUrl));
            }

            var baseUrl = ResolveRuntimeSetting(TransportBaseUrlEnv);
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                if (!featureFlags.StubTransportAllowed)
                {
                    throw new InvalidOperationException("Stub transport is disabled by feature flags.");
                }

                return new StubSessionBackend();
            }

            return new HttpSessionTransport(CreateServiceHttpClient(), new HttpSessionTransportOptions(baseUrl));
#else
            throw new InvalidOperationException(
                "Release composition requires routed XNODE_URLS transport and has no direct storage fallback.");
#endif
        });
        builder.Services.AddSingleton(featureFlags);
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<IIpCountryLookup>(_ => new IpCountryLookup(
            _ => Task.FromResult(OpenEmbeddedResource("geolite2_country_blocks_ipv4")),
            _ => Task.FromResult(OpenEmbeddedResource("geolite2_country_codes.json"))));
        builder.Services.AddSingleton<IAvatarProfileTransport>(_ =>
        {
            var baseUrl = ResolveRuntimeSetting(FileBaseUrlEnv);
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                return new HttpAvatarProfileTransport(CreateFileHttpClient(fileConnectIps), new HttpAvatarProfileTransportOptions(baseUrl));
            }

#if DEBUG
            return new DisabledAvatarProfileTransport();
#else
            throw new InvalidOperationException(
                "DEEP_FILE_URL is required in non-Debug builds. " +
                "Remote avatar publication is not allowed to be disabled for release startup.");
#endif
        });
        builder.Services.AddSingleton<IAttachmentFileTransport>(_ =>
        {
            var baseUrl = ResolveRuntimeSetting(FileBaseUrlEnv);
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                return new HttpAttachmentFileTransport(CreateFileHttpClient(fileConnectIps), new HttpAttachmentFileTransportOptions(baseUrl));
            }

#if DEBUG
            return new DisabledAttachmentFileTransport();
#else
            throw new InvalidOperationException(
                "DEEP_FILE_URL is required in non-Debug builds. " +
                "Remote attachment upload is not allowed to be disabled for release startup.");
#endif
        });
        builder.Services.AddSingleton(sp =>
            new ClientRuntimeBootstrapper(cancellationToken => CreateClientRuntimeAsync(sp, cancellationToken)));
        builder.Services.AddSingleton(sp =>
            sp.GetRequiredService<ClientRuntimeBootstrapper>().GetRequiredRuntime());

        builder.Services.AddSingleton<IPushNotificationService, MauiPushNotificationService>();
        builder.Services.AddSingleton(_ => new PushClientMetadata(
            PushNotificationCrypto.PackageName,
            AppInfo.Current.VersionString));
        builder.Services.AddSingleton<IPushSubscriptionTransport>(_ =>
        {
            return string.IsNullOrWhiteSpace(pushBaseUrl)
                ? new DisabledPushSubscriptionTransport()
                : new HttpPushSubscriptionTransport(CreateServiceHttpClient(), new HttpPushSubscriptionTransportOptions(pushBaseUrl));
        });
        builder.Services.AddSingleton<IPushRegistrationCoordinator, PushRegistrationCoordinator>();
        builder.Services.AddSingleton<SyncPollingPolicy>();
        builder.Services.AddSingleton<PushRegistrationLifecycleCoordinator>();
        builder.Services.AddSingleton<ChatOpenUiCache>();
        builder.Services.AddSingleton<IActiveConversationTracker, ActiveConversationTracker>();
        builder.Services.AddSingleton<IAccountLogoutCoordinator, MauiAccountLogoutCoordinator>();
        builder.Services.AddSingleton<IMediaCodecService, MauiMediaCodecService>();
        builder.Services.AddSingleton<IPermissionsService, MauiPermissionsService>();
        builder.Services.AddSingleton<IBackgroundTaskService, MauiBackgroundTaskService>();
        builder.Services.AddSingleton<IRegularBackgroundSyncScheduler, MauiRegularBackgroundSyncScheduler>();
        builder.Services.AddSingleton<BackgroundSyncSchedulingCoordinator>();
        builder.Services.AddSingleton<IShareExtensionBridge, MauiShareExtensionBridge>();
        builder.Services.AddSingleton<INotificationScheduler, MauiNotificationScheduler>();
        builder.Services.AddSingleton<IPrivacyScreenService, MauiPrivacyScreenService>();
#if ANDROID
        builder.Services.AddSingleton<IAppLockService, AndroidAppLockService>();
#elif WINDOWS
        builder.Services.AddSingleton<IAppLockService, WindowsAppLockService>();
#else
        builder.Services.AddSingleton<IAppLockService, AppLockService>();
#endif
        builder.Services.AddSingleton<IAppearanceService, MauiAppearanceService>();
        builder.Services.AddSingleton<IAppIconService, AppIconService>();
        builder.Services.AddSingleton<ICallSignalingTransport>(services =>
        {
            var baseUrl = ResolveRuntimeSetting(CallSignalingBaseUrlEnv);
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                return new HttpCallSignalingTransport(
                    CreateServiceHttpClient(),
                    new HttpCallSignalingTransportOptions(baseUrl),
                    cancellationToken => services
                        .GetRequiredService<ClientRuntime>()
                        .Accounts
                        .GetRecoveryPhraseAsync(cancellationToken));
            }

#if DEBUG
            return new InMemoryCallSignalingTransport();
#else
            if (featureFlags.CallsEnabled)
            {
                throw new InvalidOperationException(
                    "DEEP_CALL_SIGNALING_BASE_URL is required when calls are enabled in non-Debug builds.");
            }

            return new InMemoryCallSignalingTransport();
#endif
        });
        builder.Services.AddSingleton<RealtimeCallService>();
        builder.Services.AddSingleton<ICallService, MauiRealtimeCallService>();
        builder.Services.AddSingleton<ICallIceConfigurationProvider>(services =>
            (ICallIceConfigurationProvider)services.GetRequiredService<ICallSignalingTransport>());
        builder.Services.AddSingleton<CallSessionCoordinator>();
        builder.Services.AddSingleton<IAttachmentPickerService, MauiAttachmentPickerService>();
        builder.Services.AddSingleton<IVoiceMessageRecorder, MauiVoiceMessageRecorder>();
        builder.Services.AddSingleton<INetworkStatusService, MauiConnectivityStatusService>();
        builder.Services.AddSingleton<AuthNavigationState>();

        builder.Services.AddTransient<OnboardingViewModel>();
        builder.Services.AddTransient<WelcomeViewModel>();
        builder.Services.AddTransient<ConversationsViewModel>();
        builder.Services.AddTransient<ChatViewModel>();
        builder.Services.AddTransient<GroupChatViewModel>();
        builder.Services.AddTransient<GroupsViewModel>();
        builder.Services.AddTransient<AttachmentPickerViewModel>();
        builder.Services.AddTransient<NotificationRegistrationViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();
#if WINDOWS
        builder.Services.AddSingleton(services => new DesktopWorkspaceViewModel(
            services.GetRequiredService<ClientRuntime>(),
            () => services.GetRequiredService<ConversationsViewModel>(),
            () => services.GetRequiredService<ChatViewModel>(),
            () => services.GetRequiredService<GroupChatViewModel>()));
#endif

        builder.Services.AddSingleton<AppShell>();
        builder.Services.AddTransient<WelcomePage>();
        builder.Services.AddTransient<OnboardingPage>();
        builder.Services.AddTransient<ConversationsPage>();
        builder.Services.AddTransient<StartConversationPage>();
        builder.Services.AddTransient<NewConversationPage>();
        builder.Services.AddTransient<ChatPage>();
        builder.Services.AddTransient<ContactProfilePage>();
        builder.Services.AddTransient<GroupChatPage>();
        builder.Services.AddTransient<GroupsPage>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<SettingsDetailPage>();
        builder.Services.AddTransient<CallPage>();
#if WINDOWS
        builder.Services.AddSingleton<DesktopWorkspacePage>();
#endif

        return builder.Build();
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
    }
#endif

    private static ClientFeatureFlags BuildFeatureFlags()
    {
#if DEBUG
        return ClientFeatureFlags.Defaults;
#else
        return ClientFeatureFlags.ReleaseDefaults;
#endif
    }

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

    private static IReadOnlyList<PinnedRouterEndpoint> ResolveRouterBaseUrls()
    {
#if DEBUG
        if (string.Equals(
                Environment.GetEnvironmentVariable(E2eBootstrapEnv),
                "stub",
                StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }
#endif
        var raw = ResolveRuntimeSetting(RouterBaseUrlsEnv);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            return RoutedRuntimeConfiguration.ParseExactlyThree(raw);
        }

#if ANDROID
        return RoutedRuntimeConfiguration.ValidateExactlyThree(AndroidRealityTransport.Start());
#elif WINDOWS
        return RoutedRuntimeConfiguration.ValidateExactlyThree(WindowsRealityTransport.Start());
#else
        return [];
#endif
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
            return ClientRuntime.CreateStubbed(
                services.GetRequiredService<ClientFeatureFlags>(),
                services.GetRequiredService<IClock>(),
                avatarProfiles: services.GetRequiredService<IAvatarProfileTransport>());
        }
#endif
        var appDataDirectory = ResolveAppDataDirectory();
        var stateDbPath = Path.Combine(appDataDirectory, "client-state.db");
        var legacyStatePath = Path.Combine(appDataDirectory, "client-state.json");
        var stateDbKey = await ResolveLocalStateDatabaseKeyAsync(cancellationToken).ConfigureAwait(false);

        if (Preferences.Default.Get(WipeLocalDataOnNextLaunchKey, false))
        {
            DeleteFileForWipe(stateDbPath + "-wal");
            DeleteFileForWipe(stateDbPath + "-shm");
            DeleteFileForWipe(stateDbPath);
            await FileSystemLegacyStateArtifacts.Instance
                .PurgeAsync(legacyStatePath, cancellationToken)
                .ConfigureAwait(false);
            SecureStorage.Remove(LocalStateDatabaseKey);
            Preferences.Default.Remove(WipeLocalDataOnNextLaunchKey);
            stateDbKey = await ResolveLocalStateDatabaseKeyAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        SqliteSessionStore.EnsureEncryptedDatabase(stateDbPath, stateDbKey);
        return ClientRuntime.CreatePersistent(
            stateDbPath,
            services.GetRequiredService<ClientFeatureFlags>(),
            services.GetRequiredService<IClock>(),
            services.GetRequiredService<ISessionMessageTransport>(),
            groupSyncTransport: null,
            avatarProfiles: services.GetRequiredService<IAvatarProfileTransport>(),
            legacyInMemoryStatePath: legacyStatePath,
            sqlCipherKey: stateDbKey,
            storeDecorator: store => new SecureRecoverySessionStore(store),
            requireE2eeTransport: true);
    }

    internal static string ResolveAppDataDirectory() => AppDataPath.Resolve();

    private static HttpClient CreateRouterHttpClient()
    {
        var socketsHandler = CreateServiceHttpHandler();
        socketsHandler.UseProxy = false;
        HttpMessageHandler handler = socketsHandler;
#if ANDROID || WINDOWS
        handler = new RealityReadinessHandler(handler);
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
        ConfigureCertificatePinning(handler);
        return handler;
    }

    private static HttpClient CreateServiceHttpClient() =>
        CreateServiceHttpClient(CreateServiceHttpHandler());

    private static HttpClient CreateServiceHttpClient(HttpMessageHandler handler)
    {
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    private static HttpClient CreateFileHttpClient(IReadOnlyList<System.Net.IPAddress> preferredConnectIps)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        ConfigureCertificatePinning(handler, FileTlsPublicKeyPinsEnv);
#if ANDROID || WINDOWS
        if (preferredConnectIps.Count > 0)
        {
            handler.ConnectCallback = (context, cancellationToken) =>
                ConnectFileSocketAsync(context, preferredConnectIps, cancellationToken);
        }
#endif

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Deep/{AppInfo.Current.VersionString}");
        return client;
    }

    private static void ConfigureCertificatePinning(
        SocketsHttpHandler handler,
        string pinSettingName = TlsPublicKeyPinsEnv)
    {
        var rawPins = ResolveRuntimeSetting(pinSettingName);
        if (string.IsNullOrWhiteSpace(rawPins) &&
            !string.Equals(pinSettingName, TlsPublicKeyPinsEnv, StringComparison.Ordinal))
        {
            rawPins = ResolveRuntimeSetting(TlsPublicKeyPinsEnv);
        }

        var pins = ParsePublicKeyPins(rawPins, pinSettingName);
#if !DEBUG
        if (pins.Count == 0)
        {
            throw new InvalidOperationException(
                $"{pinSettingName} must contain at least one production certificate public-key pin.");
        }
#endif
        if (pins.Count == 0)
        {
            return;
        }

        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, policyErrors) =>
        {
            if (policyErrors != System.Net.Security.SslPolicyErrors.None || certificate is null)
            {
                return false;
            }

            using var x509 = new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate);
            var digest = System.Security.Cryptography.SHA256.HashData(ExportSubjectPublicKeyInfo(x509));
            return pins.Any(pin =>
                System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(pin, digest));
        };
    }

    private static byte[] ExportSubjectPublicKeyInfo(
        System.Security.Cryptography.X509Certificates.X509Certificate2 certificate)
    {
        using var rsa = certificate.GetRSAPublicKey();
        if (rsa is not null)
        {
            return rsa.ExportSubjectPublicKeyInfo();
        }

        using var ecdsa = certificate.GetECDsaPublicKey();
        if (ecdsa is not null)
        {
            return ecdsa.ExportSubjectPublicKeyInfo();
        }

        using var dsa = certificate.GetDSAPublicKey();
        if (dsa is not null)
        {
            return dsa.ExportSubjectPublicKeyInfo();
        }

        throw new System.Security.Cryptography.CryptographicException(
            "The TLS certificate uses an unsupported public-key algorithm.");
    }

    private static IReadOnlyList<byte[]> ParsePublicKeyPins(string? raw, string settingName)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var pins = new List<byte[]>();
        foreach (var value in raw.Split([',', ';', '\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var encoded = value.StartsWith("sha256/", StringComparison.OrdinalIgnoreCase)
                ? value["sha256/".Length..]
                : value;
            byte[] digest;
            try
            {
                digest = Convert.FromBase64String(encoded);
            }
            catch (FormatException exception)
            {
                throw new InvalidOperationException($"{settingName} contains an invalid Base64 pin.", exception);
            }

            if (digest.Length != 32)
            {
                throw new InvalidOperationException($"{settingName} pins must be SHA-256 digests.");
            }

            pins.Add(digest);
        }

        return pins;
    }

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
            _ = RoutedRuntimeConfiguration.ParseExactlyThree(value);
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
    private sealed class RealityReadinessHandler(HttpMessageHandler innerHandler)
        : DelegatingHandler(innerHandler)
    {
        private readonly object readinessSync = new();
        private readonly HashSet<int> readyPorts = [];
        private readonly SemaphoreSlim readinessGate = new(1, 1);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var localPort = LocalRealityPort(request.RequestUri);
            if (localPort is not null)
            {
                await EnsureReadyAsync(request.RequestUri!, localPort.Value, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (localPort is not null)
                {
                    lock (readinessSync)
                    {
                        readyPorts.Remove(localPort.Value);
                    }
                }

                throw;
            }
        }

        private async Task EnsureReadyAsync(Uri requestUri, int localPort, CancellationToken cancellationToken)
        {
            lock (readinessSync)
            {
                if (readyPorts.Contains(localPort))
                {
                    return;
                }
            }

            await readinessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (readinessSync)
                {
                    if (readyPorts.Contains(localPort))
                    {
                        return;
                    }
                }

#if ANDROID
                await AndroidRealityTransport.WaitUntilReadyAsync(requestUri, cancellationToken).ConfigureAwait(false);
#elif WINDOWS
                await WindowsRealityTransport.WaitUntilReadyAsync(requestUri, cancellationToken).ConfigureAwait(false);
#endif
                lock (readinessSync)
                {
                    readyPorts.Add(localPort);
                }
            }
            finally
            {
                readinessGate.Release();
            }
        }

        private static int? LocalRealityPort(Uri? requestUri) =>
            requestUri is { IsAbsoluteUri: true, IsLoopback: true, Scheme: "http" }
                ? requestUri.Port
                : null;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                readinessGate.Dispose();
            }

            base.Dispose(disposing);
        }
    }
#endif
}
