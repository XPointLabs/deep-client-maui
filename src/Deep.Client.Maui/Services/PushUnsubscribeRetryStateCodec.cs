using System.Text.Json;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Services;

public static class PushUnsubscribeRetryStateCodec
{
    private const int MaxStateLength = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Encode(PushUnsubscribeRequest request) =>
        JsonSerializer.Serialize(request, JsonOptions);

    public static bool TryDecode(string? json, out PushUnsubscribeRequest? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxStateLength)
        {
            return false;
        }

        try
        {
            var candidate = JsonSerializer.Deserialize<PushUnsubscribeRequest>(json, JsonOptions);
            if (!IsValid(candidate))
            {
                return false;
            }

            request = candidate;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsValid(PushUnsubscribeRequest? request)
    {
        if (request is null || request.SigVersion != 2 || request.SigTs <= 0 ||
            request.Pubkey is not { Length: 66 } || !request.Pubkey.StartsWith("05", StringComparison.Ordinal) ||
            !IsLowerHex(request.Pubkey) || request.SessionEd25519 is not { Length: 64 } ||
            !IsLowerHex(request.SessionEd25519) ||
            !IsSupportedService(request.Service, request.ServiceInfo?.Token) ||
            request.ServiceInfo is null || string.IsNullOrWhiteSpace(request.ServiceInfo.Token) ||
            request.ServiceInfo.Token.Length > 4096 ||
            request.ServiceInfo.Token != request.ServiceInfo.Token.Trim())
        {
            return false;
        }

        try
        {
            return !string.IsNullOrWhiteSpace(request.Signature) &&
                   Convert.FromBase64String(request.Signature).Length == 64;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsLowerHex(string value) =>
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSupportedService(string? service, string? token) => service switch
    {
        "firebase" => true,
        "wns" => IsValidWnsChannelUri(token),
        _ => false
    };

    private static bool IsValidWnsChannelUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            !uri.IsDefaultPort || string.IsNullOrEmpty(uri.Query))
        {
            return false;
        }

        return uri.Host.Equals("notify.windows.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".notify.windows.com", StringComparison.OrdinalIgnoreCase);
    }
}
