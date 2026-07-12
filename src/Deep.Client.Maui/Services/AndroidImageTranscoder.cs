#if ANDROID
using AndroidBitmap = Android.Graphics.Bitmap;
using AndroidBitmapFactory = Android.Graphics.BitmapFactory;

namespace Deep.Client.Maui.Services;

internal static class AndroidImageTranscoder
{
    internal static AndroidImageTranscodeResult TranscodeToMetadataFreeJpeg(
        string sourcePath,
        string outputDirectory,
        int maxDimension,
        int initialJpegQuality,
        int minimumJpegQuality,
        int qualityStep,
        long maxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source image was not found.", sourcePath);
        }

        if (maxDimension <= 0
            || initialJpegQuality is < 1 or > 100
            || minimumJpegQuality is < 1 or > 100
            || minimumJpegQuality > initialJpegQuality
            || qualityStep <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDimension), "Image transcode settings are invalid.");
        }

        using var bounds = new AndroidBitmapFactory.Options { InJustDecodeBounds = true };
        AndroidBitmapFactory.DecodeFile(sourcePath, bounds);
        if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0)
        {
            throw new InvalidOperationException("Selected image could not be decoded.");
        }

        var orientation = AndroidExifOrientationNormalizer.ReadOrientation(sourcePath, bounds.OutMimeType);
        using var options = new AndroidBitmapFactory.Options
        {
            InSampleSize = CalculateSampleSize(bounds.OutWidth, bounds.OutHeight, maxDimension),
            InPreferredConfig = AndroidBitmap.Config.Argb8888
        };
        AndroidBitmap? decoded = AndroidBitmapFactory.DecodeFile(sourcePath, options)
            ?? throw new InvalidOperationException("Selected image could not be decoded.");

        AndroidBitmap? oriented = null;
        AndroidBitmap? resized = null;
        try
        {
            var resizePlan = CalculateResizePlan(
                decoded.Width,
                decoded.Height,
                maxDimension,
                orientation);
            AndroidBitmap working = decoded;
            if (resizePlan.RequiresResize)
            {
                resized = AndroidBitmap.CreateScaledBitmap(
                    decoded,
                    resizePlan.PreOrientationWidth,
                    resizePlan.PreOrientationHeight,
                    true)
                    ?? throw new InvalidOperationException("Selected image could not be resized.");
                decoded.Dispose();
                decoded = null;
                working = resized;
            }

            oriented = AndroidExifOrientationNormalizer.ApplyIfNeeded(working, orientation);
            if (oriented is not null)
            {
                working.Dispose();
                decoded = null;
                resized = null;
                working = oriented;
            }

            return EncodeMetadataFreeJpeg(
                working,
                outputDirectory,
                initialJpegQuality,
                minimumJpegQuality,
                qualityStep,
                maxBytes);
        }
        finally
        {
            oriented?.Dispose();
            resized?.Dispose();
            decoded?.Dispose();
        }
    }

    private static AndroidImageTranscodeResult EncodeMetadataFreeJpeg(
        AndroidBitmap bitmap,
        string outputDirectory,
        int initialJpegQuality,
        int minimumJpegQuality,
        int qualityStep,
        long maxBytes)
    {
        Directory.CreateDirectory(outputDirectory);
        var jpegFormat = AndroidBitmap.CompressFormat.Jpeg
            ?? throw new InvalidOperationException("Android JPEG encoder is not available.");

        for (var quality = initialJpegQuality; quality >= minimumJpegQuality; quality -= qualityStep)
        {
            var outputPath = Path.Combine(outputDirectory, $"media-{Guid.NewGuid():N}.jpg");
            try
            {
                using (var output = new FileStream(
                    outputPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    FileOptions.SequentialScan))
                {
                    if (!bitmap.Compress(jpegFormat, quality, output))
                    {
                        throw new InvalidOperationException("Selected image could not be compressed.");
                    }

                    output.Flush(flushToDisk: true);
                }

                var size = new FileInfo(outputPath).Length;
                if (maxBytes <= 0 || size <= maxBytes)
                {
                    return new AndroidImageTranscodeResult(outputPath, size, bitmap.Width, bitmap.Height);
                }
            }
            catch
            {
                File.Delete(outputPath);
                throw;
            }

            File.Delete(outputPath);
        }

        throw new InvalidOperationException($"Compressed image exceeds limit {maxBytes} bytes.");
    }

    internal static int CalculateSampleSize(int width, int height, int maxDimension)
    {
        ValidateDimensions(width, height, maxDimension);

        var requiredScale = Math.Max(width, height) / (long)maxDimension;
        var sampleSize = 1;
        while (sampleSize <= requiredScale / 2)
        {
            sampleSize *= 2;
        }

        return sampleSize;
    }

    internal static AndroidImageResizePlan CalculateResizePlan(
        int decodedWidth,
        int decodedHeight,
        int maxDimension,
        int orientation)
    {
        ValidateDimensions(decodedWidth, decodedHeight, maxDimension);

        var transform = ExifOrientationTransform.FromExifValue(orientation);
        var orientedWidth = transform.SwapsDimensions ? decodedHeight : decodedWidth;
        var orientedHeight = transform.SwapsDimensions ? decodedWidth : decodedHeight;
        var longestSide = Math.Max(orientedWidth, orientedHeight);
        if (longestSide <= maxDimension)
        {
            return new AndroidImageResizePlan(
                decodedWidth,
                decodedHeight,
                orientedWidth,
                orientedHeight,
                RequiresResize: false);
        }

        var outputWidth = RoundScaledDimension(orientedWidth, maxDimension, longestSide);
        var outputHeight = RoundScaledDimension(orientedHeight, maxDimension, longestSide);
        return new AndroidImageResizePlan(
            transform.SwapsDimensions ? outputHeight : outputWidth,
            transform.SwapsDimensions ? outputWidth : outputHeight,
            outputWidth,
            outputHeight,
            RequiresResize: true);
    }

    private static int RoundScaledDimension(int dimension, int maxDimension, int longestSide)
    {
        var numerator = (long)dimension * maxDimension;
        var result = numerator / longestSide;
        var remainder = numerator % longestSide;
        var doubledRemainder = remainder * 2;
        if (doubledRemainder > longestSide
            || (doubledRemainder == longestSide && result % 2 != 0))
        {
            result++;
        }

        return Math.Max(1, (int)result);
    }

    private static void ValidateDimensions(int width, int height, int maxDimension)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (maxDimension <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDimension));
        }
    }
}

internal readonly record struct AndroidImageResizePlan(
    int PreOrientationWidth,
    int PreOrientationHeight,
    int OutputWidth,
    int OutputHeight,
    bool RequiresResize);

internal readonly record struct AndroidImageTranscodeResult(
    string OutputPath,
    long Size,
    int Width,
    int Height);
#endif
