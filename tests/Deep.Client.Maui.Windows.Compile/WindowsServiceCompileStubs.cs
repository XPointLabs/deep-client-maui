using System.Reflection;

namespace Deep.Client.Maui
{
    internal static class App
    {
        public static IServiceProvider? Services => null;
    }

    internal static class CrashDiagnostics
    {
        public static void LogException(string area, Exception exception)
        {
        }

        public static void LogInfo(string area, string message)
        {
        }
    }

    internal static class MauiProgram
    {
        public const string WindowsPushRemoteIdEnv = "DEEP_WINDOWS_PUSH_REMOTE_ID";

        public static string? ResolveRuntimeSetting(string key) => null;
    }

    public static class WnsPushPayloadCodec
    {
        public static bool TryDecode(
            ReadOnlyMemory<byte> payload,
            out IReadOnlyDictionary<string, string> data)
        {
            data = new Dictionary<string, string>();
            return false;
        }
    }

    public static class WnsChannelUriValidator
    {
        public static bool IsValid(string? value) => true;
    }

    internal static class RealityTransportConfiguration
    {
        public static RealityBootstrap LoadEmbedded(Assembly assembly) =>
            new(1, []);

        public static IReadOnlyList<Deep.Client.Shared.Services.PinnedRouterEndpoint> BuildRouterEndpoints(
            RealityBootstrap bootstrap) => [];

        public static string BuildXrayConfig(IReadOnlyList<RealitySeed> seeds) => "{}";

        public static RealitySeed? FindSeedForRequest(
            IReadOnlyList<RealitySeed>? seeds,
            Uri? requestUri) => null;
    }

    internal sealed record RealityBootstrap(int Version, IReadOnlyList<RealitySeed> Seeds);

    internal sealed record RealitySeed(
        string RouterId,
        string OriginIp,
        int Port,
        string ClientId,
        string Flow,
        string ServerName,
        string PublicKey,
        string ShortId,
        string Fingerprint,
        string SpiderX,
        int LocalPort);

    internal sealed class RealityStartupCoordinator
    {
        public RealityStartupCoordinator(
            Func<bool, CancellationToken, Task> startAsync,
            Func<int, CancellationToken, Task<bool>> probeListenerAsync,
            TimeSpan initialRetryDelay,
            TimeSpan maximumRetryDelay,
            TimeSpan listenerPollInterval,
            Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
            Action<Exception>? startupFailed = null)
        {
        }

        public Task EnsureStartedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WaitUntilReadyAsync(int listenerPort, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

namespace Deep.Client.Maui.Services
{
    public interface IAppLockService
    {
        bool IsSupported { get; }

        bool IsDeviceSecure { get; }

        bool IsEnabled { get; }

        string? UnavailableReason { get; }

        void SetEnabled(bool enabled);

        void MarkAppHidden();

        Task<bool> AuthenticateIfRequiredAsync(CancellationToken cancellationToken = default);

        Task<bool> AuthenticateNowAsync(CancellationToken cancellationToken = default);
    }

    public static class ClientSettingKeys
    {
        public const string PrivacyAppLock = "settings.privacy.app-lock";
    }

    public static class PushTokenBridge
    {
        public static void Set(string provider, string token)
        {
        }

        public static void Clear()
        {
        }
    }

    public static class BackgroundSyncBridge
    {
        public static void PublishScheduledSync()
        {
        }

        public static bool HasPendingSync() => false;
    }

    public static class MauiBackgroundSyncRunner
    {
        public static Task<bool> TrySynchronizeAsync(
            IServiceProvider? services,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public static Task<IReadOnlyList<Deep.Client.Shared.Persistence.PendingIncomingMessageNotification>>
            ListPendingIncomingMessageNotificationIdsAsync(
                IServiceProvider? services,
                int limit,
                CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Deep.Client.Shared.Persistence.PendingIncomingMessageNotification>>([]);

        public static Task MarkIncomingMessageNotificationsPresentedAsync(
            IServiceProvider? services,
            IReadOnlyCollection<Deep.Client.Shared.Domain.MessageId> messageIds,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public enum PushAuthenticationStatus
    {
        Rejected,
        Accepted,
        Replay
    }

    public readonly record struct PushAuthenticationResult(PushAuthenticationStatus Status);

    public static class PushNotificationBackgroundHandler
    {
        public static Task<PushAuthenticationResult> AuthenticateAsync(
            IReadOnlyDictionary<string, string> data,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PushAuthenticationResult(PushAuthenticationStatus.Rejected));

        public static Task<PushAuthenticationResult> AuthenticateAsync(
            IReadOnlyDictionary<string, string> data,
            Action beforePersistAcceptance,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PushAuthenticationResult(PushAuthenticationStatus.Rejected));
    }

    internal enum ShareIngressEnqueueResult
    {
        Enqueued,
        AlreadyPending,
        AlreadyCompleted,
        Rejected
    }

    public static class MauiShareExtensionBridge
    {
        internal static ShareIngressEnqueueResult TryEnqueueFromBackground(
            Deep.Client.Shared.Platform.SharePayload payload) => ShareIngressEnqueueResult.Enqueued;
    }

    internal static class PlatformIngressLimits
    {
        public const int MaxShareTextBytes = 64 * 1024;
        public const int MaxShareUriCount = 8;
        public const long MaxShareFileBytes = 25L * 1024 * 1024;
        public const long MaxShareTotalBytes = 25L * 1024 * 1024;
    }
}
