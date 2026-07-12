using Deep.Client.Maui.Services;
using Deep.Client.Shared.Platform;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class PushNotificationSecureStorageTests
{
    [Fact]
    public void StateCodecRoundTripsAtomicRegistrationState()
    {
        var state = new PushNotificationKeyState(
            new string('a', 64),
            new string('b', 64),
            "05" + new string('c', 64),
            new PushRegistration("device-token", "fcm", DateTimeOffset.Parse("2026-07-10T00:00:00Z")),
            RemoteSubscribed: true);

        var encoded = PushNotificationKeyStateCodec.Encode(state);

        Assert.True(PushNotificationKeyStateCodec.TryDecode(encoded, out var decoded));
        Assert.Equal(state, decoded);
    }

    [Fact]
    public void StateCodecRejectsMalformedOrOversizedKeyState()
    {
        Assert.False(PushNotificationKeyStateCodec.TryDecode("{\"KeyHex\":\"plaintext\"}", out _));
        Assert.False(PushNotificationKeyStateCodec.TryDecode(new string('x', 17 * 1024), out _));
    }

    [Fact]
    public void StateCodecRejectsStateThatWasNotConfirmedByTheRemoteServer()
    {
        var state = new PushNotificationKeyState(
            new string('a', 64),
            new string('b', 64),
            "05" + new string('c', 64),
            new PushRegistration("device-token", "fcm", DateTimeOffset.Parse("2026-07-10T00:00:00Z")));

        Assert.False(PushNotificationKeyStateCodec.TryDecode(PushNotificationKeyStateCodec.Encode(state), out _));
    }

    [Fact]
    public void UnsubscribeRetryCodecRoundTripsSignedRequestWithoutIdentitySeed()
    {
        var request = new PushUnsubscribeRequest(
            "05" + new string('a', 64),
            new string('b', 64),
            "firebase",
            1_800_000_000,
            Convert.ToBase64String(new byte[64]),
            new PushSubscriptionServiceInfo("device-token"));

        var encoded = PushUnsubscribeRetryStateCodec.Encode(request);

        Assert.True(PushUnsubscribeRetryStateCodec.TryDecode(encoded, out var decoded));
        Assert.Equal(request, decoded);
        Assert.DoesNotContain("seed", encoded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recovery", encoded, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnsubscribeRetryCodecRejectsMalformedOrUnsignedState()
    {
        Assert.False(PushUnsubscribeRetryStateCodec.TryDecode("{}", out _));
        Assert.False(PushUnsubscribeRetryStateCodec.TryDecode(new string('x', 17 * 1024), out _));
    }

    [Fact]
    public void UnsubscribeRetryCodecAcceptsOnlyCanonicalWnsChannelUris()
    {
        var request = new PushUnsubscribeRequest(
            "05" + new string('a', 64),
            new string('b', 64),
            "wns",
            1_800_000_000,
            Convert.ToBase64String(new byte[64]),
            new PushSubscriptionServiceInfo("https://wns2-by3p.notify.windows.com/?token=opaque"));

        Assert.True(PushUnsubscribeRetryStateCodec.TryDecode(
            PushUnsubscribeRetryStateCodec.Encode(request),
            out var decoded));
        Assert.Equal(request, decoded);

        var forged = request with
        {
            ServiceInfo = new PushSubscriptionServiceInfo(
                "https://notify.windows.com.evil.example/?token=opaque")
        };
        Assert.False(PushUnsubscribeRetryStateCodec.TryDecode(
            PushUnsubscribeRetryStateCodec.Encode(forged),
            out _));
    }
}
