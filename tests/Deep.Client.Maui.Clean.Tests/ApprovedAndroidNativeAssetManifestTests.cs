using Deep.Client.Maui.Services;

namespace Deep.Client.Maui.Clean.Tests;

public sealed class ApprovedAndroidNativeAssetManifestTests
{
    [Fact]
    public void Did2MlDsaCandidateHasExactAndroidAssetPin()
    {
        var assets = ApprovedAndroidNativeCryptoAssetStager.Assets;
        Assert.Equal(3, assets.Count);
        Assert.Equal(assets.Count, assets.Select(static asset =>
            asset.PackageAssetPath).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(assets.Count, assets.Select(static asset =>
            asset.RelativeDestinationPath).Distinct(StringComparer.Ordinal).Count());

        var mldsa = Assert.Single(assets, static asset =>
            asset.PackageAssetPath == "deep-native/libdeep_mldsa.so");
        Assert.Equal("runtimes/android-arm64/native/libdeep_mldsa.so",
            mldsa.RelativeDestinationPath);
        Assert.Equal(79_112, mldsa.Bytes);
        Assert.Equal(
            "6e46e4df970f5376416af3c50fb39e580487a616d2541aa26d1d90eddf918cc9",
            mldsa.Sha256);
    }
}
