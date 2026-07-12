#if ANDROID
using System.Globalization;
using AndroidBitmap = Android.Graphics.Bitmap;
using AndroidBitmapFactory = Android.Graphics.BitmapFactory;
using AndroidColor = Android.Graphics.Color;
using AndroidXExifInterface = AndroidX.ExifInterface.Media.ExifInterface;
using Deep.Client.Maui.Services;

namespace Deep.Client.Maui.DeviceTests;

public sealed class AndroidExifOrientationDeviceTests
{
    private const int SourceWidth = 96;
    private const int SourceHeight = 64;

    [Theory]
    [InlineData(2, "BADC")]
    [InlineData(3, "DCBA")]
    [InlineData(4, "CDAB")]
    [InlineData(5, "ACBD")]
    [InlineData(6, "CADB")]
    [InlineData(7, "DBCA")]
    [InlineData(8, "BDAC")]
    [Trait("Category", "AndroidDevice")]
    public void TranscodePhysicallyAppliesExifAndRemovesApp1Metadata(
        int orientation,
        string expectedQuadrants)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"deep-exif-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var sourcePath = Path.Combine(directory, "source.jpg");
            WriteSourceJpeg(sourcePath, orientation);

            var result = AndroidImageTranscoder.TranscodeToMetadataFreeJpeg(
                sourcePath,
                directory,
                maxDimension: 1024,
                initialJpegQuality: 100,
                minimumJpegQuality: 100,
                qualityStep: 1,
                maxBytes: 1_000_000);

            var swapsDimensions = orientation is >= 5 and <= 8;
            Assert.Equal(swapsDimensions ? SourceHeight : SourceWidth, result.Width);
            Assert.Equal(swapsDimensions ? SourceWidth : SourceHeight, result.Height);
            Assert.Equal(expectedQuadrants, ReadQuadrants(result.OutputPath));

            using var outputExif = new AndroidXExifInterface(result.OutputPath);
            Assert.Null(outputExif.GetAttribute(AndroidXExifInterface.TagOrientation));
            Assert.False(ContainsJpegMarker(File.ReadAllBytes(result.OutputPath), 0xe1));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void WriteSourceJpeg(string path, int orientation)
    {
        var config = AndroidBitmap.Config.Argb8888
            ?? throw new InvalidOperationException("Android ARGB bitmap config is unavailable.");
        using var bitmap = AndroidBitmap.CreateBitmap(
            SourceWidth,
            SourceHeight,
            config)
            ?? throw new InvalidOperationException("Android bitmap allocation failed.");

        for (var y = 0; y < SourceHeight; y++)
        {
            for (var x = 0; x < SourceWidth; x++)
            {
                bitmap.SetPixel(x, y, SourceColor(x, y));
            }
        }

        using (var output = File.Create(path))
        {
            var jpeg = AndroidBitmap.CompressFormat.Jpeg
                ?? throw new InvalidOperationException("Android JPEG encoder is unavailable.");
            Assert.True(bitmap.Compress(jpeg, 100, output));
        }

        using var exif = new AndroidXExifInterface(path);
        exif.SetAttribute(
            AndroidXExifInterface.TagOrientation,
            orientation.ToString(CultureInfo.InvariantCulture));
        exif.SaveAttributes();
        Assert.Equal(orientation, AndroidExifOrientationNormalizer.ReadOrientation(path, "image/jpeg"));
    }

    private static AndroidColor SourceColor(int x, int y)
    {
        if (y < SourceHeight / 2)
        {
            return x < SourceWidth / 2
                ? AndroidColor.Rgb(255, 0, 0)
                : AndroidColor.Rgb(0, 255, 0);
        }

        return x < SourceWidth / 2
            ? AndroidColor.Rgb(0, 0, 255)
            : AndroidColor.Rgb(255, 255, 0);
    }

    private static string ReadQuadrants(string path)
    {
        using var bitmap = AndroidBitmapFactory.DecodeFile(path)
            ?? throw new InvalidOperationException("Transcoded image could not be decoded.");
        return string.Concat(
            Classify(bitmap.GetPixel(bitmap.Width / 4, bitmap.Height / 4)),
            Classify(bitmap.GetPixel(bitmap.Width * 3 / 4, bitmap.Height / 4)),
            Classify(bitmap.GetPixel(bitmap.Width / 4, bitmap.Height * 3 / 4)),
            Classify(bitmap.GetPixel(bitmap.Width * 3 / 4, bitmap.Height * 3 / 4)));
    }

    private static char Classify(int color)
    {
        var candidates = new[]
        {
            (Name: 'A', Color: (int)AndroidColor.Rgb(255, 0, 0)),
            (Name: 'B', Color: (int)AndroidColor.Rgb(0, 255, 0)),
            (Name: 'C', Color: (int)AndroidColor.Rgb(0, 0, 255)),
            (Name: 'D', Color: (int)AndroidColor.Rgb(255, 255, 0))
        };

        return candidates.MinBy(candidate => ColorDistance(color, candidate.Color)).Name;
    }

    private static long ColorDistance(int left, int right)
    {
        var red = AndroidColor.GetRedComponent(left) - AndroidColor.GetRedComponent(right);
        var green = AndroidColor.GetGreenComponent(left) - AndroidColor.GetGreenComponent(right);
        var blue = AndroidColor.GetBlueComponent(left) - AndroidColor.GetBlueComponent(right);
        return (long)red * red + (long)green * green + (long)blue * blue;
    }

    private static bool ContainsJpegMarker(byte[] jpeg, byte marker)
    {
        for (var index = 0; index + 1 < jpeg.Length; index++)
        {
            if (jpeg[index] == 0xff && jpeg[index + 1] == marker)
            {
                return true;
            }
        }

        return false;
    }
}
#endif
