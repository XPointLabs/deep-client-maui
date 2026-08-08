using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Deep.Client.Shared.Services;

#if ANDROID
using Android.Content.PM;
#elif WINDOWS
using System.Security.Cryptography.Pkcs;
using Windows.ApplicationModel;
#endif

namespace Deep.Client.Maui.Services;

internal static class ProductionMailboxClientIdentityAttestor
{
#if WINDOWS
    internal const int MaximumWindowsInventoryEntries = 16_384;
    internal const int MaximumWindowsSignatureBytes = 1024 * 1024;
#endif
    public static async Task<ProductionMailboxClientApprovalIdentity> AttestAsync(
        CancellationToken cancellationToken = default)
    {
#if ANDROID
        if (!OperatingSystem.IsAndroidVersionAtLeast(28))
            throw new PlatformNotSupportedException(
                "Production Android artifact attestation requires Android 9 or later for signer lineage verification.");
        var context = Android.App.Application.Context;
        var packageName = context.PackageName;
        if (!ProductionMailboxBuildTrustFloor.TryLoadAndroidIdentity(out var buildIdentity) ||
            buildIdentity is null ||
            !ProductionMailboxBuildTrustFloor.TryLoad(out var trustAnchor) ||
            trustAnchor is null)
            throw new InvalidOperationException("production-credentials-unavailable");
        var manager = context.PackageManager ?? throw new InvalidDataException(
            "Production Android package manager is unavailable.");
        var assets = context.Assets ?? throw new InvalidDataException(
            "Production Android asset manager is unavailable.");
        var transparency = await ReadAndroidTransparencyAssetAsync(assets, cancellationToken)
            .ConfigureAwait(false);
        var before = CaptureAndroidPackage(manager, packageName, buildIdentity);
        var beforeTransparency = await ProductionAndroidCodeTransparencyVerifier.VerifyRuntimeAsync(
            transparency,
            trustAnchor.MrXPublicKeySha256,
            buildIdentity.ApplicationId,
            buildIdentity.VersionCode,
            before.Lineage.Select(static hash => (ReadOnlyMemory<byte>)hash).ToArray(),
            before.InstalledArtifacts,
            cancellationToken).ConfigureAwait(false);
        var beforeInventory = await ProductionMailboxArtifactSetDigest.ComputeAsync(
            before.InventoryIdentity,
            before.Lineage.Select(static hash => (ReadOnlyMemory<byte>)hash).ToArray(),
            before.Artifacts,
            cancellationToken).ConfigureAwait(false);
        var after = CaptureAndroidPackage(manager, packageName, buildIdentity);
        var afterTransparency = await ProductionAndroidCodeTransparencyVerifier.VerifyRuntimeAsync(
            transparency,
            trustAnchor.MrXPublicKeySha256,
            buildIdentity.ApplicationId,
            buildIdentity.VersionCode,
            after.Lineage.Select(static hash => (ReadOnlyMemory<byte>)hash).ToArray(),
            after.InstalledArtifacts,
            cancellationToken).ConfigureAwait(false);
        var afterInventory = await ProductionMailboxArtifactSetDigest.ComputeAsync(
            after.InventoryIdentity,
            after.Lineage.Select(static hash => (ReadOnlyMemory<byte>)hash).ToArray(),
            after.Artifacts,
            cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(before.CurrentSigner, after.CurrentSigner) ||
            !CryptographicOperations.FixedTimeEquals(beforeInventory, afterInventory) ||
            !CryptographicOperations.FixedTimeEquals(beforeTransparency, afterTransparency))
            throw new InvalidDataException(
                "Production Android package changed during attestation.");
        return new ProductionMailboxClientApprovalIdentity(
            MailboxClientPlatform.Android,
            buildIdentity.ApplicationId,
            before.CurrentSigner,
            beforeTransparency);
#elif WINDOWS
        Package package;
        try
        {
            package = Package.Current;
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException(
                "Production Windows release requires an installed MSIX identity.", exception);
        }
        var root = package.InstalledLocation.Path;
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) ||
            string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath) ||
            !string.Equals(
                Path.GetFileName(processPath),
                ProductionMailboxControlPlaneVerifier.WindowsApplicationIdentity,
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(processPath).StartsWith(
                Path.GetFullPath(root) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Production Windows artifact identity is invalid.");
        var signaturePath = Path.Combine(root, "AppxSignature.p7x");
        var manifestPath = Path.Combine(root, "AppxManifest.xml");
        var blockMapPath = Path.Combine(root, "AppxBlockMap.xml");
        if (!File.Exists(signaturePath) || !File.Exists(manifestPath) ||
            !File.Exists(blockMapPath))
            throw new InvalidDataException(
                "Production Windows package signature or manifest is unavailable.");
        var signature = await ReadBoundedFileAsync(
            signaturePath, MaximumWindowsSignatureBytes, cancellationToken)
            .ConfigureAwait(false);
        X509Certificate2 signer;
        try
        {
            // APPX/MSIX wraps the CMS SignedData with the required four-byte "PKCX"
            // package signature marker. SignedCms accepts the DER payload only.
            if (signature.Length <= 4 ||
                !signature.AsSpan(0, 4).SequenceEqual("PKCX"u8))
                throw new CryptographicException(
                    "MSIX signature framing is invalid.");
            var signed = new SignedCms();
            signed.Decode(signature.AsSpan(4));
            signed.CheckSignature(verifySignatureOnly: true);
            if (signed.SignerInfos.Count != 1)
                throw new CryptographicException(
                    "MSIX signature has an ambiguous signer set.");
            var signingCertificate = signed.SignerInfos[0].Certificate;
            if (signingCertificate is null)
                throw new CryptographicException(
                    "MSIX signature has no signing certificate.");
            signer = X509CertificateLoader.LoadCertificate(signingCertificate.RawData);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException(
                "Production Windows MSIX signature is invalid.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
        using (signer)
        {
            var signerHash = SHA256.HashData(signer.RawData);
            var beforeFiles = CaptureWindowsInventory(root);
            var beforeHash = await ProductionMailboxArtifactSetDigest.ComputeAsync(
                package.Id.FullName,
                [signerHash],
                beforeFiles,
                cancellationToken).ConfigureAwait(false);
            var afterFiles = CaptureWindowsInventory(root);
            var afterHash = await ProductionMailboxArtifactSetDigest.ComputeAsync(
                package.Id.FullName,
                [signerHash],
                afterFiles,
                cancellationToken).ConfigureAwait(false);
            if (!beforeFiles.Select(static file => file.LogicalName).SequenceEqual(
                    afterFiles.Select(static file => file.LogicalName),
                    StringComparer.Ordinal) ||
                !CryptographicOperations.FixedTimeEquals(beforeHash, afterHash))
                throw new InvalidDataException(
                    "Production Windows package changed during attestation.");
            return new ProductionMailboxClientApprovalIdentity(
                MailboxClientPlatform.Windows,
                ProductionMailboxControlPlaneVerifier.WindowsApplicationIdentity,
                signerHash,
                beforeHash);
        }
#else
        await Task.CompletedTask;
        throw new PlatformNotSupportedException(
            "Production authenticated MAU2 supports Android and Windows only.");
#endif
    }

#if ANDROID
    private static AndroidPackageSnapshot CaptureAndroidPackage(
        PackageManager manager,
        string? packageName,
        ProductionAndroidBuildIdentity buildIdentity)
    {
        if (string.IsNullOrEmpty(packageName))
            throw new InvalidDataException("Production Android package identity is invalid.");
        var info = manager.GetPackageInfo(
            packageName!, PackageInfoFlags.SigningCertificates) ??
            throw new InvalidDataException(
                "Production Android package identity is unavailable.");
        if (info.SigningInfo?.HasMultipleSigners == true)
            throw new InvalidDataException("Production Android signer set is ambiguous.");
        var signers = info.SigningInfo?.GetApkContentsSigners();
        var history = info.SigningInfo?.GetSigningCertificateHistory();
        if (signers is null || signers.Length != 1 ||
            history is null || history.Length is < 1 or > 32)
            throw new InvalidDataException(
                "Production Android signer lineage is unavailable.");
        var currentSigner = CertificateHash(signers[0]);
        var lineage = history.Select(CertificateHash).ToArray();
        if (lineage.Count(hash => CryptographicOperations.FixedTimeEquals(
                hash, currentSigner)) != 1)
            throw new InvalidDataException(
                "Production Android installed Play app-signing lineage is not approved.");
        ProductionMailboxBuildTrustFloor.VerifyInstalledAndroidTuple(
            buildIdentity,
            packageName,
            checked((ulong)info.LongVersionCode),
            lineage.Select(static hash => (ReadOnlyMemory<byte>)hash).ToArray());

        var application = info.ApplicationInfo;
        var sourcePath = application?.SourceDir;
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new InvalidDataException("Production Android base artifact is unavailable.");
        var artifacts = new List<ProductionMailboxArtifactFile>
        {
            new("android/base.apk", sourcePath)
        };
        var installedArtifacts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["base"] = sourcePath
        };
        var splitPaths = application?.SplitSourceDirs ?? [];
        var splitNames = application?.SplitNames ?? [];
        if (splitPaths.Count != splitNames.Count)
            throw new InvalidDataException(
                "Production Android split artifact inventory is ambiguous.");
        for (var index = 0; index < splitPaths.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(splitNames[index]) ||
                string.IsNullOrWhiteSpace(splitPaths[index]) ||
                !File.Exists(splitPaths[index]))
                throw new InvalidDataException(
                    "Production Android split artifact inventory is invalid.");
            artifacts.Add(new ProductionMailboxArtifactFile(
                "android/split/" + splitNames[index] + ".apk",
                splitPaths[index]));
            if (!installedArtifacts.TryAdd(splitNames[index], splitPaths[index]))
                throw new InvalidDataException(
                    "Production Android split identities are duplicated.");
        }
        return new AndroidPackageSnapshot(
            buildIdentity.ApplicationId + ":" + buildIdentity.VersionCode,
            currentSigner,
            lineage,
            artifacts,
            installedArtifacts);
    }

    private static async Task<byte[]> ReadAndroidTransparencyAssetAsync(
        Android.Content.Res.AssetManager assets,
        CancellationToken cancellationToken)
    {
        await using var stream = assets.Open(
            ProductionAndroidCodeTransparencyVerifier.AssetPath,
            Android.Content.Res.Access.Streaming) ??
            throw new InvalidDataException(
                "Production Android transparency manifest is unavailable.");
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read >
                ProductionAndroidTransparencyManifestCodec.MaximumEncodedBytes)
                throw new InvalidDataException(
                    "Production Android transparency manifest is oversized.");
            output.Write(buffer, 0, read);
        }
        if (output.Length == 0)
            throw new InvalidDataException(
                "Production Android transparency manifest is empty.");
        return output.ToArray();
    }

    private static byte[] CertificateHash(Android.Content.PM.Signature certificate)
    {
        var raw = certificate.ToByteArray();
        if (raw is not { Length: > 0 })
            throw new InvalidDataException(
                "Production Android signer certificate is unavailable.");
        try { return SHA256.HashData(raw); }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }

    private sealed record AndroidPackageSnapshot(
        string InventoryIdentity,
        byte[] CurrentSigner,
        byte[][] Lineage,
        IReadOnlyList<ProductionMailboxArtifactFile> Artifacts,
        IReadOnlyDictionary<string, string> InstalledArtifacts);
#elif WINDOWS
    internal static ProductionMailboxArtifactFile[] CaptureWindowsInventory(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(fullRoot));
        var artifacts = new List<ProductionMailboxArtifactFile>();
        var entryCount = 0;
        while (pending.Count != 0)
        {
            var directory = pending.Pop();
            directory.Refresh();
            if (!directory.Exists ||
                (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    "Production Windows package directory is unavailable or unsafe.");
            foreach (var entry in directory.EnumerateFileSystemInfos(
                         "*", SearchOption.TopDirectoryOnly))
            {
                if (++entryCount > MaximumWindowsInventoryEntries)
                    throw new InvalidDataException(
                        "Production Windows package inventory exceeds its bound.");
                entry.Refresh();
                if (!entry.Exists ||
                    (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException(
                        "Production Windows package inventory contains a reparse point.");
                switch (entry)
                {
                    case DirectoryInfo child:
                        pending.Push(child);
                        break;
                    case FileInfo file:
                        artifacts.Add(new ProductionMailboxArtifactFile(
                            "windows/" + Path.GetRelativePath(fullRoot, file.FullName)
                                .Replace('\\', '/'),
                            file.FullName));
                        break;
                    default:
                        throw new InvalidDataException(
                            "Production Windows package inventory entry is unsupported.");
                }
            }
        }
        if (artifacts.Count == 0)
            throw new InvalidDataException(
                "Production Windows package inventory is empty.");
        return artifacts
            .OrderBy(static file => file.LogicalName, StringComparer.Ordinal)
            .ToArray();
    }

    internal static async Task<byte[]> ReadBoundedFileAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        await using var stream = new FileStream(
            Path.GetFullPath(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 || stream.Length > maximumBytes)
            throw new InvalidDataException(
                "Production Windows package signature is empty or oversized.");
        var result = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(result, cancellationToken).ConfigureAwait(false);
        if (stream.Length != result.Length)
        {
            CryptographicOperations.ZeroMemory(result);
            throw new InvalidDataException(
                "Production Windows package signature changed during read.");
        }
        return result;
    }
#endif

}
