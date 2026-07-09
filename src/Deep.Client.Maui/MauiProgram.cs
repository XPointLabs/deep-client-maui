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

#if ANDROID
using Android.Content.Res;
using Android.Graphics.Drawables;
using Android.Widget;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
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
    internal const string PushBaseUrlEnv = "DEEP_PUSH_URL";
    internal const string RegistryBaseUrlEnv = "DEEP_REGISTRY_URL";
    internal const string StakingBackendBaseUrlEnv = "DEEP_STAKING_BACKEND_URL";
    internal const string StakingPortalBaseUrlEnv = "DEEP_STAKING_PORTAL_URL";
    private const string ReleaseRuntimeEnvFile = "deep.release.env";
    internal const string WipeLocalDataOnNextLaunchKey = "session.wipe-local-on-next-launch";
    private const string LocalStateDatabaseKey = "client-state.sqlcipher-key.v1";

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
#if ANDROID
        ConfigureAndroidTextInputChrome();
#endif

        var featureFlags = BuildFeatureFlags();
        var routerBaseUrls = ResolveRouterBaseUrls();
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
            builder.Services.AddSingleton<ITransportRouteProvider>(_ =>
                new DirectStorageRouteProvider(ResolveRuntimeSetting(StorageBaseUrlEnv)));
        }

        builder.Services.AddSingleton<ISessionMessageTransport>(_ =>
        {
            if (routerBaseUrls.Count > 0)
            {
                return new RoutedSessionStorageMessageTransport(
                    _.GetRequiredService<XNodeRpcClient>(),
                    new RoutedSessionStorageTransportOptions());
            }

            var storageBaseUrl = ResolveRuntimeSetting(StorageBaseUrlEnv);
            if (!string.IsNullOrWhiteSpace(storageBaseUrl))
            {
                return new SessionStorageMessageTransport(
                    CreateServiceHttpClient(),
                    new SessionStorageMessageTransportOptions(storageBaseUrl));
            }

            var baseUrl = ResolveRuntimeSetting(TransportBaseUrlEnv);
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
#if DEBUG
                if (!featureFlags.StubTransportAllowed)
                {
                    throw new InvalidOperationException("Stub transport is disabled by feature flags.");
                }

                return new StubSessionBackend();
#else
                throw new InvalidOperationException(
                    "DEEP_STORAGE_URL or DEEP_TRANSPORT_BASE_URL is required in non-Debug builds. " +
                    "Stub transport is not allowed for release startup.");
#endif
            }

            return new HttpSessionTransport(CreateServiceHttpClient(), new HttpSessionTransportOptions(baseUrl));
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
        builder.Services.AddSingleton<IGroupSyncTransport>(_ =>
        {
            if (routerBaseUrls.Count > 0)
            {
                return new RoutedSessionStorageGroupSyncTransport(
                    _.GetRequiredService<XNodeRpcClient>(),
                    new RoutedSessionStorageGroupSyncTransportOptions());
            }

            var storageBaseUrl = ResolveRuntimeSetting(StorageBaseUrlEnv);
            if (!string.IsNullOrWhiteSpace(storageBaseUrl))
            {
                return new SessionStorageGroupSyncTransport(
                    CreateServiceHttpClient(),
                    new SessionStorageGroupSyncTransportOptions(storageBaseUrl));
            }

#if DEBUG
            return new DisabledGroupSyncTransport();
#else
            if (featureFlags.GroupsV2Enabled)
            {
                throw new InvalidOperationException(
                    "DEEP_STORAGE_URL is required when groups are enabled in non-Debug builds.");
            }

            return new DisabledGroupSyncTransport();
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
        {
            var stateDbPath = Path.Combine(FileSystem.AppDataDirectory, "client-state.db");
            var legacyStatePath = Path.Combine(FileSystem.AppDataDirectory, "client-state.json");
            var stateDbKey = ResolveLocalStateDatabaseKey();

            if (Preferences.Default.Get(WipeLocalDataOnNextLaunchKey, false))
            {
                TryDeleteFile(stateDbPath);
                TryDeleteFile(stateDbPath + "-wal");
                TryDeleteFile(stateDbPath + "-shm");
                TryDeleteFile(legacyStatePath);
                SecureStorage.Remove(LocalStateDatabaseKey);
                Preferences.Default.Remove(WipeLocalDataOnNextLaunchKey);
                stateDbKey = ResolveLocalStateDatabaseKey();
            }

            SqliteSessionStore.EnsureEncryptedDatabase(stateDbPath, stateDbKey);
            return ClientRuntime.CreatePersistent(
                stateDbPath,
                sp.GetRequiredService<ClientFeatureFlags>(),
                sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<ISessionMessageTransport>(),
                sp.GetRequiredService<IGroupSyncTransport>(),
                sp.GetRequiredService<IAvatarProfileTransport>(),
                legacyStatePath,
                stateDbKey,
                storeDecorator: store => new SecureRecoverySessionStore(store));
        });

        builder.Services.AddSingleton<IPushNotificationService, MauiPushNotificationService>();
        builder.Services.AddSingleton<IPushSubscriptionTransport>(_ =>
        {
            var baseUrl = ResolveRuntimeSetting(PushBaseUrlEnv);
            return string.IsNullOrWhiteSpace(baseUrl)
                ? new DisabledPushSubscriptionTransport()
                : new HttpPushSubscriptionTransport(CreateServiceHttpClient(), new HttpPushSubscriptionTransportOptions(baseUrl));
        });
        builder.Services.AddSingleton<IPushRegistrationCoordinator, PushRegistrationCoordinator>();
        builder.Services.AddSingleton<SyncPollingPolicy>();
        builder.Services.AddSingleton<IMediaCodecService, MauiMediaCodecService>();
        builder.Services.AddSingleton<IPermissionsService, MauiPermissionsService>();
        builder.Services.AddSingleton<IBackgroundTaskService, MauiBackgroundTaskService>();
        builder.Services.AddSingleton<IShareExtensionBridge, MauiShareExtensionBridge>();
        builder.Services.AddSingleton<INotificationScheduler, MauiNotificationScheduler>();
        builder.Services.AddSingleton<IPrivacyScreenService, MauiPrivacyScreenService>();
#if ANDROID
        builder.Services.AddSingleton<IAppLockService, AndroidAppLockService>();
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

        return builder.Build();
    }

#if ANDROID
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

    private static ClientFeatureFlags BuildFeatureFlags()
    {
#if DEBUG
        return ClientFeatureFlags.Defaults;
#else
        return ClientFeatureFlags.ReleaseDefaults;
#endif
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // If file is locked, keep deferred wipe best-effort and continue startup.
        }
    }

    private static string ResolveLocalStateDatabaseKey()
    {
        try
        {
            var existing = SecureStorage.GetAsync(LocalStateDatabaseKey).GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }

            var key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            SecureStorage.SetAsync(LocalStateDatabaseKey, key).GetAwaiter().GetResult();
            return key;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Secure local database key storage is unavailable.", ex);
        }
    }

    internal static string? ResolveRuntimeSetting(string key)
    {
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

        using var embeddedStream = typeof(MauiProgram).Assembly.GetManifestResourceStream(ReleaseRuntimeEnvFile);
        if (embeddedStream is null)
        {
            return null;
        }

        using var reader = new StreamReader(embeddedStream);
        return ValidateRuntimeSetting(key, ResolveRuntimeSettingFromLines(key, ReadLines(reader)));
    }

    private static IReadOnlyList<string> ResolveRouterBaseUrls()
    {
        var raw = ResolveRuntimeSetting(RouterBaseUrlsEnv);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            return ParseRouterBaseUrls(raw);
        }

#if ANDROID
        return AndroidRealityTransport.Start();
#else
        return [];
#endif
    }

    private static Stream OpenEmbeddedResource(string name) =>
        typeof(MauiProgram).Assembly.GetManifestResourceStream(name)
        ?? throw new FileNotFoundException($"Embedded resource '{name}' was not found.", name);

    private static HttpClient CreateRouterHttpClient()
    {
        return CreateServiceHttpClient();
    }

    private static HttpClient CreateServiceHttpClient()
    {
        return new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    private static HttpClient CreateFileHttpClient(IReadOnlyList<System.Net.IPAddress> preferredConnectIps)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
#if ANDROID
        handler.ConnectCallback = (context, cancellationToken) =>
            ConnectFileSocketAsync(context, preferredConnectIps, cancellationToken);
#endif

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Deep/0.2.0");
        return client;
    }

#if ANDROID
    private static async ValueTask<Stream> ConnectFileSocketAsync(
        SocketsHttpConnectionContext context,
        IReadOnlyList<System.Net.IPAddress> preferredConnectIps,
        CancellationToken cancellationToken)
    {
        var addresses = await System.Net.Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken)
            .ConfigureAwait(false);
        var orderedAddresses = preferredConnectIps
            .Where(static address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Concat(addresses.Where(static address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
            .Distinct()
            .ToArray();
        Exception? lastError = null;

        foreach (var address in orderedAddresses)
        {
            var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Tcp)
            {
                NoDelay = true
            };

            try
            {
                await socket.ConnectAsync(new System.Net.IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken)
                    .ConfigureAwait(false);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException)
            {
                lastError = ex;
                socket.Dispose();
            }
        }

        throw new HttpRequestException($"Unable to connect to {context.DnsEndPoint.Host} over IPv4.", lastError);
    }
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
        if (string.Equals(key, FileConnectIpsEnv, StringComparison.Ordinal))
        {
            return value;
        }

        if (string.Equals(key, RouterBaseUrlsEnv, StringComparison.Ordinal))
        {
            foreach (var url in SplitRuntimeValues(value))
            {
                ValidateRuntimeUrl(key, url);
            }

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

    private static IReadOnlyList<string> ParseRouterBaseUrls(string raw)
    {
        return SplitRuntimeValues(raw)
            .Where(static value => Uri.TryCreate(value, UriKind.Absolute, out var uri)
                                   && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

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
