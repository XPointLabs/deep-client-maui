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

    internal const string PhysicalUatMrXKey = "DeepPhysicalUatMrXPublicKeySha256";
    internal const string PhysicalUatNetworkKey = "DeepPhysicalUatNetworkId";
    internal const string PhysicalUatAuthorityGenerationKey =
        "DeepPhysicalUatAuthorityGeneration";
    internal const string PhysicalUatAuthorityHashKey = "DeepPhysicalUatAuthorityHash";
    internal const string PhysicalUatRevocationGenerationKey =
        "DeepPhysicalUatRevocationGeneration";
    internal const string PhysicalUatRevocationHeadHashKey =
        "DeepPhysicalUatRevocationHeadHash";
    internal const string PhysicalUatRevocationSnapshotHashKey =
        "DeepPhysicalUatRevocationSnapshotHash";
    internal const string PhysicalUatTopologyGenerationKey =
        "DeepPhysicalUatTopologyGeneration";
    internal const string PhysicalUatTopologyHashKey = "DeepPhysicalUatTopologyHash";
    internal const string PhysicalUatAndroidApplicationIdKey =
        "DeepPhysicalUatAndroidApplicationId";
    internal const string PhysicalUatAndroidVersionCodeKey =
        "DeepPhysicalUatAndroidVersionCode";
    internal const string PhysicalUatAndroidSignerLineageKey =
        "DeepPhysicalUatAndroidSignerLineageSha256";
#if DEBUG && DEEP_PHYSICAL_E2E
    internal const string PhysicalUatWindowsPackageNameKey =
        "DeepPhysicalUatWindowsPackageName";
    internal const string PhysicalUatWindowsPublisherKey =
        "DeepPhysicalUatWindowsPublisher";
    internal const string PhysicalUatWindowsSignerKey =
        "DeepPhysicalUatWindowsSigningCertificateSha256";
#endif

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

    private static readonly string[] PhysicalUatKeys =
    [
        PhysicalUatMrXKey,
        PhysicalUatNetworkKey,
        PhysicalUatAuthorityGenerationKey,
        PhysicalUatAuthorityHashKey,
        PhysicalUatRevocationGenerationKey,
        PhysicalUatRevocationHeadHashKey,
        PhysicalUatRevocationSnapshotHashKey,
        PhysicalUatTopologyGenerationKey,
        PhysicalUatTopologyHashKey
    ];

    private static readonly string[] PhysicalUatAndroidIdentityKeys =
    [
        PhysicalUatAndroidApplicationIdKey,
        PhysicalUatAndroidVersionCodeKey,
        PhysicalUatAndroidSignerLineageKey
    ];

#if DEBUG && DEEP_PHYSICAL_E2E
    private static readonly string[] PhysicalUatWindowsIdentityKeys =
    [
        PhysicalUatWindowsPackageNameKey,
        PhysicalUatWindowsPublisherKey,
        PhysicalUatWindowsSignerKey
    ];
#endif

    public static bool TryLoad(out ProductionMailboxTrustAnchor? anchor)
    {
        var values = ReadActiveMetadata(Keys,
#if DEBUG && DEEP_PHYSICAL_E2E
            PhysicalUatKeys
#else
            Keys
#endif
        );
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

    internal static bool TryParsePhysicalUat(
        IReadOnlyDictionary<string, string?> values,
        out ProductionMailboxTrustAnchor? anchor) =>
        TryParse(Remap(values, PhysicalUatKeys, Keys), out anchor);

    public static bool TryLoadAndroidIdentity(
        out ProductionAndroidBuildIdentity? identity)
    {
        var values = ReadActiveMetadata(AndroidIdentityKeys,
#if DEBUG && DEEP_PHYSICAL_E2E
            PhysicalUatAndroidIdentityKeys
#else
            AndroidIdentityKeys
#endif
        );
        return TryParseAndroidIdentityCore(
            values,
#if DEBUG && DEEP_PHYSICAL_E2E
            "network.xpoint.deep.e2e",
#else
            ProductionMailboxControlPlaneVerifier.AndroidApplicationIdentity,
#endif
            out identity);
    }

    internal static bool TryParseAndroidIdentity(
        IReadOnlyDictionary<string, string?> values,
        out ProductionAndroidBuildIdentity? identity) =>
        TryParseAndroidIdentityCore(
            values,
            ProductionMailboxControlPlaneVerifier.AndroidApplicationIdentity,
            out identity);

    internal static bool TryParsePhysicalUatAndroidIdentity(
        IReadOnlyDictionary<string, string?> values,
        out ProductionAndroidBuildIdentity? identity) =>
        TryParseAndroidIdentityCore(
            Remap(values, PhysicalUatAndroidIdentityKeys, AndroidIdentityKeys),
            "network.xpoint.deep.e2e",
            out identity);

    private static bool TryParseAndroidIdentityCore(
        IReadOnlyDictionary<string, string?> values,
        string installedApplicationId,
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
            if (!string.Equals(applicationId, installedApplicationId,
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
                installedApplicationId,
                ProductionMailboxControlPlaneVerifier.AndroidApplicationIdentity,
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
        if (!string.Equals(applicationId, expected.InstalledApplicationId,
                StringComparison.Ordinal) ||
            versionCode != expected.VersionCode ||
            signerLineageSha256.Count != expected.SignerLineageSha256.Count ||
            signerLineageSha256.Where((hash, index) =>
                hash.Length != 32 || !System.Security.Cryptography.CryptographicOperations
                    .FixedTimeEquals(hash.Span, expected.SignerLineageSha256[index].Span)).Any())
            throw new InvalidDataException(
                "Production Android package/version/Play signer tuple is not approved.");
    }

#if DEBUG && DEEP_PHYSICAL_E2E
    public static bool TryLoadPhysicalUatWindowsIdentity(
        out PhysicalUatWindowsBuildIdentity? identity) =>
        TryParsePhysicalUatWindowsIdentity(
            ReadActiveMetadata(
                PhysicalUatWindowsIdentityKeys,
                PhysicalUatWindowsIdentityKeys),
            out identity);

    internal static bool TryParsePhysicalUatWindowsIdentity(
        IReadOnlyDictionary<string, string?> values,
        out PhysicalUatWindowsBuildIdentity? identity)
    {
        ArgumentNullException.ThrowIfNull(values);
        identity = null;
        if (PhysicalUatWindowsIdentityKeys.All(key =>
                !values.TryGetValue(key, out var value) || string.IsNullOrEmpty(value)))
            return false;
        if (PhysicalUatWindowsIdentityKeys.Any(key =>
                !values.TryGetValue(key, out var value) || string.IsNullOrEmpty(value)))
            throw Unavailable();

        try
        {
            var packageName = values[PhysicalUatWindowsPackageNameKey]!;
            var publisher = values[PhysicalUatWindowsPublisherKey]!;
            if (!string.Equals(packageName, "network.xpoint.deep.e2e",
                    StringComparison.Ordinal) ||
                packageName.Length is < 3 or > 50 ||
                publisher.Length is < 3 or > 256 ||
                string.IsNullOrWhiteSpace(publisher) ||
                publisher.Any(static character => char.IsControl(character)))
                throw new FormatException();
            identity = new PhysicalUatWindowsBuildIdentity(
                packageName,
                publisher,
                ProductionMailboxControlPlaneVerifier.WindowsApplicationIdentity,
                Hex(values[PhysicalUatWindowsSignerKey]!, 32));
            return true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            identity = null;
            throw Unavailable();
        }
    }

    internal static void VerifyInstalledPhysicalUatWindowsTuple(
        PhysicalUatWindowsBuildIdentity expected,
        string? packageName,
        string? publisher,
        string? executableName,
        ReadOnlySpan<byte> signerCertificateSha256)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (!string.Equals(packageName, expected.InstalledPackageName,
                StringComparison.Ordinal) ||
            !string.Equals(publisher, expected.Publisher, StringComparison.Ordinal) ||
            !string.Equals(executableName, expected.ApplicationIdentity,
                StringComparison.OrdinalIgnoreCase) ||
            signerCertificateSha256.Length != 32 ||
            !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                signerCertificateSha256,
                expected.SigningCertificateSha256.Span))
            throw new InvalidDataException(
                "Physical Windows UAT package, publisher, executable or signer is not approved.");
    }
#endif

    private static IReadOnlyDictionary<string, string?> ReadActiveMetadata(
        IReadOnlyList<string> canonicalKeys,
        IReadOnlyList<string> activeKeys)
    {
        var attributes = typeof(ProductionMailboxBuildTrustFloor).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToArray();
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < canonicalKeys.Count; index++)
        {
            var matches = attributes.Where(attribute => string.Equals(
                attribute.Key, activeKeys[index], StringComparison.Ordinal)).ToArray();
            values[canonicalKeys[index]] = matches.Length == 1
                ? matches[0].Value
                : null;
        }
        return values;
    }

    private static IReadOnlyDictionary<string, string?> Remap(
        IReadOnlyDictionary<string, string?> values,
        IReadOnlyList<string> sourceKeys,
        IReadOnlyList<string> destinationKeys)
    {
        var mapped = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < sourceKeys.Count; index++)
            mapped[destinationKeys[index]] = values.TryGetValue(sourceKeys[index], out var value)
                ? value
                : null;
        return mapped;
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
        string installedApplicationId,
        string applicationIdentity,
        ulong versionCode,
        IEnumerable<byte[]> signerLineageSha256)
    {
        InstalledApplicationId = installedApplicationId;
        ApplicationIdentity = applicationIdentity;
        VersionCode = versionCode;
        this.signerLineageSha256 = signerLineageSha256
            .Select(static hash => hash.ToArray()).ToArray();
    }

    public string InstalledApplicationId { get; }
    public string ApplicationIdentity { get; }
    public ulong VersionCode { get; }
    public IReadOnlyList<ReadOnlyMemory<byte>> SignerLineageSha256 =>
        signerLineageSha256.Select(static hash => (ReadOnlyMemory<byte>)hash.ToArray())
            .ToArray();
}

#if DEBUG && DEEP_PHYSICAL_E2E
internal sealed class PhysicalUatWindowsBuildIdentity
{
    private readonly byte[] signingCertificateSha256;

    public PhysicalUatWindowsBuildIdentity(
        string installedPackageName,
        string publisher,
        string applicationIdentity,
        ReadOnlySpan<byte> signingCertificateSha256)
    {
        InstalledPackageName = installedPackageName;
        Publisher = publisher;
        ApplicationIdentity = applicationIdentity;
        this.signingCertificateSha256 = signingCertificateSha256.ToArray();
    }

    public string InstalledPackageName { get; }
    public string Publisher { get; }
    public string ApplicationIdentity { get; }
    public ReadOnlyMemory<byte> SigningCertificateSha256 =>
        signingCertificateSha256.ToArray();
}
#endif
