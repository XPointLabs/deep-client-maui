#if ANDROID
using AndroidBitmap = Android.Graphics.Bitmap;
using AndroidMatrix = Android.Graphics.Matrix;
using AndroidXExifInterface = AndroidX.ExifInterface.Media.ExifInterface;

namespace Deep.Client.Maui.Services;

internal static class AndroidExifOrientationNormalizer
{
    internal const int NormalOrientation = 1;

    internal static int ReadOrientation(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var input = new MemoryStream(content, writable: false);
        using var exif = new AndroidXExifInterface(input);
        return ReadOrientation(exif);
    }

    internal static int ReadOrientation(string sourcePath, string? decodedMimeType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        if (!SupportsExifOrientation(decodedMimeType))
        {
            return NormalOrientation;
        }

        using var input = File.OpenRead(sourcePath);
        using var exif = new AndroidXExifInterface(input);
        return ReadOrientation(exif);
    }

    // The source remains owned by the caller. A non-null result is always a new bitmap.
    internal static AndroidBitmap? ApplyIfNeeded(AndroidBitmap source, int orientation)
    {
        ArgumentNullException.ThrowIfNull(source);

        var transform = ExifOrientationTransform.FromExifValue(orientation);
        if (transform.IsIdentity)
        {
            return null;
        }

        using var matrix = new AndroidMatrix();
        if (transform.RotationDegrees != 0)
        {
            matrix.PostRotate(transform.RotationDegrees);
        }

        if (transform.MirrorsHorizontally)
        {
            matrix.PostScale(-1, 1);
        }

        return AndroidBitmap.CreateBitmap(source, 0, 0, source.Width, source.Height, matrix, true)
            ?? throw new InvalidOperationException("Image orientation could not be applied.");
    }

    private static int ReadOrientation(AndroidXExifInterface exif)
    {
        var orientation = exif.GetAttributeInt(
            AndroidXExifInterface.TagOrientation,
            AndroidXExifInterface.OrientationNormal);
        if (orientation == AndroidXExifInterface.OrientationUndefined)
        {
            return NormalOrientation;
        }

        _ = ExifOrientationTransform.FromExifValue(orientation);
        return orientation;
    }

    private static bool SupportsExifOrientation(string? mimeType) =>
        mimeType?.ToLowerInvariant() is
            "image/jpeg"
            or "image/png"
            or "image/webp"
            or "image/heif"
            or "image/heic"
            or "image/avif";
}
#endif
