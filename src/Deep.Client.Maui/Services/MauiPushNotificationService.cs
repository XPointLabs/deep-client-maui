using Deep.Client.Shared.Platform;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

using System.Security.Cryptography;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Services;

public sealed class MauiPushNotificationService :
    IPushNotificationService,
    IPushNotificationEncryptionKeyStore,
    IPushUnsubscribeRetryStore
{
    private const string PendingUnsubscribeKey = "push.unsubscribe.pending.v1";
    private readonly MauiPushNotificationKeyStore keyStore = new();

    public async Task<PushRegistration?> RegisterAsync(CancellationToken cancellationToken = default)
    {
        var provider = ResolveProvider();
        if (provider is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
#if WINDOWS
        WindowsPushNotificationService.Initialize();
#else
        var permission = await MainThread.InvokeOnMainThreadAsync(
            Permissions.RequestAsync<Permissions.PostNotifications>).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (permission != PermissionStatus.Granted)
        {
            return null;
        }
#endif

#if IOS
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UIKit.UIApplication.SharedApplication.RegisterForRemoteNotifications();
        });
#endif

        var token = await ResolveTokenAsync(provider, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        return new PushRegistration(token, provider, DateTimeOffset.UtcNow);
    }

    public async Task UnregisterAsync(CancellationToken cancellationToken = default)
    {
#if WINDOWS
        WindowsPushNotificationService.CloseChannels();
#endif
        await keyStore.RemoveAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PushRegistration?> GetCachedRegistrationAsync(CancellationToken cancellationToken = default)
    {
        var state = await keyStore.GetAsync(cancellationToken).ConfigureAwait(false);
        if (state is not null && (!state.RemoteSubscribed || !IsPlatformProviderSupported(state.Registration.Provider)))
        {
            await keyStore.RemoveAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (state is not null)
        {
            var latestToken = PushTokenBridge.Get(state.Registration.Provider);
            if (!string.IsNullOrWhiteSpace(latestToken) &&
                !string.Equals(latestToken, state.Registration.Token, StringComparison.Ordinal))
            {
                await keyStore.RemoveAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            return state.Registration;
        }

        return null;
    }

    public Task<PushNotificationKeyState?> GetAsync(CancellationToken cancellationToken = default) =>
        keyStore.GetAsync(cancellationToken);

    public async Task SetAsync(PushNotificationKeyState state, CancellationToken cancellationToken = default)
    {
        await keyStore.SetAsync(state, cancellationToken).ConfigureAwait(false);
#if WINDOWS
        if (string.Equals(state.Registration.Provider, "wns", StringComparison.OrdinalIgnoreCase))
        {
            WindowsPushNotificationService.CommitChannel(state.Registration.Token);
        }
#endif
    }

    public Task RemoveAsync(CancellationToken cancellationToken = default) =>
        keyStore.RemoveAsync(cancellationToken);

    public async Task<PushUnsubscribeRequest?> GetPendingUnsubscribeAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = await SecureStorage.Default.GetAsync(PendingUnsubscribeKey).ConfigureAwait(false);
        if (PushUnsubscribeRetryStateCodec.TryDecode(json, out var request))
        {
            return request;
        }

        if (!string.IsNullOrWhiteSpace(json))
        {
            SecureStorage.Default.Remove(PendingUnsubscribeKey);
        }

        return null;
    }

    public async Task SetPendingUnsubscribeAsync(
        PushUnsubscribeRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var encoded = PushUnsubscribeRetryStateCodec.Encode(request);
        if (!PushUnsubscribeRetryStateCodec.TryDecode(encoded, out _))
        {
            throw new ArgumentException("Invalid pending push unsubscribe request.", nameof(request));
        }

        await SecureStorage.Default.SetAsync(PendingUnsubscribeKey, encoded).ConfigureAwait(false);
#if ANDROID
        try
        {
            AndroidBackgroundSyncScheduler.ScheduleUnsubscribeRetry();
        }
        catch (Exception exception)
        {
            CrashDiagnostics.LogException("Push.ScheduleUnsubscribeRetry", exception);
        }
#endif
    }

    public Task RemovePendingUnsubscribeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SecureStorage.Default.Remove(PendingUnsubscribeKey);
        return Task.CompletedTask;
    }

    private static string? ResolveProvider()
    {
#if ANDROID
        return "fcm";
#elif IOS
        return "apns";
#elif WINDOWS
        return "wns";
#else
        return null;
#endif
    }

    private static bool IsPlatformProviderSupported(string provider)
    {
#if ANDROID
        return string.Equals(provider, "fcm", StringComparison.OrdinalIgnoreCase);
#elif IOS
        return string.Equals(provider, "apns", StringComparison.OrdinalIgnoreCase);
#elif WINDOWS
        return string.Equals(provider, "wns", StringComparison.OrdinalIgnoreCase);
#else
        return false;
#endif
    }

    private static Task<string?> ResolveTokenAsync(string provider, CancellationToken cancellationToken)
    {
#if WINDOWS
        return WindowsPushNotificationService.GetChannelUriAsync(cancellationToken);
#else
        var bridged = PushTokenBridge.Get(provider);
        if (!string.IsNullOrWhiteSpace(bridged))
        {
            return Task.FromResult<string?>(bridged);
        }

#if ANDROID
        return FirebaseTokenProvider.GetTokenAsync(cancellationToken);
#elif IOS
        return PushTokenBridge.WaitForTokenAsync(provider, TimeSpan.FromSeconds(30), cancellationToken);
#else
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(null);
#endif
#endif
    }
}

public enum PushAuthenticationStatus
{
    Rejected,
    Accepted,
    Replay
}

public readonly record struct PushAuthenticationResult(PushAuthenticationStatus Status, string? ReplayId = null);

public static class PushNotificationBackgroundHandler
{
    public static async Task<bool> IsAuthenticatedAsync(
        IReadOnlyDictionary<string, string> data,
        CancellationToken cancellationToken = default) =>
        (await AuthenticateAsync(data, cancellationToken).ConfigureAwait(false)).Status == PushAuthenticationStatus.Accepted;

    public static bool TryNormalizeEnvelopeData(
        IReadOnlyDictionary<string, string> data,
        out string encodedEnvelope,
        out string protocolVersion)
    {
        encodedEnvelope = string.Empty;
        protocolVersion = string.Empty;
        if (data.Count != 2 || !data.TryGetValue("enc_payload", out var encoded) ||
            !data.TryGetValue("spns", out var version) ||
            version != PushNotificationCrypto.EnvelopeVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
            string.IsNullOrWhiteSpace(encoded) || encoded.Length > 8192)
        {
            return false;
        }

        encodedEnvelope = encoded;
        protocolVersion = version;
        return true;
    }

    public static Task<PushAuthenticationResult> AuthenticateAsync(
        IReadOnlyDictionary<string, string> data,
        CancellationToken cancellationToken = default) =>
        AuthenticateCoreAsync(data, beforePersistAcceptance: null, cancellationToken);

    public static Task<PushAuthenticationResult> AuthenticateAsync(
        IReadOnlyDictionary<string, string> data,
        Action beforePersistAcceptance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(beforePersistAcceptance);
        return AuthenticateCoreAsync(data, beforePersistAcceptance, cancellationToken);
    }

    private static async Task<PushAuthenticationResult> AuthenticateCoreAsync(
        IReadOnlyDictionary<string, string> data,
        Action? beforePersistAcceptance,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeEnvelopeData(data, out var encoded, out _))
        {
            return new(PushAuthenticationStatus.Rejected);
        }

        byte[] envelope;
        try
        {
            envelope = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            return new(PushAuthenticationStatus.Rejected);
        }

        if (envelope.Length > PushNotificationCrypto.MaxEnvelopeBytes)
        {
            return new(PushAuthenticationStatus.Rejected);
        }

        var state = await new MauiPushNotificationKeyStore().GetAsync(cancellationToken).ConfigureAwait(false);
        if (state is null || state.KeyHex.Length != PushNotificationCrypto.KeySizeBytes * 2 ||
            string.IsNullOrWhiteSpace(state.SubscriptionBinding) || string.IsNullOrWhiteSpace(state.SessionBinding))
        {
            return new(PushAuthenticationStatus.Rejected);
        }

        byte[]? key = null;
        byte[]? plaintext = null;
        try
        {
            key = Convert.FromHexString(state.KeyHex);
            if (!PushNotificationCrypto.TryDecrypt(
                    envelope,
                    key,
                    state.SubscriptionBinding,
                    state.SessionBinding,
                    out plaintext) ||
                !PushNotificationCrypto.TryDecodePayload(plaintext, out var payload) ||
                payload is null)
            {
                return new(PushAuthenticationStatus.Rejected);
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!PushNotificationCrypto.IsPayloadCurrentlyValid(payload, now))
            {
                return new(PushAuthenticationStatus.Rejected);
            }

            var replayId = PushNotificationCrypto.ComputeReplayId(payload, state.SubscriptionBinding);
            return MauiPushNotificationReplayStore.TryAccept(
                replayId,
                payload.Expiration,
                now,
                beforePersistAcceptance)
                ? new(PushAuthenticationStatus.Accepted, replayId)
                : new(PushAuthenticationStatus.Replay, replayId);
        }
        finally
        {
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }

            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }
}
