using Deep.Client.Shared.Services;
using System.Net;

namespace Deep.Client.Maui.Core.Services;

public sealed class RoutedRuntimeEndpointPolicy
{
    private RoutedRuntimeEndpointPolicy(bool allowDevLocalIpv4Http)
    {
        AllowsDevLocalIpv4Http = allowDevLocalIpv4Http;
    }

    public bool AllowsDevLocalIpv4Http { get; }

    public static RoutedRuntimeEndpointPolicy Production { get; } = new(false);

    public static RoutedRuntimeEndpointPolicy PhysicalE2eDevelopment { get; } = new(true);
}

public static class RoutedRuntimeConfiguration
{
    public const int MinimumRouterCount = 3;
    public const int MaximumRouterCount = 16;

    public static IReadOnlyList<PinnedRouterEndpoint> ParseAtLeastThree(
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

    public static IReadOnlyList<PinnedRouterEndpoint> ValidateAtLeastThree(
        IEnumerable<PinnedRouterEndpoint> endpoints,
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

    public static void RejectDirectStorageForRoutedComposition(string? storageUrl)
    {
        if (!string.IsNullOrWhiteSpace(storageUrl))
        {
            throw new InvalidOperationException(
                "DEEP_STORAGE_URL must be absent when routed XNODE_URLS composition is active.");
        }
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
                policy.AllowsDevLocalIpv4Http
                    ? $"{settingName} must be HTTPS, explicit loopback HTTP, or canonical development-local IPv4 HTTP."
                    : $"{settingName} must be HTTPS or explicit loopback HTTP.");
        }

        return uri;
    }

    private static PinnedRouterEndpoint ParseEndpoint(
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
            new PinnedRouterEndpoint(
                value[(separator + 1)..].Trim(),
                value[..separator].Trim()),
            endpointPolicy);
    }

    private static PinnedRouterEndpoint NormalizeEndpoint(
        PinnedRouterEndpoint endpoint,
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

        return new PinnedRouterEndpoint(uri.AbsoluteUri, routerId);
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
            return !endpointPolicy.AllowsDevLocalIpv4Http ||
                   address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                   HasCanonicalIpv4Host(uri, address);
        }
        if (!endpointPolicy.AllowsDevLocalIpv4Http ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            !HasCanonicalIpv4Host(uri, address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               bytes[0] == 169 && bytes[1] == 254 ||
               bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
               bytes[0] == 192 && bytes[1] == 168;
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
