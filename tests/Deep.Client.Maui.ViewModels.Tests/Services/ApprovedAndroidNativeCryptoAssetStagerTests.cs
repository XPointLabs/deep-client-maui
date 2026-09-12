using System.Security.Cryptography;
using Deep.Client.Maui.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class ApprovedAndroidNativeCryptoAssetStagerTests
{
    [Fact]
    public async Task ExactCompletePairIsStagedAtApprovedRelativePaths()
    {
        var root = CreateRoot();
        try
        {
            var whole = Enumerable.Range(0, 257).Select(value => (byte)value).ToArray();
            var braid = Enumerable.Range(0, 769).Select(value => (byte)(value * 3)).ToArray();

            await ApprovedAndroidNativeCryptoAssetStager.StageAsync(
                root,
                [Input("whole", "runtimes/android-arm64/native/libdeep_mlkem.so", whole),
                 Input("braid", "runtimes/android-arm64/native/libdeep_mlkem_braid.so", braid)]);

            Assert.Equal(whole, await File.ReadAllBytesAsync(Path.Combine(
                root, "runtimes", "android-arm64", "native", "libdeep_mlkem.so")));
            Assert.Equal(braid, await File.ReadAllBytesAsync(Path.Combine(
                root, "runtimes", "android-arm64", "native", "libdeep_mlkem_braid.so")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidSecondAssetLeavesBothExistingDestinationsUntouched()
    {
        var root = CreateRoot();
        try
        {
            var directory = Path.Combine(root, "runtimes", "android-arm64", "native");
            Directory.CreateDirectory(directory);
            var firstPath = Path.Combine(directory, "first.so");
            var secondPath = Path.Combine(directory, "second.so");
            await File.WriteAllBytesAsync(firstPath, [0xa1]);
            await File.WriteAllBytesAsync(secondPath, [0xb2]);
            var first = new byte[] { 1, 2, 3 };
            var second = new byte[] { 4, 5, 6 };
            var invalidSecond = Input("second", "runtimes/android-arm64/native/second.so", second);
            invalidSecond = invalidSecond with
            {
                Asset = invalidSecond.Asset with { Sha256 = new string('0', 64) }
            };

            await Assert.ThrowsAsync<CryptographicException>(() =>
                ApprovedAndroidNativeCryptoAssetStager.StageAsync(
                    root,
                    [Input("first", "runtimes/android-arm64/native/first.so", first), invalidSecond]));

            Assert.Equal(new byte[] { 0xa1 }, await File.ReadAllBytesAsync(firstPath));
            Assert.Equal(new byte[] { 0xb2 }, await File.ReadAllBytesAsync(secondPath));
            Assert.Empty(Directory.GetFiles(directory, "*.staging-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OversizedSourceAndEscapingDestinationFailClosed()
    {
        var root = CreateRoot();
        try
        {
            var bytes = new byte[] { 1, 2 };
            var underspecified = Input("asset", "native/asset.so", bytes) with
            {
                Asset = Input("asset", "native/asset.so", bytes).Asset with { Bytes = 1 }
            };
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ApprovedAndroidNativeCryptoAssetStager.StageAsync(root, [underspecified]));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ApprovedAndroidNativeCryptoAssetStager.StageAsync(
                    root,
                    [Input("asset", "../escape.so", bytes)]));
            Assert.False(File.Exists(Path.GetFullPath(Path.Combine(root, "..", "escape.so"))));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationRemovesTemporaryFilesAndPreservesDestination()
    {
        var root = CreateRoot();
        try
        {
            var destination = Path.Combine(root, "native", "asset.so");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllBytesAsync(destination, [0x44]);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ApprovedAndroidNativeCryptoAssetStager.StageAsync(
                    root,
                    [Input("asset", "native/asset.so", [1, 2, 3])],
                    cancellation.Token));

            Assert.Equal(new byte[] { 0x44 }, await File.ReadAllBytesAsync(destination));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, "*.staging-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ApprovedAndroidNativeCryptoAssetInput Input(
        string packagePath,
        string relativePath,
        byte[] bytes) => new(
            new ApprovedAndroidNativeCryptoAsset(
                packagePath,
                relativePath,
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()),
            new MemoryStream(bytes, writable: false));

    private static string CreateRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "deep-android-native-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
