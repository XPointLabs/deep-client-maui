using System.Text.Json;

namespace Deep.Client.Maui.Services;

public static class WnsPushPayloadCodec
{
    public const int MaxPayloadBytes = 5_000;

    public static bool TryDecode(
        ReadOnlyMemory<byte> payload,
        out IReadOnlyDictionary<string, string> data)
    {
        data = new Dictionary<string, string>();
        if (payload.IsEmpty || payload.Length > MaxPayloadBytes)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var result = new Dictionary<string, string>(2, StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if ((property.Name is not "enc_payload" and not "spns") ||
                    property.Value.ValueKind != JsonValueKind.String ||
                    !result.TryAdd(property.Name, property.Value.GetString() ?? string.Empty))
                {
                    return false;
                }
            }

            if (result.Count != 2 ||
                !result.TryGetValue("enc_payload", out var encodedEnvelope) ||
                string.IsNullOrWhiteSpace(encodedEnvelope) || encodedEnvelope.Length > 8192 ||
                !result.TryGetValue("spns", out var version) || version != "1")
            {
                return false;
            }

            data = result;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public static class WnsChannelUriValidator
{
    public static bool IsValid(string? value)
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
