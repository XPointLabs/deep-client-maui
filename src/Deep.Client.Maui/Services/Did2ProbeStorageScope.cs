namespace Deep.Client.Maui.Services;

/// <summary>Compile-time Windows canary custody; never reads another network's probe state.</summary>
internal static class Did2ProbeStorageScope
{
    internal static string ResolveCanaryRoot(string localAppData, ReadOnlySpan<byte> networkId)
    {
        if (string.IsNullOrWhiteSpace(localAppData) || !Path.IsPathFullyQualified(localAppData))
            throw new ArgumentException("The canary requires an absolute local application-data root.", nameof(localAppData));
        if (networkId.Length != 16 || networkId.IndexOfAnyExcept((byte)0) < 0)
            throw new ArgumentException("The canary requires a nonzero 16-byte network ID.", nameof(networkId));
        return Path.GetFullPath(Path.Combine(localAppData, "XPointLabs", "DeepDid2CanaryProbe",
            Convert.ToHexString(networkId).ToLowerInvariant()));
    }
}
