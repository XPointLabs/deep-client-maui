using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Sodium;

namespace Deep.Client.Maui.Services;

internal sealed record ProductionAndroidTransparencyArtifact(
    string SplitIdentity,
    ReadOnlyMemory<byte> SemanticSha256);

internal sealed class ProductionAndroidTransparencyManifest
{
    public required string ApplicationId { get; init; }
    public required ulong VersionCode { get; init; }
    public required IReadOnlyList<ReadOnlyMemory<byte>> PlaySignerLineageSha256 { get; init; }
    public required IReadOnlyList<ProductionAndroidTransparencyArtifact> Artifacts { get; init; }
    public required ReadOnlyMemory<byte> MrXEd25519PublicKey { get; init; }
    public required ReadOnlyMemory<byte> Signature { get; init; }
}

internal static class ProductionAndroidTransparencyManifestCodec
{
    internal const int MaximumEncodedBytes = 512 * 1024;
    internal const int MaximumArtifacts = 4_096;
    private static ReadOnlySpan<byte> Magic => "ACT1"u8;
    private static ReadOnlySpan<byte> Domain =>
        "Deep/AndroidCodeTransparency/ACT1/v1"u8;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] Encode(ProductionAndroidTransparencyManifest value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var unsigned = EncodeUnsigned(value);
        if (value.Signature.Length != 64)
            throw new InvalidDataException("Android transparency signature is invalid.");
        var output = new byte[checked(unsigned.Length + 64)];
        unsigned.CopyTo(output, 0);
        value.Signature.Span.CopyTo(output.AsSpan(unsigned.Length));
        if (output.Length > MaximumEncodedBytes)
            throw new InvalidDataException("Android transparency manifest is oversized.");
        return output;
    }

    internal static byte[] GetSigningBytes(ProductionAndroidTransparencyManifest value)
    {
        var unsigned = EncodeUnsigned(value);
        var output = new byte[checked(Domain.Length + unsigned.Length)];
        Domain.CopyTo(output);
        unsigned.CopyTo(output, Domain.Length);
        return output;
    }

    internal static ProductionAndroidTransparencyManifest Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length is < 150 or > MaximumEncodedBytes ||
            !encoded[..4].SequenceEqual(Magic) || encoded[4] != 1 ||
            encoded.Slice(5, 3).IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Android transparency framing is invalid.");
        var offset = 8;
        var app = ReadString(encoded, ref offset, 256);
        var version = ReadUInt64(encoded, ref offset);
        var lineageCount = ReadUInt16(encoded, ref offset);
        if (lineageCount is < 1 or > 32)
            throw new InvalidDataException("Android transparency lineage is invalid.");
        var lineage = new ReadOnlyMemory<byte>[lineageCount];
        for (var index = 0; index < lineage.Length; index++)
            lineage[index] = ReadBytes(encoded, ref offset, 32);
        var artifactCount = ReadUInt16(encoded, ref offset);
        if (artifactCount is < 1 or > MaximumArtifacts)
            throw new InvalidDataException("Android transparency artifact count is invalid.");
        var artifacts = new ProductionAndroidTransparencyArtifact[artifactCount];
        for (var index = 0; index < artifacts.Length; index++)
            artifacts[index] = new ProductionAndroidTransparencyArtifact(
                ReadString(encoded, ref offset, 256), ReadBytes(encoded, ref offset, 32));
        var publicKey = ReadBytes(encoded, ref offset, 32);
        var signature = ReadBytes(encoded, ref offset, 64);
        if (offset != encoded.Length)
            throw new InvalidDataException("Android transparency manifest is non-canonical.");
        var value = new ProductionAndroidTransparencyManifest
        {
            ApplicationId = app,
            VersionCode = version,
            PlaySignerLineageSha256 = lineage,
            Artifacts = artifacts,
            MrXEd25519PublicKey = publicKey,
            Signature = signature
        };
        if (!Encode(value).AsSpan().SequenceEqual(encoded))
            throw new InvalidDataException("Android transparency manifest is non-canonical.");
        return value;
    }

    private static byte[] EncodeUnsigned(ProductionAndroidTransparencyManifest value)
    {
        Validate(value);
        using var stream = new MemoryStream();
        stream.Write(Magic); stream.WriteByte(1); stream.Write(new byte[3]);
        WriteString(stream, value.ApplicationId);
        WriteUInt64(stream, value.VersionCode);
        WriteUInt16(stream, checked((ushort)value.PlaySignerLineageSha256.Count));
        foreach (var hash in value.PlaySignerLineageSha256) stream.Write(hash.Span);
        WriteUInt16(stream, checked((ushort)value.Artifacts.Count));
        foreach (var artifact in value.Artifacts)
        {
            WriteString(stream, artifact.SplitIdentity);
            stream.Write(artifact.SemanticSha256.Span);
        }
        stream.Write(value.MrXEd25519PublicKey.Span);
        return stream.ToArray();
    }

    private static void Validate(ProductionAndroidTransparencyManifest value)
    {
        if (value.ApplicationId is not ("network.xpoint.deep" or
                "network.xpoint.deep.e2e") ||
            value.VersionCode == 0 || value.PlaySignerLineageSha256.Count is < 1 or > 32 ||
            value.Artifacts.Count is < 1 or > MaximumArtifacts ||
            value.MrXEd25519PublicKey.Length != 32 ||
            value.MrXEd25519PublicKey.Span.IndexOfAnyExcept((byte)0) < 0)
            throw new InvalidDataException("Android transparency manifest is incomplete.");
        ValidateHashes(value.PlaySignerLineageSha256, "lineage");
        if (value.Artifacts.Count(static artifact => artifact.SplitIdentity == "base") != 1 ||
            value.Artifacts.Select(static artifact => artifact.SplitIdentity)
                .Distinct(StringComparer.Ordinal).Count() != value.Artifacts.Count ||
            !value.Artifacts.Select(static artifact => artifact.SplitIdentity)
                .SequenceEqual(value.Artifacts.Select(static artifact => artifact.SplitIdentity)
                    .Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("Android transparency split identities are invalid.");
        foreach (var artifact in value.Artifacts)
        {
            ValidateIdentity(artifact.SplitIdentity);
            if (artifact.SemanticSha256.Length != 32 ||
                artifact.SemanticSha256.Span.IndexOfAnyExcept((byte)0) < 0)
                throw new InvalidDataException("Android transparency artifact digest is invalid.");
        }
    }

    private static void ValidateHashes(
        IReadOnlyList<ReadOnlyMemory<byte>> hashes, string label)
    {
        if (hashes.Any(static hash => hash.Length != 32 ||
                hash.Span.IndexOfAnyExcept((byte)0) < 0) ||
            hashes.Select(static hash => Convert.ToHexString(hash.Span))
                .Distinct(StringComparer.Ordinal).Count() != hashes.Count)
            throw new InvalidDataException($"Android transparency {label} is invalid.");
    }

    private static void ValidateIdentity(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity) || identity.Length > 256 ||
            identity is "." or ".." || identity.Contains('/') || identity.Contains('\\') ||
            StrictUtf8.GetByteCount(identity) > 256)
            throw new InvalidDataException("Android transparency split identity is invalid.");
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        try { WriteUInt16(stream, checked((ushort)bytes.Length)); stream.Write(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void WriteUInt16(Stream stream, ushort value)
    { Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, value); stream.Write(bytes); }
    private static void WriteUInt64(Stream stream, ulong value)
    { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(bytes, value); stream.Write(bytes); }
    private static ushort ReadUInt16(ReadOnlySpan<byte> value, ref int offset)
    { Require(value, offset, 2); var result = BinaryPrimitives.ReadUInt16BigEndian(value[offset..]); offset += 2; return result; }
    private static ulong ReadUInt64(ReadOnlySpan<byte> value, ref int offset)
    { Require(value, offset, 8); var result = BinaryPrimitives.ReadUInt64BigEndian(value[offset..]); offset += 8; return result; }
    private static byte[] ReadBytes(ReadOnlySpan<byte> value, ref int offset, int count)
    { Require(value, offset, count); var result = value.Slice(offset, count).ToArray(); offset += count; return result; }
    private static string ReadString(ReadOnlySpan<byte> value, ref int offset, int maximum)
    { var count = ReadUInt16(value, ref offset); if (count is 0 || count > maximum) throw new InvalidDataException("Android transparency string is invalid."); var bytes = ReadBytes(value, ref offset, count); try { return StrictUtf8.GetString(bytes); } catch (DecoderFallbackException e) { throw new InvalidDataException("Android transparency string is invalid.", e); } finally { CryptographicOperations.ZeroMemory(bytes); } }
    private static void Require(ReadOnlySpan<byte> value, int offset, int count)
    { if (offset < 0 || count < 0 || offset > value.Length - count) throw new InvalidDataException("Android transparency manifest is truncated."); }
}

internal static class ProductionAndroidCodeTransparencyVerifier
{
    internal const string AssetPath = "deep/production/android-code-transparency.act1";

    internal static Task<byte[]> VerifyRuntimeAsync(
        ReadOnlyMemory<byte> encodedManifest,
        ReadOnlyMemory<byte> expectedMrXPublicKeySha256,
        string applicationId,
        ulong versionCode,
        IReadOnlyList<ReadOnlyMemory<byte>> signerLineage,
        IReadOnlyDictionary<string, string> installedArtifacts,
        CancellationToken cancellationToken = default) => VerifyCoreAsync(
            encodedManifest, null, expectedMrXPublicKeySha256, applicationId, versionCode,
            signerLineage, installedArtifacts, requireCompleteArtifactSet: false,
            cancellationToken);

    internal static Task<byte[]> VerifyBuildAsync(
        ReadOnlyMemory<byte> encodedManifest,
        ReadOnlyMemory<byte> expectedManifestSha256,
        ReadOnlyMemory<byte> expectedMrXPublicKeySha256,
        string applicationId,
        ulong versionCode,
        IReadOnlyList<ReadOnlyMemory<byte>> signerLineage,
        IReadOnlyDictionary<string, string> generatedArtifacts,
        CancellationToken cancellationToken = default) => VerifyCoreAsync(
            encodedManifest, expectedManifestSha256, expectedMrXPublicKeySha256,
            applicationId, versionCode, signerLineage, generatedArtifacts,
            requireCompleteArtifactSet: true, cancellationToken);

    private static async Task<byte[]> VerifyCoreAsync(
        ReadOnlyMemory<byte> encodedManifest,
        ReadOnlyMemory<byte>? expectedManifestSha256,
        ReadOnlyMemory<byte> expectedMrXPublicKeySha256,
        string applicationId,
        ulong versionCode,
        IReadOnlyList<ReadOnlyMemory<byte>> signerLineage,
        IReadOnlyDictionary<string, string> installedArtifacts,
        bool requireCompleteArtifactSet,
        CancellationToken cancellationToken)
    {
        if (encodedManifest.Length > ProductionAndroidTransparencyManifestCodec.MaximumEncodedBytes)
            throw new InvalidDataException("Android transparency manifest is oversized.");
        var frozen = encodedManifest.ToArray();
        var frozenLineage = FreezeLineage(signerLineage);
        var frozenArtifacts = FreezeArtifacts(installedArtifacts);
        var manifest = ProductionAndroidTransparencyManifestCodec.Decode(frozen);
        var manifestHash = SHA256.HashData(frozen);
        if ((expectedManifestSha256 is { } expectedManifest &&
                (expectedManifest.Length != 32 || !CryptographicOperations.FixedTimeEquals(
                    manifestHash, expectedManifest.Span))) ||
            expectedMrXPublicKeySha256.Length != 32 ||
            !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(manifest.MrXEd25519PublicKey.Span),
                expectedMrXPublicKeySha256.Span))
            throw new InvalidDataException("Android transparency trust binding is invalid.");
        var signingBytes = ProductionAndroidTransparencyManifestCodec.GetSigningBytes(manifest);
        try
        {
            if (!PublicKeyAuth.VerifyDetached(
                    manifest.Signature.ToArray(), signingBytes,
                    manifest.MrXEd25519PublicKey.ToArray()))
                throw new InvalidDataException("Android transparency signature is invalid.");
        }
        finally { CryptographicOperations.ZeroMemory(signingBytes); }
        if (!string.Equals(applicationId, manifest.ApplicationId, StringComparison.Ordinal) ||
            versionCode != manifest.VersionCode ||
            frozenLineage.Length != manifest.PlaySignerLineageSha256.Count ||
            frozenLineage.Where((hash, index) => hash.Length != 32 ||
                !CryptographicOperations.FixedTimeEquals(
                    hash.Span, manifest.PlaySignerLineageSha256[index].Span)).Any() ||
            frozenArtifacts.Count is < 1 or > ProductionAndroidTransparencyManifestCodec.MaximumArtifacts ||
            !frozenArtifacts.ContainsKey("base") ||
            (requireCompleteArtifactSet && frozenArtifacts.Count != manifest.Artifacts.Count))
            throw new InvalidDataException("Android transparency package tuple is not approved.");
        var expected = manifest.Artifacts.ToDictionary(
            static artifact => artifact.SplitIdentity,
            static artifact => artifact.SemanticSha256,
            StringComparer.Ordinal);
        foreach (var artifact in frozenArtifacts.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (!expected.TryGetValue(artifact.Key, out var digest))
                throw new InvalidDataException("Android transparency split is not approved.");
            var actual = await ProductionAndroidSemanticApkDigest.ComputeAsync(
                artifact.Value, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(actual, digest.Span))
                throw new InvalidDataException("Android transparency artifact content is not approved.");
        }
        return manifestHash;
    }

    private static ReadOnlyMemory<byte>[] FreezeLineage(
        IReadOnlyList<ReadOnlyMemory<byte>> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var frozen = new List<ReadOnlyMemory<byte>>(32);
        foreach (var hash in source)
        {
            if (frozen.Count == 32 || hash.Length != 32)
                throw new InvalidDataException(
                    "Android transparency signer lineage is outside strict bounds.");
            frozen.Add(hash.ToArray());
        }
        if (frozen.Count == 0)
            throw new InvalidDataException(
                "Android transparency signer lineage is outside strict bounds.");
        return frozen.ToArray();
    }

    private static IReadOnlyDictionary<string, string> FreezeArtifacts(
        IReadOnlyDictionary<string, string> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var frozen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var artifact in source)
        {
            if (frozen.Count == ProductionAndroidTransparencyManifestCodec.MaximumArtifacts ||
                string.IsNullOrEmpty(artifact.Key) || artifact.Key.Length > 256 ||
                string.IsNullOrWhiteSpace(artifact.Value) || artifact.Value.Length > 32_768)
                throw new InvalidDataException(
                    "Android transparency artifact inventory is outside strict bounds.");
            var fullPath = Path.GetFullPath(artifact.Value);
            if (!frozen.TryAdd(artifact.Key, fullPath))
                throw new InvalidDataException(
                    "Android transparency artifact inventory is duplicated.");
        }
        if (frozen.Count == 0)
            throw new InvalidDataException(
                "Android transparency artifact inventory is empty.");
        return frozen;
    }
}

internal static class ProductionAndroidSemanticApkDigest
{
    private const int MaximumEntries = 65_534;
    private const long MaximumApkBytes = 512L * 1024 * 1024;
    private const long MaximumEntryBytes = 512L * 1024 * 1024;
    private const long MaximumTotalBytes = 2L * 1024 * 1024 * 1024;
    private static ReadOnlySpan<byte> Domain => "Deep/AndroidSemanticApk/v1"u8;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static async Task<byte[]> ComputeAsync(
        string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        EnsureNoReparse(fullPath);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length is <= 0 or > MaximumApkBytes)
            throw new InvalidDataException("Android artifact is unavailable or oversized.");
        var originalLength = info.Length;
        var originalWrite = info.LastWriteTimeUtc;
        await using var stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
        PreflightZipDirectory(stream, originalLength);
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count is < 1 or > MaximumEntries)
            throw new InvalidDataException("Android artifact ZIP entry count is invalid.");
        var entries = new List<ZipArchiveEntry>();
        foreach (var entry in archive.Entries.Where(static entry =>
                     !entry.FullName.EndsWith('/')))
        {
            ValidateName(entry.FullName);
            if (IsDirectSigningMetadata(entry.FullName) ||
                string.Equals(entry.FullName, "assets/" +
                    ProductionAndroidCodeTransparencyVerifier.AssetPath,
                    StringComparison.Ordinal))
                continue;
            if (LooksLikeAmbiguousSigningMetadata(entry.FullName))
                throw new InvalidDataException(
                    "Android artifact contains ambiguous APK signing metadata.");
            entries.Add(entry);
        }
        entries.Sort(static (left, right) =>
            string.CompareOrdinal(left.FullName, right.FullName));
        if (entries.Count == 0 || entries.Select(static entry => entry.FullName)
            .Distinct(StringComparer.Ordinal).Count() != entries.Count)
            throw new InvalidDataException("Android artifact ZIP inventory is invalid.");
        long total = 0;
        using var output = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        output.AppendData(Domain); AppendUInt32(output, checked((uint)entries.Count));
        foreach (var entry in entries)
        {
            if (entry.Length < 0 || entry.Length > MaximumEntryBytes ||
                (entry.Length > 4L * 1024 * 1024 &&
                 (entry.CompressedLength <= 0 || entry.Length / entry.CompressedLength > 1_000)))
                throw new InvalidDataException("Android artifact ZIP entry is unsafe.");
            total = checked(total + entry.Length);
            if (total > MaximumTotalBytes)
                throw new InvalidDataException("Android artifact expanded size is oversized.");
            AppendString(output, entry.FullName); AppendUInt64(output, checked((ulong)entry.Length));
            await using var entryStream = entry.Open();
            using var entryHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024]; long readTotal = 0;
            int read;
            while ((read = await entryStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            { readTotal = checked(readTotal + read); if (readTotal > entry.Length) throw new InvalidDataException("Android artifact ZIP entry expanded past its declaration."); entryHash.AppendData(buffer.AsSpan(0, read)); }
            if (readTotal != entry.Length) throw new InvalidDataException("Android artifact ZIP entry is truncated.");
            var digest = entryHash.GetHashAndReset(); output.AppendData(digest); CryptographicOperations.ZeroMemory(digest);
        }
        info.Refresh(); EnsureNoReparse(fullPath);
        if (stream.Length != originalLength || !info.Exists || info.Length != originalLength ||
            info.LastWriteTimeUtc != originalWrite)
            throw new InvalidDataException("Android artifact changed during transparency verification.");
        return output.GetHashAndReset();
    }

    private static void PreflightZipDirectory(Stream stream, long fileLength)
    {
        const int minimumEocdBytes = 22;
        const int maximumCommentBytes = ushort.MaxValue;
        var tailLength = checked((int)Math.Min(fileLength,
            minimumEocdBytes + maximumCommentBytes));
        if (tailLength < minimumEocdBytes)
            throw new InvalidDataException("Android artifact ZIP is truncated.");
        var tail = new byte[tailLength];
        stream.Position = fileLength - tailLength;
        stream.ReadExactly(tail);
        var eocd = -1;
        for (var index = tail.Length - minimumEocdBytes; index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index, 4)) == 0x06054b50)
            {
                eocd = index;
                break;
            }
        }
        if (eocd < 0)
            throw new InvalidDataException("Android artifact ZIP directory is unavailable.");
        var record = tail.AsSpan(eocd);
        var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(record[20..]);
        if (record.Length != minimumEocdBytes + commentLength ||
            BinaryPrimitives.ReadUInt16LittleEndian(record[4..]) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(record[6..]) != 0)
            throw new InvalidDataException("Android artifact ZIP directory is non-canonical.");
        var entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(record[8..]);
        var entries = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
        var directoryBytes = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
        var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
        var absoluteEocd = checked(fileLength - tailLength + eocd);
        if (entries is < 1 or > MaximumEntries || entriesOnDisk != entries ||
            directoryOffset > absoluteEocd || directoryBytes > absoluteEocd - directoryOffset ||
            directoryOffset + directoryBytes != absoluteEocd)
            throw new InvalidDataException("Android artifact ZIP directory bounds are invalid.");
    }

    private static bool IsDirectSigningMetadata(string name)
    {
        if (!name.StartsWith("META-INF/", StringComparison.Ordinal))
            return false;
        var leaf = name["META-INF/".Length..];
        if (leaf.Contains('/')) return false;
        if (string.Equals(leaf, "MANIFEST.MF", StringComparison.Ordinal)) return true;
        if (leaf.StartsWith("SIG-", StringComparison.Ordinal))
            return leaf.Length is >= 5 and <= 72 &&
                leaf[4..].All(static value => value is >= 'A' and <= 'Z' or
                    >= '0' and <= '9' or '_' or '-' or '.');
        var separator = leaf.LastIndexOf('.');
        if (separator is < 1 or > 8) return false;
        if (!leaf[..separator].All(static value => value is >= 'A' and <= 'Z' or
                >= '0' and <= '9' or '_' or '-'))
            return false;
        return leaf[(separator + 1)..] is "SF" or "RSA" or "DSA" or "EC";
    }

    private static bool LooksLikeAmbiguousSigningMetadata(string name)
    {
        if (!name.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase))
            return false;
        var leaf = name[(name.IndexOf('/') + 1)..].Split('/').Last();
        return string.Equals(leaf, "MANIFEST.MF", StringComparison.OrdinalIgnoreCase) ||
            leaf.EndsWith(".SF", StringComparison.OrdinalIgnoreCase) ||
            leaf.EndsWith(".RSA", StringComparison.OrdinalIgnoreCase) ||
            leaf.EndsWith(".DSA", StringComparison.OrdinalIgnoreCase) ||
            leaf.EndsWith(".EC", StringComparison.OrdinalIgnoreCase) ||
            leaf.StartsWith("SIG-", StringComparison.OrdinalIgnoreCase);
    }
    private static void ValidateName(string name)
    { if (string.IsNullOrEmpty(name) || name.Length > 1_024 || name.StartsWith('/') || name.Contains('\\') || name.Split('/').Any(static part => part.Length == 0 || part is "." or "..") || StrictUtf8.GetByteCount(name) > 1_024) throw new InvalidDataException("Android artifact ZIP name is invalid."); }
    private static void EnsureNoReparse(string path)
    { FileSystemInfo? current = new FileInfo(path); while (current is not null) { current.Refresh(); if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Android artifact path contains a reparse point."); current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent; } }
    private static void AppendString(IncrementalHash hash, string value)
    { var bytes = StrictUtf8.GetBytes(value); try { AppendUInt32(hash, checked((uint)bytes.Length)); hash.AppendData(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); } }
    private static void AppendUInt32(IncrementalHash hash, uint value)
    { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); hash.AppendData(bytes); }
    private static void AppendUInt64(IncrementalHash hash, ulong value)
    { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(bytes, value); hash.AppendData(bytes); }
}
