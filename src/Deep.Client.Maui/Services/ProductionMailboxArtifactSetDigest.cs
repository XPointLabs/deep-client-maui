using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Deep.Client.Maui.Services;

internal sealed record ProductionMailboxArtifactFile(string LogicalName, string Path);

internal enum ProductionMailboxArtifactDigestFaultPoint
{
    AfterOpenBeforeHash
}

/// <summary>
/// Race-resistant digest of the complete installed executable artifact set. Logical names, file
/// lengths, individual hashes, and the ordered signer lineage are committed; machine-specific
/// absolute paths are deliberately excluded.
/// </summary>
internal static class ProductionMailboxArtifactSetDigest
{
    private const int MaximumArtifactCount = 16_384;
    private const int MaximumLogicalNameBytes = 2_048;
    private static ReadOnlySpan<byte> Domain =>
        "deep.production-mailbox.artifact-set.v1"u8;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static async Task<byte[]> ComputeAsync(
        string platformIdentity,
        IReadOnlyList<ReadOnlyMemory<byte>> signerLineageSha256,
        IReadOnlyList<ProductionMailboxArtifactFile> artifacts,
        CancellationToken cancellationToken = default,
        Action<ProductionMailboxArtifactDigestFaultPoint, string>? faultInjector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platformIdentity);
        ArgumentNullException.ThrowIfNull(signerLineageSha256);
        ArgumentNullException.ThrowIfNull(artifacts);
        if (signerLineageSha256.Count is < 1 or > 32 ||
            signerLineageSha256.Any(static hash =>
                hash.Length != 32 || hash.Span.IndexOfAnyExcept((byte)0) < 0) ||
            signerLineageSha256.Select(static hash => Convert.ToHexString(hash.Span))
                .Distinct(StringComparer.Ordinal).Count() != signerLineageSha256.Count)
            throw new InvalidDataException("Production signer lineage is invalid.");
        if (artifacts.Count is < 1 or > MaximumArtifactCount)
            throw new InvalidDataException("Production artifact set count is invalid.");

        var ordered = artifacts
            .Select(static artifact => ValidateArtifact(artifact))
            .OrderBy(static artifact => artifact.LogicalName, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Select(static artifact => artifact.LogicalName)
            .Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new InvalidDataException("Production artifact logical names are not unique.");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Domain);
        AppendString(hash, platformIdentity);
        AppendUInt32(hash, checked((uint)signerLineageSha256.Count));
        foreach (var signer in signerLineageSha256) hash.AppendData(signer.Span);
        AppendUInt32(hash, checked((uint)ordered.Length));
        foreach (var artifact in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendString(hash, artifact.LogicalName);
            EnsureNoReparsePoints(artifact.Path);
            var info = new FileInfo(artifact.Path);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    $"Production artifact '{artifact.LogicalName}' is unavailable or unsafe.");
            await using var stream = new FileStream(
                info.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var originalLength = stream.Length;
            if (originalLength <= 0)
                throw new InvalidDataException(
                    $"Production artifact '{artifact.LogicalName}' is empty.");
            var originalLastWriteTimeUtc = info.LastWriteTimeUtc;
            faultInjector?.Invoke(
                ProductionMailboxArtifactDigestFaultPoint.AfterOpenBeforeHash,
                artifact.Path);
            AppendUInt64(hash, checked((ulong)originalLength));
            var fileHash = await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                stream.Position = 0;
                info.Refresh();
                if (stream.Length != originalLength || !info.Exists ||
                    info.Length != originalLength ||
                    info.LastWriteTimeUtc != originalLastWriteTimeUtc ||
                    (info.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException(
                        $"Production artifact '{artifact.LogicalName}' changed during attestation.");
                hash.AppendData(fileHash);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(fileHash);
            }
        }
        return hash.GetHashAndReset();
    }

    private static ProductionMailboxArtifactFile ValidateArtifact(
        ProductionMailboxArtifactFile? artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (string.IsNullOrWhiteSpace(artifact.LogicalName) ||
            artifact.LogicalName.Length > MaximumLogicalNameBytes ||
            artifact.LogicalName.StartsWith("/", StringComparison.Ordinal) ||
            artifact.LogicalName.Contains('\\') ||
            artifact.LogicalName.Split('/').Any(static segment =>
                segment.Length == 0 || segment is "." or "..") ||
            StrictUtf8.GetByteCount(artifact.LogicalName) > MaximumLogicalNameBytes)
            throw new InvalidDataException("Production artifact logical name is invalid.");
        var fullPath = System.IO.Path.GetFullPath(artifact.Path);
        return artifact with { Path = fullPath };
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var current = new FileInfo(Path.GetFullPath(path)) as FileSystemInfo;
        while (current is not null)
        {
            current.Refresh();
            if (current.Exists &&
                (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    "Production artifact path contains a reparse point.");
            var parent = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null
            };
            current = parent;
        }
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        byte[] encoded;
        try
        {
            encoded = StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidDataException("Production artifact identity is invalid UTF-8.",
                exception);
        }
        try
        {
            if (encoded.Length is <= 0 or > MaximumLogicalNameBytes)
                throw new InvalidDataException(
                    "Production artifact identity length is invalid.");
            AppendUInt32(hash, checked((uint)encoded.Length));
            hash.AppendData(encoded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private static void AppendUInt32(IncrementalHash hash, uint value)
    {
        Span<byte> encoded = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(encoded, value);
        hash.AppendData(encoded);
    }

    private static void AppendUInt64(IncrementalHash hash, ulong value)
    {
        Span<byte> encoded = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(encoded, value);
        hash.AppendData(encoded);
    }
}
