using Android.Content.PM;
using Deep.Client.Maui.Core.Services;
using System.Security.Cryptography;

namespace Deep.Client.Maui;

internal sealed class AndroidArchivePackageSignerVerifier : IAndroidPackageSignerVerifier
{
    private readonly PackageManager packageManager;

    public AndroidArchivePackageSignerVerifier(PackageManager packageManager)
    {
        this.packageManager = packageManager
            ?? throw new ArgumentNullException(nameof(packageManager));
    }

    public Task<AndroidPackageSignerResult> VerifySnapshotAsync(
        string snapshotPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            PackageInfo? package;
            IEnumerable<Signature>? signatures;
            if (OperatingSystem.IsAndroidVersionAtLeast(28))
            {
                package = packageManager.GetPackageArchiveInfo(
                    snapshotPath,
                    PackageInfoFlags.SigningCertificates);
                signatures = package?.SigningInfo?.GetApkContentsSigners();
            }
            else
            {
                package = packageManager.GetPackageArchiveInfo(
                    snapshotPath,
                    PackageInfoFlags.Signatures);
#pragma warning disable CA1422 // Required compatibility path for Android API 26-27.
                signatures = package?.Signatures;
#pragma warning restore CA1422
            }
            if (package?.PackageName is not { Length: > 0 } packageId)
            {
                return Task.FromResult(Failed("Android could not parse the APK archive."));
            }

            var signerDigests = signatures?
                .Select(signature => signature.ToByteArray())
                .Where(bytes => bytes is { Length: > 0 })
                .Select(bytes => Convert.ToHexString(SHA256.HashData(bytes!)).ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (signerDigests is not { Length: 1 })
            {
                return Task.FromResult(Failed(
                    "Android APK must expose exactly one current package signer."));
            }

            string versionCode;
            if (OperatingSystem.IsAndroidVersionAtLeast(28))
            {
                versionCode = package.LongVersionCode.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
#pragma warning disable CA1422 // Required compatibility path for Android API 26-27.
                versionCode = package.VersionCode.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
#pragma warning restore CA1422
            }

            return Task.FromResult(new AndroidPackageSignerResult(
                true,
                packageId,
                signerDigests[0],
                versionCode,
                package.VersionName,
                null));
        }
        catch (Exception exception) when (
            exception is Java.Lang.Exception or IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(Failed(
                $"Android package signer inspection failed: {exception.Message}"));
        }
    }

    private static AndroidPackageSignerResult Failed(string message) =>
        new(false, null, null, null, null, message);
}
