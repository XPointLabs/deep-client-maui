namespace Deep.Client.Maui.Core.Services;

public sealed record TrustedRootBinding(int Version, string Sha256);

public sealed record TrustedMetadataVersions(
    int Timestamp,
    int Snapshot,
    int Targets,
    int AndroidRelease);

public sealed record TrustedUpdateState(
    string Schema,
    TrustedRootBinding TrustedRoot,
    TrustedMetadataVersions Versions)
{
    public const string CurrentSchema = "deep.update-trust.client-state.v1";
}

public sealed record UpdateMetadataBundle(
    ReadOnlyMemory<byte> TrustedRoot,
    IReadOnlyList<ReadOnlyMemory<byte>> CandidateRoots,
    ReadOnlyMemory<byte> Timestamp,
    ReadOnlyMemory<byte> Snapshot,
    ReadOnlyMemory<byte> Targets,
    ReadOnlyMemory<byte> AndroidRelease);

public sealed record VerifiedAndroidTarget(
    string TargetPath,
    long Length,
    string Sha256,
    string PackageId,
    string PackageSignerSha256,
    string VersionCode,
    string VersionName,
    string SourceCommit,
    DateTimeOffset MetadataExpiresAtUtc);

public sealed record VerifiedUpdateMetadata(
    TrustedUpdateState State,
    VerifiedAndroidTarget AndroidTarget,
    int RootVersion,
    int TimestampVersion,
    int SnapshotVersion,
    int TargetsVersion,
    int AndroidReleaseVersion);

public sealed record AndroidPackageSignerResult(
    bool IsValid,
    string? PackageId,
    string? SignerCertificateSha256,
    string? VersionCode,
    string? VersionName,
    string? Failure);

public interface IAndroidPackageSignerVerifier
{
    Task<AndroidPackageSignerResult> VerifySnapshotAsync(
        string snapshotPath,
        CancellationToken cancellationToken);
}

public sealed record OfflineAndroidPackageRequest(
    UpdateMetadataBundle Metadata,
    string TargetPath,
    string SourceLabel,
    string ApkPath,
    DateTimeOffset UpdateStartUtc);

public sealed record OfflineAndroidPackageVerification(
    bool IsVerified,
    string Status,
    string SourceLabel,
    string TargetPath,
    string PackageId,
    string VersionName,
    string VersionCode,
    string SourceCommit,
    DateTimeOffset MetadataExpiresAtUtc,
    string? Failure,
    string? VerifiedSnapshotSha256);

public interface ITrustedUpdateStateStore
{
    Task<TrustedUpdateState?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(TrustedUpdateState state, CancellationToken cancellationToken);
}

public interface IOfflineAndroidUpdateVerifier
{
    Task<OfflineAndroidPackageVerification> VerifyAsync(
        OfflineAndroidPackageRequest request,
        CancellationToken cancellationToken);
}

public sealed record UpdateTrustConfiguration(
    bool Enabled,
    ReadOnlyMemory<byte> ProvisionedTrustedRoot,
    string ConfigurationSource)
{
    public static UpdateTrustConfiguration Disabled { get; } =
        new(false, ReadOnlyMemory<byte>.Empty, "not configured");
}
