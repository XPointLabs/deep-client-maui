#if ANDROID
using Android.Content.Res;
using Deep.Client.Maui.Services;

namespace Deep.Client.Maui;

internal static class AndroidApprovedNativeCryptoAssetSource
{
    internal static async Task StageAsync(CancellationToken cancellationToken)
    {
        var assetManager = Android.App.Application.Context.Assets
            ?? throw new InvalidOperationException("Android packaged assets are unavailable.");
        var assets = ApprovedAndroidNativeCryptoAssetStager.Assets;
        var streams = new List<Stream>(assets.Count);
        try
        {
            foreach (var asset in assets)
                streams.Add(assetManager.Open(asset.PackageAssetPath, Access.Streaming));
            var inputs = assets.Select((asset, index) =>
                new ApprovedAndroidNativeCryptoAssetInput(asset, streams[index])).ToArray();
            await ApprovedAndroidNativeCryptoAssetStager.StageAsync(
                    AppContext.BaseDirectory,
                    inputs,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            foreach (var stream in streams)
                stream.Dispose();
        }
    }
}
#endif
