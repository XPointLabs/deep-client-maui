using Deep.Client.Shared.Services;
using System.Net;

namespace Deep.Client.Maui.Core.Services;

public static class RoutedRuntimeConfiguration
{
    public const int RequiredRouterCount = 3;

    public static IReadOnlyList<PinnedRouterEndpoint> ParseExactlyThree(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException(
                "XNODE_URLS must contain exactly three pinned router entries.");
        }

        var endpoints = Split(raw)
            .Select(ParseEndpoint)
            .ToArray();
        return ValidateExactlyThree(endpoints);
    }

    public static IReadOnlyList<PinnedRouterEndpoint> ValidateExactlyThree(
        IEnumerable<PinnedRouterEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var normalized = endpoints
            .Select(static endpoint => NormalizeEndpoint(endpoint))
            .ToArray();

        if (normalized.Length != RequiredRouterCount)
        {
            throw new InvalidOperationException(
                "XNODE_URLS must contain exactly three pinned router entries.");
        }

        if (normalized
                .Select(static endpoint => endpoint.ExpectedRouterId)
                .Distinct(StringComparer.Ordinal)
                .Count() != RequiredRouterCount)
        {
            throw new InvalidOperationException(
                "XNODE_URLS must contain exactly three unique lowercase router IDs.");
        }

        if (normalized
                .Select(static endpoint => endpoint.BaseUrl)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != RequiredRouterCount)
        {
            throw new InvalidOperationException(
                "XNODE_URLS must contain exactly three unique absolute router URLs.");
        }

        return normalized;
    }

    public static void RejectDirectStorageForRoutedComposition(string? storageUrl)
    {
        if (!string.IsNullOrWhiteSpace(storageUrl))
        {
            throw new InvalidOperationException(
                "DEEP_STORAGE_URL must be absent when routed XNODE_URLS composition is active.");
        }
    }

    public static Uri RequireLiveServiceUrl(string settingName, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) ||
            !Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            !IsAllowedLiveUri(uri))
        {
            throw new InvalidOperationException(
                $"{settingName} must be HTTPS or explicit loopback HTTP.");
        }

        return uri;
    }

    private static PinnedRouterEndpoint ParseEndpoint(string value)
    {
        var separator = value.IndexOf('|');
        if (separator <= 0 || separator == value.Length - 1)
        {
            throw new InvalidOperationException(
                "XNODE_URLS entries must use '<64-lowerhex-routerId>|<absolute-url>'.");
        }

        return NormalizeEndpoint(new PinnedRouterEndpoint(
            value[(separator + 1)..].Trim(),
            value[..separator].Trim()));
    }

    private static PinnedRouterEndpoint NormalizeEndpoint(PinnedRouterEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var routerId = endpoint.ExpectedRouterId?.Trim() ?? string.Empty;
        if (routerId.Length != 64 ||
            routerId.Any(static value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new InvalidOperationException(
                "XNODE_URLS router IDs must be exactly 64 lowercase hexadecimal characters.");
        }

        var uri = RequireLiveServiceUrl("XNODE_URLS", endpoint.BaseUrl);
        if (uri.AbsolutePath != "/")
        {
            throw new InvalidOperationException(
                "XNODE_URLS router base URLs must use the root path.");
        }

        return new PinnedRouterEndpoint(uri.AbsoluteUri, routerId);
    }

    private static bool IsAllowedLiveUri(Uri uri)
    {
        if (string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        return uri.Scheme == Uri.UriSchemeHttp &&
            IPAddress.TryParse(uri.DnsSafeHost, out var address) &&
            IPAddress.IsLoopback(address);
    }

    private static string[] Split(string raw) =>
        raw.Split(
            [';', ',', '\n', '\r', '\t', ' '],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
