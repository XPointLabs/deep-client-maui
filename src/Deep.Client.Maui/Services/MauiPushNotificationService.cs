using Deep.Client.Shared.Platform;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui.Services;

public sealed class MauiPushNotificationService : IPushNotificationService
{
        private const string PushTokenKey = "push.token";
        private const string PushProviderKey = "push.provider";
        private const string PushRegisteredAtKey = "push.registered-at";
        private const string PushInstallIdKey = "push.install-id";

        public async Task<PushRegistration?> RegisterAsync(CancellationToken cancellationToken = default)
    {
                var permission = await Permissions.RequestAsync<Permissions.PostNotifications>().ConfigureAwait(false);
                if (permission != PermissionStatus.Granted)
                {
                        return null;
                }

#if IOS || MACCATALYST
                MainThread.BeginInvokeOnMainThread(() =>
                {
                        UIKit.UIApplication.SharedApplication.RegisterForRemoteNotifications();
                });
#endif

                var provider = ResolveProvider();
                var token = await ResolveTokenAsync(provider, cancellationToken).ConfigureAwait(false);
                var registeredAt = DateTimeOffset.UtcNow;

                await SecureStorage.SetAsync(PushTokenKey, token).ConfigureAwait(false);
                await SecureStorage.SetAsync(PushProviderKey, provider).ConfigureAwait(false);
                await SecureStorage.SetAsync(PushRegisteredAtKey, registeredAt.ToString("O")).ConfigureAwait(false);

                return new PushRegistration(token, provider, registeredAt);
    }

        public async Task UnregisterAsync(CancellationToken cancellationToken = default)
        {
                SecureStorage.Remove(PushTokenKey);
                SecureStorage.Remove(PushProviderKey);
                SecureStorage.Remove(PushRegisteredAtKey);
                await Task.CompletedTask.ConfigureAwait(false);
        }

        public async Task<PushRegistration?> GetCachedRegistrationAsync(CancellationToken cancellationToken = default)
        {
                var token = await SecureStorage.GetAsync(PushTokenKey).ConfigureAwait(false);
                var provider = await SecureStorage.GetAsync(PushProviderKey).ConfigureAwait(false);
                var registeredAt = await SecureStorage.GetAsync(PushRegisteredAtKey).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(provider))
                {
                        return null;
                }

                var timestamp = DateTimeOffset.TryParse(registeredAt, out var parsed) ? parsed : DateTimeOffset.UtcNow;
                return new PushRegistration(token, provider, timestamp);
        }

        private static string ResolveProvider()
        {
#if ANDROID
                return "fcm";
#elif IOS || MACCATALYST
                return "apns";
#elif WINDOWS
                return "wns";
#else
                return "unknown";
#endif
        }

        private static async Task<string> ResolveTokenAsync(string provider, CancellationToken cancellationToken)
        {
                var bridged = PushTokenBridge.Take(provider);
                if (!string.IsNullOrWhiteSpace(bridged))
                {
                        return bridged;
                }

#if WINDOWS
                var channel = await Windows.Networking.PushNotifications.PushNotificationChannelManager.CreatePushNotificationChannelForApplicationAsync();
                if (!string.IsNullOrWhiteSpace(channel.Uri))
                {
                        return channel.Uri;
                }
#endif

                var installId = await SecureStorage.GetAsync(PushInstallIdKey).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(installId))
                {
                        installId = Guid.NewGuid().ToString("N");
                        await SecureStorage.SetAsync(PushInstallIdKey, installId).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                return $"{provider}:{installId}";
        }
}

public static class PushTokenBridge
{
        private static readonly object Gate = new();
        private static readonly Dictionary<string, string> Tokens = new(StringComparer.OrdinalIgnoreCase);

        public static void Set(string provider, string token)
        {
                if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(token))
                {
                        return;
                }

                lock (Gate)
                {
                        Tokens[provider.Trim()] = token.Trim();
                }
        }

        public static string? Take(string provider)
        {
                if (string.IsNullOrWhiteSpace(provider))
                {
                        return null;
                }

                lock (Gate)
                {
                        return Tokens.TryGetValue(provider.Trim(), out var token) ? token : null;
                }
        }
}
