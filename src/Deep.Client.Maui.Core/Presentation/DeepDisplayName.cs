using Deep.Client.Shared.Domain;

namespace Deep.Client.Maui.Core.Presentation;

public static class DeepDisplayName
{
    public static string AvatarInitial(string? displayName, string? fallbackSeed = null)
    {
        var trimmed = displayName?.Trim();
        var source = LooksLikeRawSessionId(trimmed)
            ? null
            : trimmed;

        if (string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(fallbackSeed))
        {
            source = CompactSuffix(fallbackSeed.Trim());
        }

        foreach (var ch in source ?? string.Empty)
        {
            if (char.IsLetterOrDigit(ch))
            {
                return char.ToUpperInvariant(ch).ToString();
            }
        }

        return "D";
    }

    public static string ContactTitleOrFallback(SessionId sessionId, string? displayName)
    {
        var title = ContactTitle(sessionId, displayName);
        return string.IsNullOrWhiteSpace(title)
            ? $"Deep {CompactSuffix(sessionId.Value)}"
            : title;
    }

    public static string ContactTitle(SessionId sessionId, string? displayName)
    {
        var trimmed = displayName?.Trim();
        return LooksLikeRawSessionId(trimmed, sessionId)
            ? string.Empty
            : trimmed!;
    }

    public static string LocalNameOrEmpty(SessionId sessionId, string? displayName)
    {
        var trimmed = displayName?.Trim();
        return LooksLikeRawSessionId(trimmed, sessionId) ? string.Empty : trimmed ?? string.Empty;
    }

    public static bool LooksLikeRawSessionId(string? value, SessionId? expected = null)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return true;
        }

        if (expected is not null && string.Equals(trimmed, expected.Value.Value, StringComparison.Ordinal))
        {
            return true;
        }

        if (trimmed.Length == 11
            && trimmed.StartsWith("Deep ", StringComparison.Ordinal)
            && trimmed[5..].All(IsHex))
        {
            return true;
        }

        return trimmed.Length >= 48 && trimmed.All(static ch =>
            IsHex(ch));
    }

    public static string ShortId(string value)
    {
        if (value.Length <= 12)
        {
            return value;
        }

        return $"{value[..6]}...{value[^4..]}";
    }

    private static string CompactSuffix(string value) =>
        value.Length <= 6 ? value : value[^6..];

    private static bool IsHex(char ch) =>
        ch is >= '0' and <= '9'
        || ch is >= 'a' and <= 'f'
        || ch is >= 'A' and <= 'F';
}
