using System.Text.Json;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Services;

public static class PushNotificationKeyStateCodec
{
    public static string Encode(PushNotificationKeyState state) => JsonSerializer.Serialize(state, JsonOptions);

    public static bool TryDecode(string? json, out PushNotificationKeyState? state)
    {
        state = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > 16 * 1024)
        {
            return false;
        }

        try
        {
            var candidate = JsonSerializer.Deserialize<PushNotificationKeyState>(json, JsonOptions);
            if (candidate is null || !candidate.RemoteSubscribed || candidate.KeyHex is null ||
                candidate.KeyHex.Length != PushNotificationCrypto.KeySizeBytes * 2 || !IsLowerHex(candidate.KeyHex) ||
                candidate.SubscriptionBinding is null || candidate.SubscriptionBinding.Length != 64 || !IsLowerHex(candidate.SubscriptionBinding) ||
                candidate.SessionBinding is null || candidate.SessionBinding.Length != 66 ||
                !candidate.SessionBinding.StartsWith("05", StringComparison.Ordinal) || !IsLowerHex(candidate.SessionBinding) ||
                candidate.Registration is null ||
                string.IsNullOrWhiteSpace(candidate.Registration.Token) ||
                candidate.Registration.Token != candidate.Registration.Token.Trim() ||
                string.IsNullOrWhiteSpace(candidate.Registration.Provider))
            {
                return false;
            }

            state = candidate;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static bool IsLowerHex(string value) =>
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
