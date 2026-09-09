using System.Reflection;

namespace Deep.Client.Maui.Services;

internal static class ActiveBuildNetworkId
{
    internal const string DevelopmentKey = "DeepDevelopmentNetworkId";
    internal const string DevelopmentNetworkIdHex = "3e078ce750768322d79079ef97b85b56";
    internal const string ProductionKey = "DeepProductionNetworkId";
    internal const string PhysicalUatKey = "DeepPhysicalUatNetworkId";

    internal static ReadOnlyMemory<byte> Load()
    {
#if DEBUG && DEEP_PHYSICAL_E2E
        const string activeKey = PhysicalUatKey;
#elif DEBUG
        const string activeKey = DevelopmentKey;
#else
        const string activeKey = ProductionKey;
#endif
        var matches = typeof(ActiveBuildNetworkId).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute => string.Equals(attribute.Key, activeKey, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"Active build network ID metadata '{activeKey}' must occur exactly once.");
        }

        var activeValue = matches[0].Value;
#if DEBUG && !DEEP_PHYSICAL_E2E
        if (!string.Equals(activeValue, DevelopmentNetworkIdHex, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Development builds must use the canonical survival P10E network ID.");
        }
#endif
        return ParseExact(activeValue, activeKey);
    }

    internal static byte[] ParseExact(string? value, string sourceName)
    {
        if (value is null
            || value.Length != 32
            || value.Any(static character => character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
        {
            throw new InvalidOperationException(
                $"Build network ID '{sourceName}' must be exactly 16 bytes of lowercase hexadecimal.");
        }

        var parsed = Convert.FromHexString(value);
        if (parsed.AsSpan().IndexOfAnyExcept((byte)0) < 0)
        {
            throw new InvalidOperationException(
                $"Build network ID '{sourceName}' must not be all zero.");
        }

        return parsed;
    }
}
