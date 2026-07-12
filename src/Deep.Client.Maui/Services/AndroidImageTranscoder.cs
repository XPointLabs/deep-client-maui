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
        using var decoded = AndroidBitmapFactory.DecodeFile(sourcePath, options)
            ?? throw new InvalidOperationException("Selected image could not be decoded.");

        AndroidBitmap? oriented = null;
        AndroidBitmap? scaled = null;
        try
        {
            oriented = AndroidExifOrientationNormalizer.ApplyIfNeeded(decoded, orientation);
            var working = oriented ?? decoded;
            var longestSide = Math.Max(working.Width, working.Height);
            if (longestSide > maxDimension)
            {
                var scale = maxDimension / (double)longestSide;
                var width = Math.Max(1, (int)Math.Round(working.Width * scale));
                var height = Math.Max(1, (int)Math.Round(working.Height * scale));
                scaled = AndroidBitmap.CreateScaledBitmap(working, width, height, true)
                    ?? throw new InvalidOperationException("Selected image could not be resized.");
                working = scaled;
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
            scaled?.Dispose();
            oriented?.Dispose();
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

    private static int CalculateSampleSize(int width, int height, int maxDimension)
    {
        var sampleSize = 1L;
        var longestSide = Math.Max(width, height);
        while (longestSide / (sampleSize * 2) >= maxDimension && sampleSize <= int.MaxValue / 2)
        {
            sampleSize *= 2;
        }

        return (int)sampleSize;
    }
}

internal readonly record struct AndroidImageTranscodeResult(
    string OutputPath,
    long Size,
    int Width,
    int Height);
#endif
