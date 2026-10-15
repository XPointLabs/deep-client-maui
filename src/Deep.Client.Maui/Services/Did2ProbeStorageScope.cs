namespace Deep.Client.Maui.Services;

/// <summary>Compile-time diagnostic custody; never reads another lane or network's state.</summary>
internal static class Did2ProbeStorageScope
{
    internal static string ResolveCanaryRoot(string localAppData, ReadOnlySpan<byte> networkId)
        => ResolveRoot(localAppData, networkId, "DeepDid2CanaryProbe");

    internal static string ResolvePhysicalRoot(string localAppData, ReadOnlySpan<byte> networkId)
        => ResolveRoot(localAppData, networkId, "DeepDid2Physical");

    // The caller supplies the canonical OS-owned app files directory, not a
    // runtime/environment override. Every app-owned component is checked;
    // foreign OS ancestors above that boundary are deliberately not inspected.
    internal static void RequireOwnedPhysicalPath(string canonicalAppDataRoot, string path)
    {
        if (!Path.IsPathFullyQualified(canonicalAppDataRoot) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Physical custody requires absolute app-owned paths.");
        var boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(canonicalAppDataRoot));
        var target = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!target.StartsWith(boundary + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException("Physical custody must stay below the app files directory.");
        for (var current = new DirectoryInfo(target); current is not null; current = current.Parent)
        {
            if (current.LinkTarget is not null || File.Exists(current.FullName) ||
                (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new InvalidOperationException("App-owned physical custody must not contain redirects or files.");
            if (string.Equals(current.FullName, boundary, comparison)) return;
        }
        throw new InvalidOperationException("Physical custody boundary was not reached.");
    }

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
