using System.Security.Cryptography;

namespace Deep.Client.Maui.Core.Services;

public sealed class OfflineAndroidUpdateVerifier : IOfflineAndroidUpdateVerifier
{
    private const long MaximumApkBytes = 1_073_741_824;
    private readonly UpdateTrustConfiguration configuration;
    private readonly PortableUpdateMetadataVerifier metadataVerifier;
    private readonly ITrustedUpdateStateStore stateStore;
    private readonly IAndroidPackageSignerVerifier packageSignerVerifier;
    private readonly string privateSnapshotRoot;

    public OfflineAndroidUpdateVerifier(
        UpdateTrustConfiguration configuration,
        PortableUpdateMetadataVerifier metadataVerifier,
        ITrustedUpdateStateStore stateStore,
        IAndroidPackageSignerVerifier packageSignerVerifier,
        string privateSnapshotRoot)
    {
        this.configuration = configuration
            ?? throw new ArgumentNullException(nameof(configuration));
        this.metadataVerifier = metadataVerifier
            ?? throw new ArgumentNullException(nameof(metadataVerifier));
        this.stateStore = stateStore
            ?? throw new ArgumentNullException(nameof(stateStore));
        this.packageSignerVerifier = packageSignerVerifier
            ?? throw new ArgumentNullException(nameof(packageSignerVerifier));
        ArgumentException.ThrowIfNullOrWhiteSpace(privateSnapshotRoot);
        this.privateSnapshotRoot = Path.GetFullPath(privateSnapshotRoot);
    }

    public async Task<OfflineAndroidPackageVerification> VerifyAsync(
        OfflineAndroidPackageRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!configuration.Enabled ||
            configuration.ProvisionedTrustedRoot.IsEmpty)
        {
            return Failed(request, "Проверка обновлений не настроена: доверенный корень отсутствует.");
        }

        try
        {
            var state = await stateStore.LoadAsync(cancellationToken);
            if (state is null)
            {
                if (!request.Metadata.TrustedRoot.Span.SequenceEqual(
                    configuration.ProvisionedTrustedRoot.Span))
                {
                    throw new InvalidDataException(
                        "Initial metadata root does not match the explicitly provisioned trusted root.");
                }
                state = PortableUpdateMetadataVerifier.CreateInitialState(
                    configuration.ProvisionedTrustedRoot);
                await stateStore.SaveAsync(state, cancellationToken);
            }

            var verified = await metadataVerifier.VerifyAsync(
                request.Metadata,
                state,
                request.UpdateStartUtc,
                request.TargetPath,
                (rootState, ct) => stateStore.SaveAsync(rootState, ct),
                cancellationToken);

            var target = verified.AndroidTarget;
            if (target.Length > MaximumApkBytes)
            {
                throw new InvalidDataException("Signed APK exceeds the 1 GiB verification limit.");
            }

            Directory.CreateDirectory(privateSnapshotRoot);
            var verificationDirectory = Path.Combine(
                privateSnapshotRoot,
                $"deep-update-{Guid.NewGuid():N}");
            Directory.CreateDirectory(verificationDirectory);
            var snapshotPath = Path.Combine(verificationDirectory, "candidate.apk");
            try
            {
                var observedHash = await CopyBoundedSnapshotAsync(
                    request.ApkPath,
                    snapshotPath,
                    target.Length,
                    cancellationToken);
                if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(observedHash),
                    Convert.FromHexString(target.Sha256)))
                {
                    throw new InvalidDataException(
                        "APK SHA-256 does not match signed update metadata.");
                }

                var signer = await packageSignerVerifier.VerifySnapshotAsync(
                    snapshotPath,
                    cancellationToken);
                var snapshotAfterSigner = await ComputeSha256Async(
                    snapshotPath,
                    cancellationToken);
                if (!IsExactSha256(snapshotAfterSigner, observedHash))
                {
                    throw new InvalidDataException(
                        "Private APK snapshot changed during package signer verification.");
                }
                if (!signer.IsValid)
                {
                    throw new InvalidDataException(
                        signer.Failure ?? "Android package signer verification failed.");
                }
                if (!string.Equals(signer.PackageId, target.PackageId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Android package ID does not match signed update metadata.");
                }
                if (!IsExactSha256(signer.SignerCertificateSha256, target.PackageSignerSha256))
                {
                    throw new InvalidDataException(
                        "Android package signer does not match signed update metadata.");
                }
                if (!string.Equals(signer.VersionCode, target.VersionCode, StringComparison.Ordinal) ||
                    !string.Equals(signer.VersionName, target.VersionName, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Android package version does not match signed update metadata.");
                }

                await stateStore.SaveAsync(verified.State, cancellationToken);
                return new OfflineAndroidPackageVerification(
                    true,
                    "Пакет проверен. Перед передачей установщику подтвердите версию вручную.",
                    request.SourceLabel,
                    request.TargetPath,
                    target.PackageId,
                    target.VersionName,
                    target.VersionCode,
                    target.SourceCommit,
                    target.MetadataExpiresAtUtc,
                    null,
                    observedHash);
            }
            finally
            {
                try
                {
                    Directory.Delete(verificationDirectory, recursive: true);
                }
                catch
                {
                    // App-private cleanup is best effort; the verified snapshot is never installed here.
                }
            }
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException or
            CryptographicException or KeyNotFoundException or InvalidOperationException or
            FormatException or ArgumentException)
        {
            return Failed(request, exception.Message);
        }
    }

    private static OfflineAndroidPackageVerification Failed(
        OfflineAndroidPackageRequest request,
        string failure) =>
        new(
            false,
            "Проверка не пройдена. Установка заблокирована без возможности обхода.",
            request.SourceLabel,
            request.TargetPath,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            DateTimeOffset.MinValue,
            failure,
            null);

    private static async Task<string> CopyBoundedSnapshotAsync(
        string sourcePath,
        string snapshotPath,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        if (expectedLength < 0 || expectedLength > MaximumApkBytes)
        {
            throw new InvalidDataException("Signed APK length is invalid.");
        }

        await using var source = new FileStream(
            Path.GetFullPath(sourcePath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length != expectedLength)
        {
            throw new InvalidDataException(
                "APK length does not match signed update metadata.");
        }

        await using var snapshot = new FileStream(
            snapshotPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long copied = 0;
        while (copied < expectedLength)
        {
            var requested = (int)Math.Min(buffer.Length, expectedLength - copied);
            var count = await source.ReadAsync(
                buffer.AsMemory(0, requested),
                cancellationToken);
            if (count == 0)
            {
                throw new InvalidDataException("APK changed while creating the verification snapshot.");
            }
            await snapshot.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            hash.AppendData(buffer, 0, count);
            copied += count;
        }
        if (await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) != 0)
        {
            throw new InvalidDataException("APK changed while creating the verification snapshot.");
        }
        await snapshot.FlushAsync(cancellationToken);
        snapshot.Flush(flushToDisk: true);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool IsExactSha256(string? observed, string expected)
    {
        try
        {
            return observed is { Length: 64 } &&
                CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(observed),
                    Convert.FromHexString(expected));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static async Task<string> ComputeSha256Async(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}

public sealed class UnsupportedPlatformPackageSignerVerifier : IAndroidPackageSignerVerifier
{
    private readonly string platformName;

    public UnsupportedPlatformPackageSignerVerifier(string platformName)
    {
        this.platformName = string.IsNullOrWhiteSpace(platformName)
            ? "unknown"
            : platformName;
    }

    public Task<AndroidPackageSignerResult> VerifySnapshotAsync(
        string snapshotPath,
        CancellationToken cancellationToken) =>
        Task.FromResult(new AndroidPackageSignerResult(
            false,
            null,
            null,
            null,
            null,
            platformName.Equals("iOS", StringComparison.OrdinalIgnoreCase)
                ? "iOS does not permit this Android sideload verification/install flow; Apple signing, provisioning and distribution policy still apply."
                : $"Android package signer verification is unavailable on {platformName}."));
}
