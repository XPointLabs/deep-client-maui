using System.Net;

namespace Deep.Client.Maui.Core.Services;

public sealed class RoutedRuntimeEndpointPolicy
{
    private RoutedRuntimeEndpointPolicy()
    {
    }

    public static RoutedRuntimeEndpointPolicy Production { get; } = new();
}

public sealed record RealityRouterEndpoint(string BaseUrl, string ExpectedRouterId);

public static class RoutedRuntimeConfiguration
{
    public const int MinimumRouterCount = 3;
    public const int MaximumRouterCount = 16;

    public static IReadOnlyList<RealityRouterEndpoint> ParseAtLeastThree(
        string raw,
        RoutedRuntimeEndpointPolicy? endpointPolicy = null)
    {
        var policy = endpointPolicy ?? RoutedRuntimeEndpointPolicy.Production;
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException(
                $"XNODE_URLS must contain between {MinimumRouterCount} and {MaximumRouterCount} pinned router entries.");
        }

        var endpoints = Split(raw)
            .Select(value => ParseEndpoint(value, policy))
            .ToArray();
        return ValidateAtLeastThree(endpoints, policy);
    }

    public static IReadOnlyList<RealityRouterEndpoint> ValidateAtLeastThree(
        IEnumerable<RealityRouterEndpoint> endpoints,
        RoutedRuntimeEndpointPolicy? endpointPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var policy = endpointPolicy ?? RoutedRuntimeEndpointPolicy.Production;
        var normalized = endpoints
            .Select(endpoint => NormalizeEndpoint(endpoint, policy))
            .ToArray();

        if (normalized.Length < MinimumRouterCount || normalized.Length > MaximumRouterCount)
        {
            throw new InvalidOperationException(
                $"XNODE_URLS must contain between {MinimumRouterCount} and {MaximumRouterCount} pinned router entries.");
        }

        if (normalized
                .Select(static endpoint => endpoint.ExpectedRouterId)
                .Distinct(StringComparer.Ordinal)
                .Count() != normalized.Length)
        {
            throw new InvalidOperationException(
                "XNODE_URLS must contain unique lowercase router IDs.");
        }

        if (normalized
                .Select(static endpoint => endpoint.BaseUrl)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != normalized.Length)
        {
            throw new InvalidOperationException(
                "XNODE_URLS must contain unique absolute router URLs.");
        }

        return normalized;
    }

    public static Uri RequireLiveServiceUrl(
        string settingName,
        string? raw,
        RoutedRuntimeEndpointPolicy? endpointPolicy = null)
    {
        var policy = endpointPolicy ?? RoutedRuntimeEndpointPolicy.Production;
        if (string.IsNullOrWhiteSpace(raw) ||
            !Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            !IsAllowedLiveUri(uri, policy))
        {
            throw new InvalidOperationException(
                $"{settingName} must be HTTPS or explicit loopback HTTP.");
        }

        return uri;
    }

    private static RealityRouterEndpoint ParseEndpoint(
        string value,
        RoutedRuntimeEndpointPolicy endpointPolicy)
    {
        var separator = value.IndexOf('|');
        if (separator <= 0 || separator == value.Length - 1)
        {
            throw new InvalidOperationException(
                "XNODE_URLS entries must use '<64-lowerhex-routerId>|<absolute-url>'.");
        }

        return NormalizeEndpoint(
            new RealityRouterEndpoint(
                value[(separator + 1)..].Trim(),
                value[..separator].Trim()),
            endpointPolicy);
    }

    private static RealityRouterEndpoint NormalizeEndpoint(
        RealityRouterEndpoint endpoint,
        RoutedRuntimeEndpointPolicy endpointPolicy)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var routerId = endpoint.ExpectedRouterId?.Trim() ?? string.Empty;
        if (routerId.Length != 64 ||
            routerId.Any(static value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new InvalidOperationException(
                "XNODE_URLS router IDs must be exactly 64 lowercase hexadecimal characters.");
        }

        var uri = RequireLiveServiceUrl(
            "XNODE_URLS",
            endpoint.BaseUrl,
            endpointPolicy);
        if (uri.AbsolutePath != "/")
        {
            throw new InvalidOperationException(
                "XNODE_URLS router base URLs must use the root path.");
        }

        return new RealityRouterEndpoint(uri.AbsoluteUri, routerId);
    }

    private static bool IsAllowedLiveUri(
        Uri uri,
        RoutedRuntimeEndpointPolicy endpointPolicy)
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

        if (uri.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(uri.DnsSafeHost, out var address))
        {
            return false;
        }
        if (IPAddress.IsLoopback(address))
        {
            return address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                   HasCanonicalIpv4Host(uri, address);
        }
        return false;
    }

    private static bool HasCanonicalIpv4Host(Uri uri, IPAddress address)
    {
        var original = uri.OriginalString;
        var schemeEnd = original.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return false;
        }

        var authorityStart = schemeEnd + 3;
        var authorityEnd = original.IndexOfAny(['/', '?', '#'], authorityStart);
        var authority = authorityEnd < 0
            ? original[authorityStart..]
            : original[authorityStart..authorityEnd];
        var portSeparator = authority.LastIndexOf(':');
        var host = portSeparator < 0
            ? authority
            : authority[..portSeparator];
        return string.Equals(host, address.ToString(), StringComparison.Ordinal);
    }

    private static string[] Split(string raw) =>
        raw.Split(
            [';', ',', '\n', '\r', '\t', ' '],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
