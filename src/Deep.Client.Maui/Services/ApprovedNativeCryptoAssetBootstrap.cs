namespace Deep.Client.Maui.Services;

internal static class ApprovedNativeCryptoAssetBootstrap
{
    internal static Task StageAsync(CancellationToken cancellationToken)
    {
#if ANDROID
        return AndroidApprovedNativeCryptoAssetSource.StageAsync(cancellationToken);
#else
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
#endif
    }
}
