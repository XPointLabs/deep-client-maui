using System.Security.Cryptography;

namespace Deep.Client.Maui.Core.Services;

public sealed class VerifiedAndroidPackageHandoffService
    : IVerifiedAndroidPackageHandoffService
{
    public const long AbsoluteMaximumPackageBytes = 1_073_741_824;
    public static readonly TimeSpan AbsoluteMaximumTimeToLive =
        TimeSpan.FromMinutes(15);

    private readonly string privateSnapshotRoot;
    private readonly IAndroidPackageSignerVerifier packageSignerVerifier;
    private readonly IAndroidPackageInstallerHandoff installerHandoff;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan timeToLive;
    private readonly long maximumPackageBytes;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ActiveSnapshot? activeSnapshot;

    public VerifiedAndroidPackageHandoffService(
        string privateSnapshotRoot,
        IAndroidPackageSignerVerifier packageSignerVerifier,
        IAndroidPackageInstallerHandoff installerHandoff,
        TimeProvider? timeProvider = null,
        TimeSpan? timeToLive = null,
        long maximumPackageBytes = AbsoluteMaximumPackageBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateSnapshotRoot);
        this.privateSnapshotRoot = Path.GetFullPath(privateSnapshotRoot);
        this.packageSignerVerifier = packageSignerVerifier
            ?? throw new ArgumentNullException(nameof(packageSignerVerifier));
        this.installerHandoff = installerHandoff
            ?? throw new ArgumentNullException(nameof(installerHandoff));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.timeToLive = timeToLive ?? TimeSpan.FromMinutes(10);
        if (this.timeToLive <= TimeSpan.Zero ||
            this.timeToLive > AbsoluteMaximumTimeToLive)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeToLive),
                $"Handoff TTL must be between zero and {AbsoluteMaximumTimeToLive}.");
        }
        if (maximumPackageBytes <= 0 ||
            maximumPackageBytes > AbsoluteMaximumPackageBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPackageBytes));
        }
        this.maximumPackageBytes = maximumPackageBytes;

        Directory.CreateDirectory(this.privateSnapshotRoot);
        CleanupRootOnStartup();
    }

    public async Task<PreservedAndroidPackageHandle> PreserveVerifiedSnapshotAsync(
        string verifiedSnapshotPath,
        VerifiedAndroidTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedSnapshotPath);
        ArgumentNullException.ThrowIfNull(target);
        ValidateTarget(target);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CleanupActiveSnapshot();
            var sourcePath = Path.GetFullPath(verifiedSnapshotPath);
            var observed = await InspectFileAsync(
                sourcePath,
                target.Length,
                cancellationToken).ConfigureAwait(false);
            if (!FixedTimeSha256Equals(observed, target.Sha256))
            {
                throw new InvalidDataException(
                    "Verified snapshot no longer matches signed package metadata.");
            }

            var now = timeProvider.GetUtcNow();
            var expiresAt = now + timeToLive;
            if (target.MetadataExpiresAtUtc < expiresAt)
            {
                expiresAt = target.MetadataExpiresAtUtc;
            }
            if (expiresAt <= now)
            {
                throw new InvalidDataException(
                    "Verified package metadata expired before installer handoff.");
            }

            var handleBytes = RandomNumberGenerator.GetBytes(32);
            var handle = Base64UrlEncode(handleBytes);
            var handleDigest = SHA256.HashData(handleBytes);
            var directoryPath = Path.Combine(
                privateSnapshotRoot,
                $"pkg-{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}");
            var snapshotPath = Path.Combine(
                directoryPath,
                $"{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}.apk");
            Directory.CreateDirectory(directoryPath);
            try
            {
                File.Move(sourcePath, snapshotPath);
                var expiryCancellation = new CancellationTokenSource();
                var preserved = new ActiveSnapshot(
                    handleDigest,
                    directoryPath,
                    snapshotPath,
                    target,
                    expiresAt,
                    expiryCancellation);
                activeSnapshot = preserved;
                _ = ExpireSnapshotAsync(
                    preserved,
                    expiresAt - now,
                    expiryCancellation.Token);
                return new PreservedAndroidPackageHandle(handle, expiresAt);
            }
            catch
            {
                DeleteDirectoryBestEffort(directoryPath);
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(handleBytes);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<VerifiedAndroidPackageHandoffResult> HandOffAsync(
        string handle,
        bool userConfirmed,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ActiveSnapshot? claimed = null;
        try
        {
            claimed = ClaimActiveSnapshot(handle);
            if (claimed is null)
            {
                return Blocked("The installer handoff handle is invalid or has already been used.");
            }
            if (!userConfirmed)
            {
                return Blocked("Explicit user confirmation is required before installer handoff.");
            }
            if (timeProvider.GetUtcNow() >= claimed.ExpiresAtUtc)
            {
                return Blocked("The installer handoff handle expired.");
            }

            var target = claimed.Target;
            var beforeIdentity = await InspectFileAsync(
                claimed.SnapshotPath,
                target.Length,
                cancellationToken).ConfigureAwait(false);
            if (!FixedTimeSha256Equals(beforeIdentity, target.Sha256))
            {
                return Blocked("The verified package snapshot changed before installer handoff.");
            }

            var identity = await packageSignerVerifier.VerifySnapshotAsync(
                claimed.SnapshotPath,
                cancellationToken).ConfigureAwait(false);
            if (!MatchesTarget(identity, target))
            {
                return Blocked("Android package identity revalidation failed.");
            }

            var afterIdentity = await InspectFileAsync(
                claimed.SnapshotPath,
                target.Length,
                cancellationToken).ConfigureAwait(false);
            if (!FixedTimeSha256Equals(afterIdentity, target.Sha256))
            {
                return Blocked("The verified package snapshot changed during identity revalidation.");
            }

            AndroidPackageInstallerHandoffResult platformResult;
            try
            {
                platformResult = await installerHandoff.RequestInstallAsync(
                    claimed.SnapshotPath,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return Blocked("The platform installer rejected the verified package handoff.");
            }

            return platformResult is { IsAccepted: true }
                ? new VerifiedAndroidPackageHandoffResult(
                    true,
                    "Verified package was handed to the platform installer after explicit confirmation.",
                    null)
                : Blocked("The platform installer rejected the verified package handoff.");
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException or
            CryptographicException or FormatException or ArgumentException)
        {
            return Blocked("Verified package handoff failed closed.");
        }
        finally
        {
            if (claimed is not null)
            {
                DeleteDirectoryBestEffort(claimed.DirectoryPath);
            }
            gate.Release();
        }
    }

    private ActiveSnapshot? ClaimActiveSnapshot(string handle)
    {
        var candidate = activeSnapshot;
        if (candidate is null || string.IsNullOrWhiteSpace(handle))
        {
            return null;
        }

        byte[] handleBytes;
        try
        {
            handleBytes = Base64UrlDecode(handle);
        }
        catch (FormatException)
        {
            return null;
        }
        try
        {
            var digest = SHA256.HashData(handleBytes);
            if (!CryptographicOperations.FixedTimeEquals(
                digest,
                candidate.HandleDigest))
            {
                return null;
            }
            activeSnapshot = null;
            candidate.ExpiryCancellation.Cancel();
            return candidate;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(handleBytes);
        }
    }

    private void CleanupRootOnStartup()
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(privateSnapshotRoot))
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }
        }
    }

    private void CleanupActiveSnapshot()
    {
        if (activeSnapshot is not null)
        {
            activeSnapshot.ExpiryCancellation.Cancel();
            DeleteDirectoryBestEffort(activeSnapshot.DirectoryPath);
            activeSnapshot = null;
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(privateSnapshotRoot))
        {
            if (Directory.Exists(path))
            {
                DeleteDirectoryBestEffort(path);
            }
            else
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // Preservation fails below if the bounded private root is not empty.
                }
            }
        }
        if (Directory.EnumerateFileSystemEntries(privateSnapshotRoot).Any())
        {
            throw new IOException("Unable to clean the private installer handoff store.");
        }
    }

    private async Task ExpireSnapshotAsync(
        ActiveSnapshot candidate,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, timeProvider, cancellationToken)
                .ConfigureAwait(false);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(activeSnapshot, candidate) &&
                    timeProvider.GetUtcNow() >= candidate.ExpiresAtUtc)
                {
                    activeSnapshot = null;
                    DeleteDirectoryBestEffort(candidate.DirectoryPath);
                }
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // A handoff, replacement or shutdown-style cleanup consumed the snapshot.
        }
        catch
        {
            // A later operation or startup cleanup retries without logging package data.
        }
        finally
        {
            candidate.ExpiryCancellation.Dispose();
        }
    }

    private static void DeleteDirectoryBestEffort(string directoryPath)
    {
        try
        {
            Directory.Delete(directoryPath, recursive: true);
        }
        catch
        {
            // A later startup cleanup retries removal. No package path is logged.
        }
    }

    private void ValidateTarget(VerifiedAndroidTarget target)
    {
        if (target.Length < 0 || target.Length > maximumPackageBytes ||
            !IsLowerHexSha256(target.Sha256) ||
            !IsLowerHexSha256(target.PackageSignerSha256) ||
            string.IsNullOrWhiteSpace(target.PackageId) ||
            string.IsNullOrWhiteSpace(target.VersionCode) ||
            string.IsNullOrWhiteSpace(target.VersionName))
        {
            throw new InvalidDataException("Signed Android package handoff metadata is invalid.");
        }
    }

    private async Task<string> InspectFileAsync(
        string filePath,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != expectedLength ||
            stream.Length > maximumPackageBytes)
        {
            throw new InvalidDataException(
                "Private package snapshot length changed before installer handoff.");
        }
        var digest = await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static bool MatchesTarget(
        AndroidPackageSignerResult? observed,
        VerifiedAndroidTarget target) =>
        observed is { IsValid: true } &&
        string.Equals(observed.PackageId, target.PackageId, StringComparison.Ordinal) &&
        FixedTimeSha256Equals(
            observed.SignerCertificateSha256,
            target.PackageSignerSha256) &&
        string.Equals(observed.VersionCode, target.VersionCode, StringComparison.Ordinal) &&
        string.Equals(observed.VersionName, target.VersionName, StringComparison.Ordinal);

    private static bool FixedTimeSha256Equals(string? observed, string expected)
    {
        try
        {
            return IsLowerHexSha256(observed) &&
                IsLowerHexSha256(expected) &&
                CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(observed!),
                    Convert.FromHexString(expected));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsLowerHexSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = (normalized.Length % 4) switch
        {
            0 => normalized,
            2 => normalized + "==",
            3 => normalized + "=",
            _ => throw new FormatException("Invalid base64url value.")
        };
        return Convert.FromBase64String(normalized);
    }

    private static VerifiedAndroidPackageHandoffResult Blocked(string failure) =>
        new(
            false,
            "Installer handoff is blocked without a bypass.",
            failure);

    private sealed record ActiveSnapshot(
        byte[] HandleDigest,
        string DirectoryPath,
        string SnapshotPath,
        VerifiedAndroidTarget Target,
        DateTimeOffset ExpiresAtUtc,
        CancellationTokenSource ExpiryCancellation);
}

public sealed class UnsupportedAndroidPackageInstallerHandoff
    : IAndroidPackageInstallerHandoff
{
    private readonly string platformName;

    public UnsupportedAndroidPackageInstallerHandoff(string platformName)
    {
        this.platformName = string.IsNullOrWhiteSpace(platformName)
            ? "unknown platform"
            : platformName.Trim();
    }

    public Task<AndroidPackageInstallerHandoffResult> RequestInstallAsync(
        string privateVerifiedSnapshotPath,
        CancellationToken cancellationToken) =>
        Task.FromResult(new AndroidPackageInstallerHandoffResult(
            false,
            $"{platformName} installer handoff is explicitly unsupported without a separately verified platform contract."));
}
