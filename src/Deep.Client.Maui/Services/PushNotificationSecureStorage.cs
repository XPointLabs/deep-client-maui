using Deep.Client.Shared.Services;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui.Services;

public sealed class MauiPushNotificationKeyStore : IPushNotificationEncryptionKeyStore
{
    public const string StateKey = "push.registration.v2";

    public async Task<PushNotificationKeyState?> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = await SecureStorage.Default.GetAsync(StateKey).ConfigureAwait(false);
        return PushNotificationKeyStateCodec.TryDecode(json, out var state) ? state : null;
    }

    public async Task SetAsync(PushNotificationKeyState state, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!PushNotificationKeyStateCodec.TryDecode(PushNotificationKeyStateCodec.Encode(state), out _))
        {
            throw new ArgumentException("Invalid push key state.", nameof(state));
        }

        await SecureStorage.Default.SetAsync(StateKey, PushNotificationKeyStateCodec.Encode(state)).ConfigureAwait(false);
    }

    public Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SecureStorage.Default.Remove(StateKey);
        return Task.CompletedTask;
    }
}
