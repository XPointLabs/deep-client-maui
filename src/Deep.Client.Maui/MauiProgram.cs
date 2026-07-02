using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Pages;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Features;
using Deep.Client.Shared.Platform;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui;

public static class MauiProgram
{
    internal const string TransportBaseUrlEnv = "DEEP_TRANSPORT_BASE_URL";
    internal const string RouterBaseUrlsEnv = "XNODE_URLS";
    internal const string StorageBaseUrlEnv = "DEEP_STORAGE_URL";
    internal const string CallSignalingBaseUrlEnv = "DEEP_CALL_SIGNALING_BASE_URL";
    internal const string FileBaseUrlEnv = "DEEP_FILE_URL";
    internal const string PushBaseUrlEnv = "DEEP_PUSH_URL";
    internal const string RegistryBaseUrlEnv = "DEEP_REGISTRY_URL";
    internal const string StakingBackendBaseUrlEnv = "DEEP_STAKING_BACKEND_URL";
    internal const string StakingPortalBaseUrlEnv = "DEEP_STAKING_PORTAL_URL";
    private const string ReleaseRuntimeEnvFile = "deep.release.env";
    internal const string WipeLocalDataOnNextLaunchKey = "session.wipe-local-on-next-launch";

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        var featureFlags = BuildFeatureFlags();
        var routerBaseUrls = ResolveRouterBaseUrls();

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
                    new HttpClient(),
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

            return new HttpSessionTransport(new HttpClient(), new HttpSessionTransportOptions(baseUrl));
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
                return new HttpAvatarProfileTransport(new HttpClient(), new HttpAvatarProfileTransportOptions(baseUrl));
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
                    new HttpClient(),
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
                return new HttpAttachmentFileTransport(new HttpClient(), new HttpAttachmentFileTransportOptions(baseUrl));
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

            if (Preferences.Default.Get(WipeLocalDataOnNextLaunchKey, false))
            {
                TryDeleteFile(stateDbPath);
                TryDeleteFile(legacyStatePath);
                Preferences.Default.Remove(WipeLocalDataOnNextLaunchKey);
            }

            return ClientRuntime.CreatePersistent(
                stateDbPath,
                sp.GetRequiredService<ClientFeatureFlags>(),
                sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<ISessionMessageTransport>(),
                sp.GetRequiredService<IGroupSyncTransport>(),
                sp.GetRequiredService<IAvatarProfileTransport>(),
                legacyStatePath);
        });

        builder.Services.AddSingleton<IPushNotificationService, MauiPushNotificationService>();
        builder.Services.AddSingleton<IPushSubscriptionTransport>(_ =>
        {
            var baseUrl = ResolveRuntimeSetting(PushBaseUrlEnv);
            return string.IsNullOrWhiteSpace(baseUrl)
                ? new DisabledPushSubscriptionTransport()
                : new HttpPushSubscriptionTransport(new HttpClient(), new HttpPushSubscriptionTransportOptions(baseUrl));
        });
        builder.Services.AddSingleton<IPushRegistrationCoordinator, PushRegistrationCoordinator>();
        builder.Services.AddSingleton<IMediaCodecService, MauiMediaCodecService>();
        builder.Services.AddSingleton<IPermissionsService, MauiPermissionsService>();
        builder.Services.AddSingleton<IBackgroundTaskService, MauiBackgroundTaskService>();
        builder.Services.AddSingleton<IShareExtensionBridge, MauiShareExtensionBridge>();
        builder.Services.AddSingleton<INotificationScheduler, MauiNotificationScheduler>();
        builder.Services.AddSingleton<IPrivacyScreenService, MauiPrivacyScreenService>();
        builder.Services.AddSingleton<IAppearanceService, MauiAppearanceService>();
        builder.Services.AddSingleton<ICallSignalingTransport>(services =>
        {
            var baseUrl = ResolveRuntimeSetting(CallSignalingBaseUrlEnv);
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                return new HttpCallSignalingTransport(
                    new HttpClient(),
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
        builder.Services.AddSingleton<IAttachmentPickerService, MauiAttachmentPickerService>();
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
        builder.Services.AddTransient<GroupChatPage>();
        builder.Services.AddTransient<GroupsPage>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<SettingsDetailPage>();

        return builder.Build();
    }

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

    internal static string? ResolveRuntimeSetting(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var filePath = Path.Combine(AppContext.BaseDirectory, ReleaseRuntimeEnvFile);
        if (File.Exists(filePath))
        {
            var fileValue = ResolveRuntimeSettingFromLines(key, File.ReadLines(filePath));
            if (!string.IsNullOrWhiteSpace(fileValue))
            {
                return fileValue;
            }
        }

        using var embeddedStream = typeof(MauiProgram).Assembly.GetManifestResourceStream(ReleaseRuntimeEnvFile);
        if (embeddedStream is null)
        {
            return null;
        }

        using var reader = new StreamReader(embeddedStream);
        return ResolveRuntimeSettingFromLines(key, ReadLines(reader));
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

    private static IReadOnlyList<string> ParseRouterBaseUrls(string raw)
    {
        return raw
            .Split([';', ',', '\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static value => Uri.TryCreate(value, UriKind.Absolute, out var uri)
                                   && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

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
