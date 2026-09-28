namespace Deep.Client.Maui.Services;

/// <summary>Compile-time Windows diagnostic custody; never reads another lane or network's state.</summary>
internal static class Did2ProbeStorageScope
{
    internal static string ResolveCanaryRoot(string localAppData, ReadOnlySpan<byte> networkId)
        => ResolveRoot(localAppData, networkId, "DeepDid2CanaryProbe");

    internal static string ResolvePhysicalRoot(string localAppData, ReadOnlySpan<byte> networkId)
        => ResolveRoot(localAppData, networkId, "DeepDid2Physical");

    private static string ResolveRoot(string localAppData, ReadOnlySpan<byte> networkId,
        string lane)
    {
        if (string.IsNullOrWhiteSpace(localAppData) || !Path.IsPathFullyQualified(localAppData))
            throw new ArgumentException("The diagnostic requires an absolute local application-data root.", nameof(localAppData));
        if (networkId.Length != 16 || networkId.IndexOfAnyExcept((byte)0) < 0)
            throw new ArgumentException("The diagnostic requires a nonzero 16-byte network ID.", nameof(networkId));
        return Path.GetFullPath(Path.Combine(localAppData, "XPointLabs", lane,
            Convert.ToHexString(networkId).ToLowerInvariant()));
    }
}
