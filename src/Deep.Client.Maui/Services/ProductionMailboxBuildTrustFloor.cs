using System.Globalization;
using System.Reflection;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Services;

/// <summary>
/// Reads only build-generated assembly metadata. Runtime environment and downloaded files are
/// intentionally not trust-root sources.
/// </summary>
internal static class ProductionMailboxBuildTrustFloor
{
    internal const string MrXKey = "DeepProductionMrXPublicKeySha256";
    internal const string NetworkKey = "DeepProductionNetworkId";
    internal const string AuthorityGenerationKey = "DeepProductionAuthorityGeneration";
    internal const string AuthorityHashKey = "DeepProductionAuthorityHash";
    internal const string RevocationGenerationKey = "DeepProductionRevocationGeneration";
    internal const string RevocationHeadHashKey = "DeepProductionRevocationHeadHash";
    internal const string RevocationSnapshotHashKey = "DeepProductionRevocationSnapshotHash";
    internal const string TopologyGenerationKey = "DeepProductionTopologyGeneration";
    internal const string TopologyHashKey = "DeepProductionTopologyHash";
    internal const string AndroidApplicationIdKey = "DeepProductionAndroidApplicationId";
    internal const string AndroidVersionCodeKey = "DeepProductionAndroidVersionCode";
    internal const string AndroidSignerLineageKey =
        "DeepProductionAndroidSignerLineageSha256";

    private static readonly string[] Keys =
    [
        MrXKey,
        NetworkKey,
        AuthorityGenerationKey,
        AuthorityHashKey,
        RevocationGenerationKey,
        RevocationHeadHashKey,
        RevocationSnapshotHashKey,
        TopologyGenerationKey,
        TopologyHashKey
    ];

    private static readonly string[] AndroidIdentityKeys =
    [
        AndroidApplicationIdKey,
        AndroidVersionCodeKey,
        AndroidSignerLineageKey
    ];

    public static bool TryLoad(out ProductionMailboxTrustAnchor? anchor)
    {
        var values = typeof(ProductionMailboxBuildTrustFloor).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute => Keys.Contains(attribute.Key, StringComparer.Ordinal))
            .GroupBy(attribute => attribute.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Count() == 1 ? group.Single().Value : null,
                StringComparer.Ordinal);
        return TryParse(values, out anchor);
    }

    internal static bool TryParse(
        IReadOnlyDictionary<string, string?> values,
        out ProductionMailboxTrustAnchor? anchor)
    {
        ArgumentNullException.ThrowIfNull(values);
        anchor = null;
        if (Keys.All(key => !values.TryGetValue(key, out var value) ||
                string.IsNullOrEmpty(value)))
            return false;
        if (Keys.Any(key => !values.TryGetValue(key, out var value) ||
                string.IsNullOrEmpty(value)))
            throw Unavailable();

        try
        {
            anchor = new ProductionMailboxTrustAnchor(
                Hex(values[MrXKey]!, 32),
                Hex(values[NetworkKey]!, 16),
                Generation(values[AuthorityGenerationKey]!),
                Hex(values[AuthorityHashKey]!, 32),
                Generation(values[RevocationGenerationKey]!),
                Hex(values[RevocationHeadHashKey]!, 32),
                Hex(values[RevocationSnapshotHashKey]!, 32),
                Generation(values[TopologyGenerationKey]!),
                Hex(values[TopologyHashKey]!, 32));
            _ = ProductionMailboxTrustStateCodec.Encode(
                new ProductionMailboxTrustState(1, anchor, anchor, anchor));
            return true;
        }
        catch (Exception exception) when (exception is FormatException or
            OverflowException or InvalidDataException)
        {
            anchor = null;
            throw Unavailable();
        }
    }

    public static bool TryLoadAndroidIdentity(
        out ProductionAndroidBuildIdentity? identity)
    {
        var values = typeof(ProductionMailboxBuildTrustFloor).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute => AndroidIdentityKeys.Contains(
                attribute.Key, StringComparer.Ordinal))
            .GroupBy(attribute => attribute.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Count() == 1 ? group.Single().Value : null,
                StringComparer.Ordinal);
        return TryParseAndroidIdentity(values, out identity);
    }

    internal static bool TryParseAndroidIdentity(
        IReadOnlyDictionary<string, string?> values,
        out ProductionAndroidBuildIdentity? identity)
    {
        ArgumentNullException.ThrowIfNull(values);
        identity = null;
        if (AndroidIdentityKeys.All(key =>
                !values.TryGetValue(key, out var value) || string.IsNullOrEmpty(value)))
            return false;
        if (AndroidIdentityKeys.Any(key =>
                !values.TryGetValue(key, out var value) || string.IsNullOrEmpty(value)))
            throw Unavailable();
        try
        {
            var applicationId = values[AndroidApplicationIdKey]!;
            if (!string.Equals(
                    applicationId,
                    ProductionMailboxControlPlaneVerifier.AndroidApplicationIdentity,
                    StringComparison.Ordinal))
                throw new FormatException();
            var lineage = values[AndroidSignerLineageKey]!
                .Split('|', StringSplitOptions.None)
                .Select(value => Hex(value, 32))
                .ToArray();
            if (lineage.Length is < 1 or > 32 ||
                lineage.Select(Convert.ToHexStringLower)
                    .Distinct(StringComparer.Ordinal).Count() != lineage.Length)
                throw new FormatException();
            identity = new ProductionAndroidBuildIdentity(
                applicationId,
                Generation(values[AndroidVersionCodeKey]!),
                lineage);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            identity = null;
            throw Unavailable();
        }
    }

    internal static void VerifyInstalledAndroidTuple(
        ProductionAndroidBuildIdentity expected,
        string? applicationId,
        ulong versionCode,
        IReadOnlyList<ReadOnlyMemory<byte>> signerLineageSha256)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(signerLineageSha256);
        if (!string.Equals(applicationId, expected.ApplicationId, StringComparison.Ordinal) ||
            versionCode != expected.VersionCode ||
            signerLineageSha256.Count != expected.SignerLineageSha256.Count ||
            signerLineageSha256.Where((hash, index) =>
                hash.Length != 32 || !System.Security.Cryptography.CryptographicOperations
                    .FixedTimeEquals(hash.Span, expected.SignerLineageSha256[index].Span)).Any())
            throw new InvalidDataException(
                "Production Android package/version/Play signer tuple is not approved.");
    }

    private static byte[] Hex(string value, int bytes)
    {
        if (value.Length != bytes * 2 ||
            value.Any(character => character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')))
            throw new FormatException();
        var decoded = Convert.FromHexString(value);
        if (decoded.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            throw new FormatException();
        return decoded;
    }

    private static ulong Generation(string value)
    {
        if (value.Length == 0 || value[0] == '0' ||
            !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
                out var parsed) || parsed == 0)
            throw new FormatException();
        return parsed;
    }

    private static InvalidOperationException Unavailable() =>
        new("production-credentials-unavailable");
}

internal sealed class ProductionAndroidBuildIdentity
{
    private readonly byte[][] signerLineageSha256;

    public ProductionAndroidBuildIdentity(
        string applicationId,
        ulong versionCode,
        IEnumerable<byte[]> signerLineageSha256)
    {
        ApplicationId = applicationId;
        VersionCode = versionCode;
        this.signerLineageSha256 = signerLineageSha256
            .Select(static hash => hash.ToArray()).ToArray();
    }

    public string ApplicationId { get; }
    public ulong VersionCode { get; }
    public IReadOnlyList<ReadOnlyMemory<byte>> SignerLineageSha256 =>
        signerLineageSha256.Select(static hash => (ReadOnlyMemory<byte>)hash.ToArray())
            .ToArray();
}
