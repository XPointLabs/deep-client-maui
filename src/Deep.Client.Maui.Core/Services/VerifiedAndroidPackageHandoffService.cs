using System.Security.Cryptography;

namespace Deep.Client.Maui.Core.Services;

public sealed class VerifiedAndroidPackageHandoffService
    : IVerifiedAndroidPackageHandoffService
{
    public const long AbsoluteMaximumPackageBytes = 1_073_741_824;
    public const string OwnedStoreDirectoryName =
        ".deep-verified-update-handoff-v1";
    public static readonly TimeSpan AbsoluteMaximumTimeToLive =
        TimeSpan.FromMinutes(15);

    private const string StoreMarkerName = ".deep-store-owner";
    private const string StoreMarkerContent =
        "deep.verified-update-handoff.store.v1\n";
    private const string PackageMarkerName = ".deep-package-owner";
    private const string PackageMarkerContent =
        "deep.verified-update-handoff.package.v1\n";
    private const int DeleteAttempts = 3;

    private readonly string ownedStoreRoot;
    private readonly IAndroidPackageSignerVerifier packageSignerVerifier;
    private readonly IAndroidPackageInstallerHandoff installerHandoff;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan timeToLive;
    private readonly long maximumPackageBytes;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HashSet<string> pendingCleanupDirectories =
        new(StringComparer.OrdinalIgnoreCase);
    private ActiveSnapshot? activeSnapshot;

    public VerifiedAndroidPackageHandoffService(
        string appPrivateRoot,
        IAndroidPackageSignerVerifier packageSignerVerifier,
        IAndroidPackageInstallerHandoff installerHandoff,
        TimeProvider? timeProvider = null,
        TimeSpan? timeToLive = null,
        long maximumPackageBytes = AbsoluteMaximumPackageBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appPrivateRoot);
        var canonicalParent = Path.GetFullPath(appPrivateRoot);
        ownedStoreRoot = Path.Combine(
            canonicalParent,
            OwnedStoreDirectoryName);
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

        Directory.CreateDirectory(canonicalParent);
        if (IsReparsePoint(canonicalParent))
        {
            throw new InvalidDataException(
                "App-private installer handoff parent cannot be a reparse point.");
        }
        InitializeOwnedStore();
        CleanupOwnedPackagesOnStartup();
    }

    public async Task<PreservedAndroidPackageHandle> PreserveVerifiedSnapshotAsync(
        string verifiedSnapshotPath,
        VerifiedAndroidTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedSnapshotPath);
        ArgumentNullException.ThrowIfNull(target);
        ValidateTarget(target);

        var sourcePath = Path.GetFullPath(verifiedSnapshotPath);
        if (IsInsideRoot(ownedStoreRoot, sourcePath))
        {
            throw new InvalidDataException(
                "Installer handoff staging input cannot be inside the owned store.");
        }
        var observed = await InspectPathAsync(
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

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RetryPendingCleanupUnsafe();
            CleanupActiveSnapshotUnsafe();

            var handleBytes = RandomNumberGenerator.GetBytes(32);
            var handle = Base64UrlEncode(handleBytes);
            var handleDigest = SHA256.HashData(handleBytes);
            var directoryPath = Path.Combine(
                ownedStoreRoot,
                $"pkg-{LowerHex(RandomNumberGenerator.GetBytes(24))}");
            var snapshotPath = Path.Combine(
                directoryPath,
                $"{LowerHex(RandomNumberGenerator.GetBytes(24))}.apk");
            Directory.CreateDirectory(directoryPath);
            try
            {
                WriteMarker(
                    Path.Combine(directoryPath, PackageMarkerName),
                    PackageMarkerContent);
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
                if (!DeleteOwnedPackageDirectory(directoryPath))
                {
                    pendingCleanupDirectories.Add(directoryPath);
                }
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
        ActiveSnapshot? claimed;
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            RetryPendingCleanupUnsafe(throwIfPending: false);
            claimed = ClaimActiveSnapshotUnsafe(handle);
        }
        finally
        {
            gate.Release();
        }

        if (claimed is null)
        {
            return Blocked(
                "The installer handoff handle is invalid or has already been used.");
        }

        VerifiedAndroidPackageHandoffResult outcome;
        var cleanupPending = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = claimed.ExpiresAtUtc - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return Blocked("The installer handoff handle expired.");
            }
            using var expiryCancellation =
                new CancellationTokenSource(remaining, timeProvider);
            using var effectiveCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    expiryCancellation.Token);
            outcome = await ExecuteClaimedHandoffAsync(
                claimed,
                userConfirmed,
                effectiveCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            cleanupPending = !DeleteOwnedPackageDirectory(claimed.DirectoryPath);
            if (cleanupPending)
            {
                await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    pendingCleanupDirectories.Add(claimed.DirectoryPath);
                }
                finally
                {
                    gate.Release();
                }
            }
        }

        return outcome with { CleanupPending = cleanupPending };
    }

    private async Task<VerifiedAndroidPackageHandoffResult>
        ExecuteClaimedHandoffAsync(
            ActiveSnapshot claimed,
            bool userConfirmed,
            CancellationToken cancellationToken)
    {
        if (!userConfirmed)
        {
            return Blocked(
                "Explicit user confirmation is required before installer handoff.");
        }
        if (timeProvider.GetUtcNow() >= claimed.ExpiresAtUtc)
        {
            return Blocked("The installer handoff handle expired.");
        }

        try
        {
            var target = claimed.Target;
            await using var verifiedPackage = new FileStream(
                claimed.SnapshotPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var beforeIdentity = await InspectOpenStreamAsync(
                verifiedPackage,
                target.Length,
                cancellationToken).ConfigureAwait(false);
            if (!FixedTimeSha256Equals(beforeIdentity, target.Sha256))
            {
                return Blocked(
                    "The verified package snapshot changed before installer handoff.");
            }

            var identity = await packageSignerVerifier.VerifySnapshotAsync(
                    claimed.SnapshotPath,
                    cancellationToken)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!MatchesTarget(identity, target))
            {
                return Blocked("Android package identity revalidation failed.");
            }

            var afterIdentity = await InspectOpenStreamAsync(
                verifiedPackage,
                target.Length,
                cancellationToken).ConfigureAwait(false);
            if (!FixedTimeSha256Equals(afterIdentity, target.Sha256))
            {
                return Blocked(
                    "The verified package snapshot changed during identity revalidation.");
            }

            verifiedPackage.Position = 0;
            AndroidPackageInstallerHandoffResult? platformResult;
            try
            {
                platformResult = await installerHandoff.RequestInstallAsync(
                        verifiedPackage,
                        target.Length,
                        cancellationToken)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return Blocked(
                    "The platform installer rejected the verified package handoff.");
            }

            var afterAdapter = await InspectOpenStreamAsync(
                verifiedPackage,
                target.Length,
                cancellationToken).ConfigureAwait(false);
            if (!FixedTimeSha256Equals(afterAdapter, target.Sha256))
            {
                return Blocked(
                    "The verified package snapshot changed during installer handoff.");
            }
            if (platformResult is not { IsAccepted: true } ||
                platformResult.OwnedLength != target.Length ||
                !FixedTimeSha256Equals(
                    platformResult.OwnedSha256,
                    target.Sha256))
            {
                return Blocked(
                    "The platform installer did not prove ownership of the exact verified package.");
            }

            return new VerifiedAndroidPackageHandoffResult(
                true,
                "Verified package was copied by the platform installer after explicit confirmation.",
                null,
                CleanupPending: false);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException or
            CryptographicException or FormatException or ArgumentException)
        {
            return Blocked("Verified package handoff failed closed.");
        }
    }

    private ActiveSnapshot? ClaimActiveSnapshotUnsafe(string handle)
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

    private void InitializeOwnedStore()
    {
        var alreadyExisted = Directory.Exists(ownedStoreRoot);
        if (!alreadyExisted)
        {
            Directory.CreateDirectory(ownedStoreRoot);
            WriteMarker(
                Path.Combine(ownedStoreRoot, StoreMarkerName),
                StoreMarkerContent);
        }

        if (IsReparsePoint(ownedStoreRoot) ||
            !MarkerMatches(
                Path.Combine(ownedStoreRoot, StoreMarkerName),
                StoreMarkerContent))
        {
            throw new InvalidDataException(
                "Private installer handoff store ownership marker is invalid.");
        }
    }

    private void CleanupOwnedPackagesOnStartup()
    {
        foreach (var directoryPath in EnumerateOwnedPackageDirectories())
        {
            if (!DeleteOwnedPackageDirectory(directoryPath))
            {
                pendingCleanupDirectories.Add(directoryPath);
            }
        }
        if (pendingCleanupDirectories.Count > 0)
        {
            throw new IOException(
                "Private installer handoff cleanup is pending.");
        }
    }

    private void CleanupActiveSnapshotUnsafe()
    {
        if (activeSnapshot is null)
        {
            return;
        }

        var snapshot = activeSnapshot;
        activeSnapshot = null;
        snapshot.ExpiryCancellation.Cancel();
        if (!DeleteOwnedPackageDirectory(snapshot.DirectoryPath))
        {
            pendingCleanupDirectories.Add(snapshot.DirectoryPath);
            throw new IOException(
                "Private installer handoff cleanup is pending.");
        }
    }

    private void RetryPendingCleanupUnsafe(bool throwIfPending = true)
    {
        foreach (var directoryPath in pendingCleanupDirectories.ToArray())
        {
            if (DeleteOwnedPackageDirectory(directoryPath))
            {
                pendingCleanupDirectories.Remove(directoryPath);
            }
        }
        if (throwIfPending && pendingCleanupDirectories.Count > 0)
        {
            throw new IOException(
                "Private installer handoff cleanup is pending.");
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
            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(activeSnapshot, candidate) &&
                    timeProvider.GetUtcNow() >= candidate.ExpiresAtUtc)
                {
                    activeSnapshot = null;
                    if (!DeleteOwnedPackageDirectory(candidate.DirectoryPath))
                    {
                        pendingCleanupDirectories.Add(candidate.DirectoryPath);
                    }
                }
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // A claim or replacement consumed the snapshot.
        }
        catch
        {
            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                pendingCleanupDirectories.Add(candidate.DirectoryPath);
            }
            finally
            {
                gate.Release();
            }
        }
        finally
        {
            candidate.ExpiryCancellation.Dispose();
        }
    }

    private IEnumerable<string> EnumerateOwnedPackageDirectories()
    {
        foreach (var directoryPath in Directory.EnumerateDirectories(
            ownedStoreRoot,
            "pkg-*",
            SearchOption.TopDirectoryOnly))
        {
            if (IsOwnedPackageDirectory(directoryPath))
            {
                yield return directoryPath;
            }
        }
    }

    private bool DeleteOwnedPackageDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return true;
        }
        if (!IsOwnedPackageDirectory(directoryPath) ||
            ContainsReparsePoint(directoryPath))
        {
            return false;
        }

        for (var attempt = 0; attempt < DeleteAttempts; attempt++)
        {
            try
            {
                if (TryDeleteOwnedPackageOnce(directoryPath))
                {
                    return true;
                }
            }
            catch (IOException)
            {
                // A live file handle can make cleanup temporarily unavailable.
            }
            catch (UnauthorizedAccessException)
            {
                // Startup and the next operation retry this owned path.
            }
        }
        return !Directory.Exists(directoryPath);
    }

    private static bool TryDeleteOwnedPackageOnce(string directoryPath)
    {
        var markerPath = Path.Combine(directoryPath, PackageMarkerName);
        foreach (var entry in Directory.EnumerateFileSystemEntries(
            directoryPath,
            "*",
            SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(entry, markerPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.Delete(entry, recursive: true);
            }
            else
            {
                File.Delete(entry);
            }
        }

        File.Delete(markerPath);
        try
        {
            Directory.Delete(directoryPath, recursive: false);
            return true;
        }
        catch
        {
            if (!File.Exists(markerPath))
            {
                try
                {
                    WriteMarker(markerPath, PackageMarkerContent);
                }
                catch
                {
                    // The caller records pending cleanup without claiming success.
                }
            }
            throw;
        }
    }

    private bool IsOwnedPackageDirectory(string directoryPath)
    {
        try
        {
            var canonical = Path.GetFullPath(directoryPath);
            if (!string.Equals(
                    Path.GetDirectoryName(canonical),
                    ownedStoreRoot,
                    StringComparison.OrdinalIgnoreCase) ||
                !IsPackageDirectoryName(Path.GetFileName(canonical)) ||
                IsReparsePoint(canonical))
            {
                return false;
            }
            return MarkerMatches(
                Path.Combine(canonical, PackageMarkerName),
                PackageMarkerContent);
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsReparsePoint(string root)
    {
        try
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                foreach (var entry in Directory.EnumerateFileSystemEntries(
                    current,
                    "*",
                    SearchOption.TopDirectoryOnly))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        return true;
                    }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                    }
                }
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool IsInsideRoot(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative.Length == 0 ||
            (!relative.StartsWith("..", StringComparison.Ordinal) &&
             !Path.IsPathRooted(relative));
    }

    private static bool IsPackageDirectoryName(string value) =>
        value.Length == 52 &&
        value.StartsWith("pkg-", StringComparison.Ordinal) &&
        value.AsSpan(4).ContainsOnlyLowerHex();

    private static void WriteMarker(string markerPath, string content)
    {
        using var stream = new FileStream(
            markerPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream);
        writer.Write(content);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static bool MarkerMatches(string markerPath, string expected)
    {
        try
        {
            return File.ReadAllText(markerPath) == expected &&
                !IsReparsePoint(markerPath);
        }
        catch
        {
            return false;
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
            throw new InvalidDataException(
                "Signed Android package handoff metadata is invalid.");
        }
    }

    private async Task<string> InspectPathAsync(
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
        return await InspectOpenStreamAsync(
            stream,
            expectedLength,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> InspectOpenStreamAsync(
        FileStream stream,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        if (stream.Length != expectedLength ||
            stream.Length > maximumPackageBytes)
        {
            throw new InvalidDataException(
                "Private package snapshot length changed before installer handoff.");
        }
        stream.Position = 0;
        var digest = await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        stream.Position = 0;
        return LowerHex(digest);
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
        value.AsSpan().ContainsOnlyLowerHex();

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

    private static string LowerHex(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(value).ToLowerInvariant();

    private static VerifiedAndroidPackageHandoffResult Blocked(string failure) =>
        new(
            false,
            "Installer handoff is blocked without a bypass.",
            failure,
            CleanupPending: false);

    private sealed record ActiveSnapshot(
        byte[] HandleDigest,
        string DirectoryPath,
        string SnapshotPath,
        VerifiedAndroidTarget Target,
        DateTimeOffset ExpiresAtUtc,
        CancellationTokenSource ExpiryCancellation);
}

file static class LowerHexSpanExtensions
{
    public static bool ContainsOnlyLowerHex(this ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }
        return true;
    }
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
        Stream verifiedPackage,
        long expectedLength,
        CancellationToken cancellationToken) =>
        Task.FromResult(new AndroidPackageInstallerHandoffResult(
            false,
            0,
            null,
            $"{platformName} installer handoff is explicitly unsupported without a separately verified platform contract."));
}
