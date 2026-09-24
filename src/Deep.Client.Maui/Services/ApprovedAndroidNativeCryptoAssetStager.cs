using System.Security.Cryptography;

namespace Deep.Client.Maui.Services;

internal sealed record ApprovedAndroidNativeCryptoAsset(
    string PackageAssetPath,
    string RelativeDestinationPath,
    long Bytes,
    string Sha256);

internal sealed record ApprovedAndroidNativeCryptoAssetInput(
    ApprovedAndroidNativeCryptoAsset Asset,
    Stream Source);

internal static class ApprovedAndroidNativeCryptoAssetStager
{
    internal const long MaximumAssetBytes = 4 * 1024 * 1024;

    internal static IReadOnlyList<ApprovedAndroidNativeCryptoAsset> Assets { get; } =
    [
        new(
            "deep-native/libdeep_mlkem.so",
            "runtimes/android-arm64/native/libdeep_mlkem.so",
            67_576,
            "5528f0ff05cbda00bdcd648ef72dcc870c3dde3535aa5f77457b554e93261fa5"),
        new(
            "deep-native/libdeep_mlkem_braid.so",
            "runtimes/android-arm64/native/libdeep_mlkem_braid.so",
            612_376,
            "dd51b21ddd836c84a978616596a749c34bf6258532e2ae2f931f235a124cacff"),
        new(
            "deep-native/libdeep_mldsa.so",
            "runtimes/android-arm64/native/libdeep_mldsa.so",
            79_112,
            "6e46e4df970f5376416af3c50fb39e580487a616d2541aa26d1d90eddf918cc9")
    ];

    internal static async Task StageAsync(
        string applicationBaseDirectory,
        IReadOnlyList<ApprovedAndroidNativeCryptoAssetInput> inputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationBaseDirectory);
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0)
            throw new InvalidDataException("The approved Android native asset set is empty.");

        var root = Path.GetFullPath(applicationBaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootPrefix = root + Path.DirectorySeparatorChar;
        var staged = new List<StagedAsset>(inputs.Count);
        var destinations = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            foreach (var input in inputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(input);
                ArgumentNullException.ThrowIfNull(input.Asset);
                ArgumentNullException.ThrowIfNull(input.Source);
                var asset = input.Asset;
                ValidateAsset(asset);
                if (Path.IsPathRooted(asset.RelativeDestinationPath))
                    throw new InvalidDataException("Android native asset destination must be relative.");

                var destination = Path.GetFullPath(Path.Combine(
                    root,
                    asset.RelativeDestinationPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(rootPrefix, StringComparison.Ordinal) ||
                    !destinations.Add(destination))
                    throw new InvalidDataException("Android native asset destination is invalid or duplicated.");

                var directory = Path.GetDirectoryName(destination)
                    ?? throw new InvalidDataException("Android native asset destination is invalid.");
                Directory.CreateDirectory(directory);
                var temporary = destination + ".staging-" + Guid.NewGuid().ToString("N");
                await CopyAndValidateAsync(input.Source, temporary, asset, cancellationToken)
                    .ConfigureAwait(false);
                staged.Add(new StagedAsset(temporary, destination));
            }

            // No destination is changed until the complete set has passed its exact
            // size and digest checks. Each final rename is atomic and idempotent.
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in staged)
            {
                File.Move(item.TemporaryPath, item.DestinationPath, overwrite: true);
                item.Committed = true;
            }
        }
        finally
        {
            foreach (var item in staged)
            {
                if (!item.Committed && File.Exists(item.TemporaryPath))
                    File.Delete(item.TemporaryPath);
            }
        }
    }

    private static void ValidateAsset(ApprovedAndroidNativeCryptoAsset asset)
    {
        if (string.IsNullOrWhiteSpace(asset.PackageAssetPath) ||
            string.IsNullOrWhiteSpace(asset.RelativeDestinationPath) ||
            asset.Bytes is <= 0 or > MaximumAssetBytes ||
            asset.Sha256.Length != 64 ||
            asset.Sha256.Any(static value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidDataException("Android native asset approval identity is invalid.");
    }

    private static async Task CopyAndValidateAsync(
        Stream source,
        string temporaryPath,
        ApprovedAndroidNativeCryptoAsset asset,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        byte[]? actual = null;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                buffer.Length,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                long bytes = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    bytes = checked(bytes + read);
                    if (bytes > asset.Bytes)
                        throw new InvalidDataException("Packaged Android native asset is oversized.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                }
                if (bytes != asset.Bytes)
                    throw new InvalidDataException("Packaged Android native asset length differs from its approval.");
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            actual = hash.GetHashAndReset();
            var expected = Convert.FromHexString(asset.Sha256);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(actual, expected))
                    throw new CryptographicException(
                        "Packaged Android native asset digest differs from its approval.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expected);
            }
        }
        catch
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (actual is not null)
                CryptographicOperations.ZeroMemory(actual);
        }
    }

    private sealed class StagedAsset(string temporaryPath, string destinationPath)
    {
        internal string TemporaryPath { get; } = temporaryPath;
        internal string DestinationPath { get; } = destinationPath;
        internal bool Committed { get; set; }
    }
}
