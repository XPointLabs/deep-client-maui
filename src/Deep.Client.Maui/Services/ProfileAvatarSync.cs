using System.Buffers.Binary;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;
using Microsoft.Maui.Storage;

#if ANDROID
using AndroidBitmap = Android.Graphics.Bitmap;
using AndroidBitmapFactory = Android.Graphics.BitmapFactory;
#endif

namespace Deep.Client.Maui.Services;

internal static class ProfileAvatarSync
{
    internal const int MaximumInputBytes = 20_000_000;
    internal const int MaximumInputDimension = 8_192;
    internal const long MaximumInputPixels = 25_000_000;
    internal const int MaximumAvatarDimension = 1_024;
    internal const int MaximumAvatarBytes = 1_500_000;
    private const string AvatarContentType = "image/jpeg";
    private static readonly int[] JpegQualities = [88, 80, 72, 64];

    public static async Task SaveAndPublishAsync(
        FileResult photo,
        string avatarPath,
        ClientRuntime runtime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentException.ThrowIfNullOrWhiteSpace(avatarPath);
        ArgumentNullException.ThrowIfNull(runtime);

        byte[]? original = null;
        byte[]? normalizedContent = null;
        try
        {
            original = await ReadSelectedImageAsync(photo, cancellationToken).ConfigureAwait(false);
            var normalized = await NormalizeToJpegAsync(original, cancellationToken).ConfigureAwait(false);
            normalizedContent = normalized.Content;
            ValidateNormalizedJpeg(normalizedContent, normalized.Width, normalized.Height);

            var fullAvatarPath = Path.GetFullPath(avatarPath);
            await WriteAtomicallyAsync(fullAvatarPath, normalizedContent, cancellationToken).ConfigureAwait(false);
            await PublishAsync(fullAvatarPath, runtime, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (original is not null)
            {
                Array.Clear(original);
            }

            if (normalizedContent is not null)
            {
                Array.Clear(normalizedContent);
            }
        }
    }

    internal static void ValidateNormalizedJpeg(ReadOnlySpan<byte> content, int expectedWidth, int expectedHeight)
    {
        if (content.IsEmpty || content.Length > MaximumAvatarBytes)
        {
            throw new InvalidOperationException("Normalized avatar exceeds its byte limit.");
        }

        var dimensions = InspectMetadataFreeJpeg(content);
        if (dimensions.Width != expectedWidth
            || dimensions.Height != expectedHeight
            || dimensions.Width <= 0
            || dimensions.Height <= 0
            || dimensions.Width > MaximumAvatarDimension
            || dimensions.Height > MaximumAvatarDimension)
        {
            throw new InvalidOperationException("Normalized avatar dimensions do not match its decoded pixels.");
        }
    }

    private static async Task<byte[]> ReadSelectedImageAsync(
        FileResult photo,
        CancellationToken cancellationToken)
    {
        await using var source = await photo.OpenReadAsync().ConfigureAwait(false);
        if (source.CanSeek && source.Length > MaximumInputBytes)
        {
            throw new InvalidOperationException($"Selected image exceeds the {MaximumInputBytes} byte limit.");
        }

        using var destination = new MemoryStream();
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    var content = destination.ToArray();
                    if (content.Length == 0)
                    {
                        throw new InvalidOperationException("Selected image is empty.");
                    }

                    return content;
                }

                if (destination.Length + read > MaximumInputBytes)
                {
                    throw new InvalidOperationException($"Selected image exceeds the {MaximumInputBytes} byte limit.");
                }

                destination.Write(buffer, 0, read);
            }
        }
        finally
        {
            Array.Clear(buffer);
        }
    }

    private static async Task WriteAtomicallyAsync(
        string avatarPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(avatarPath)
            ?? throw new InvalidOperationException("Avatar path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(avatarPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var destination = new FileStream(
                temporaryPath,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous | FileOptions.WriteThrough
                }))
            {
                await destination.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, avatarPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task PublishAsync(
        string avatarPath,
        ClientRuntime runtime,
        CancellationToken cancellationToken)
    {
        if (!runtime.AvatarProfiles.IsEnabled)
        {
            return;
        }

        var account = await runtime.Accounts.GetActiveAccountAsync(cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return;
        }

        var recoveryPhrase = await runtime.Accounts.GetRecoveryPhraseAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The active account recovery phrase is unavailable.");
        using var identity = new SessionIdentityProvider(recoveryPhrase);
        if (identity.SessionId != account.SessionId)
        {
            throw new InvalidOperationException("The active account identity does not match its recovery phrase.");
        }

        await using var upload = File.OpenRead(avatarPath);
        await runtime.AvatarProfiles
            .UploadAsync(identity, upload, AvatarContentType, cancellationToken)
            .ConfigureAwait(false);
    }

    private static Task<NormalizedAvatar> NormalizeToJpegAsync(
        byte[] content,
        CancellationToken cancellationToken)
    {
#if ANDROID
        return Task.Run(() => NormalizeAndroid(content), cancellationToken);
#elif IOS || MACCATALYST
        return Task.Run(() => NormalizeApple(content), cancellationToken);
#elif WINDOWS
        return NormalizeWindowsAsync(content, cancellationToken);
#else
        throw new PlatformNotSupportedException("Avatar image normalization is unavailable on this platform.");
#endif
    }

#if ANDROID
    private static NormalizedAvatar NormalizeAndroid(byte[] content)
    {
        var bounds = new AndroidBitmapFactory.Options { InJustDecodeBounds = true };
        AndroidBitmapFactory.DecodeByteArray(content, 0, content.Length, bounds);
        if (bounds.OutMimeType is not ("image/jpeg" or "image/png" or "image/webp"))
        {
            throw new InvalidOperationException("Selected file is not a supported JPEG, PNG, or WebP image.");
        }

        var orientation = ReadAndroidOrientation(content);
        var dimensions = CreateDimensions(bounds.OutWidth, bounds.OutHeight, orientation);
        ValidateInputDimensions(dimensions);

        var options = new AndroidBitmapFactory.Options
        {
            InSampleSize = CalculateSampleSize(dimensions.Width, dimensions.Height),
            InPreferredConfig = AndroidBitmap.Config.Argb8888
        };
        using var decoded = AndroidBitmapFactory.DecodeByteArray(content, 0, content.Length, options)
            ?? throw new InvalidOperationException("Selected image could not be decoded.");

        AndroidBitmap? oriented = null;
        AndroidBitmap? resized = null;
        try
        {
            var working = decoded;
            if (orientation != 1)
            {
                oriented = ApplyAndroidOrientation(decoded, orientation);
                working = oriented;
            }

            var target = CalculateTargetDimensions(dimensions);
            if (working.Width != target.Width || working.Height != target.Height)
            {
                resized = AndroidBitmap.CreateScaledBitmap(working, target.Width, target.Height, true)
                    ?? throw new InvalidOperationException("Selected image could not be resized.");
                working = resized;
            }

            ValidateDecodedDimensions(dimensions, working.Width, working.Height);
            return new NormalizedAvatar(EncodeAndroidJpeg(working), working.Width, working.Height);
        }
        finally
        {
            resized?.Dispose();
            oriented?.Dispose();
        }
    }

    private static int ReadAndroidOrientation(byte[] content)
    {
        using var input = new MemoryStream(content, writable: false);
        using var exif = new AndroidX.ExifInterface.Media.ExifInterface(input);
        var orientation = exif.GetAttributeInt(
            AndroidX.ExifInterface.Media.ExifInterface.TagOrientation,
            AndroidX.ExifInterface.Media.ExifInterface.OrientationNormal);
        if (orientation == AndroidX.ExifInterface.Media.ExifInterface.OrientationUndefined)
        {
            return AndroidX.ExifInterface.Media.ExifInterface.OrientationNormal;
        }

        if (orientation is < 1 or > 8)
        {
            throw new InvalidOperationException("Selected image has invalid orientation metadata.");
        }

        return orientation;
    }

    private static AndroidBitmap ApplyAndroidOrientation(AndroidBitmap source, int orientation)
    {
        using var matrix = new Android.Graphics.Matrix();
        switch (orientation)
        {
            case 2:
                matrix.PostScale(-1, 1);
                break;
            case 3:
                matrix.PostRotate(180);
                break;
            case 4:
                matrix.PostRotate(180);
                matrix.PostScale(-1, 1);
                break;
            case 5:
                matrix.PostRotate(90);
                matrix.PostScale(-1, 1);
                break;
            case 6:
                matrix.PostRotate(90);
                break;
            case 7:
                matrix.PostRotate(-90);
                matrix.PostScale(-1, 1);
                break;
            case 8:
                matrix.PostRotate(-90);
                break;
            default:
                throw new InvalidOperationException("Selected image has invalid orientation metadata.");
        }

        return AndroidBitmap.CreateBitmap(source, 0, 0, source.Width, source.Height, matrix, true)
            ?? throw new InvalidOperationException("Selected image orientation could not be applied.");
    }

    private static byte[] EncodeAndroidJpeg(AndroidBitmap bitmap)
    {
        var jpeg = AndroidBitmap.CompressFormat.Jpeg
            ?? throw new InvalidOperationException("Android JPEG encoder is unavailable.");
        foreach (var quality in JpegQualities)
        {
            using var output = new MemoryStream();
            if (!bitmap.Compress(jpeg, quality, output))
            {
                throw new InvalidOperationException("Selected image could not be encoded.");
            }

            if (output.Length <= MaximumAvatarBytes)
            {
                return output.ToArray();
            }
        }

        throw new InvalidOperationException("Normalized avatar exceeds its byte limit.");
    }

    private static int CalculateSampleSize(int width, int height)
    {
        var sampleSize = 1;
        while (Math.Max(width, height) / (sampleSize * 2) >= MaximumAvatarDimension)
        {
            sampleSize *= 2;
        }

        return sampleSize;
    }
#endif

#if IOS || MACCATALYST
    private static NormalizedAvatar NormalizeApple(byte[] content)
    {
        using var data = Foundation.NSData.FromArray(content);
        using var source = ImageIO.CGImageSource.FromData(
            data,
            new ImageIO.CGImageOptions { ShouldCache = false, ShouldCacheImmediately = false })
            ?? throw new InvalidOperationException("Selected image could not be decoded.");
        if (source.ImageCount != 1
            || source.TypeIdentifier is not { } typeIdentifier
            || typeIdentifier != UniformTypeIdentifiers.UTTypes.Jpeg.Identifier
                && typeIdentifier != UniformTypeIdentifiers.UTTypes.Png.Identifier
                && typeIdentifier != UniformTypeIdentifiers.UTTypes.WebP.Identifier)
        {
            throw new InvalidOperationException("Selected file is not a supported static JPEG, PNG, or WebP image.");
        }

        var properties = source.GetProperties(0);
        var width = properties.PixelWidth
            ?? throw new InvalidOperationException("Selected image width is unavailable.");
        var height = properties.PixelHeight
            ?? throw new InvalidOperationException("Selected image height is unavailable.");
        var orientation = properties.Orientation is null ? 1 : (int)properties.Orientation.Value;
        if (orientation is < 1 or > 8)
        {
            throw new InvalidOperationException("Selected image has invalid orientation metadata.");
        }

        var dimensions = CreateDimensions(width, height, orientation);
        ValidateInputDimensions(dimensions);
        var options = new ImageIO.CGImageThumbnailOptions
        {
            CreateThumbnailFromImageAlways = true,
            CreateThumbnailWithTransform = true,
            MaxPixelSize = MaximumAvatarDimension,
            ShouldCache = false,
            ShouldCacheImmediately = true
        };
        using var thumbnail = source.CreateThumbnail(0, options)
            ?? throw new InvalidOperationException("Selected image could not be decoded.");
        var decodedWidth = checked((int)thumbnail.Width);
        var decodedHeight = checked((int)thumbnail.Height);
        ValidateDecodedDimensions(dimensions, decodedWidth, decodedHeight);

        using var image = UIKit.UIImage.FromImage(thumbnail);
        foreach (var quality in JpegQualities)
        {
            using var encoded = image.AsJPEG((CoreGraphics.NFloat)(quality / 100d))
                ?? throw new InvalidOperationException("Selected image could not be encoded.");
            if (encoded.Length <= MaximumAvatarBytes)
            {
                return new NormalizedAvatar(encoded.ToArray(), decodedWidth, decodedHeight);
            }
        }

        throw new InvalidOperationException("Normalized avatar exceeds its byte limit.");
    }
#endif

#if WINDOWS
    private static async Task<NormalizedAvatar> NormalizeWindowsAsync(
        byte[] content,
        CancellationToken cancellationToken)
    {
        using var inputStream = new MemoryStream(content, writable: false);
        using var randomAccessInput = System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(inputStream);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(randomAccessInput);
        cancellationToken.ThrowIfCancellationRequested();

        var codecId = decoder.DecoderInformation.CodecId;
        if (codecId != Windows.Graphics.Imaging.BitmapDecoder.JpegDecoderId
            && codecId != Windows.Graphics.Imaging.BitmapDecoder.PngDecoderId
            && codecId != Windows.Graphics.Imaging.BitmapDecoder.WebpDecoderId)
        {
            throw new InvalidOperationException("Selected file is not a supported JPEG, PNG, or WebP image.");
        }

        var dimensions = new ImageDimensions(
            checked((int)decoder.PixelWidth),
            checked((int)decoder.PixelHeight),
            checked((int)decoder.OrientedPixelWidth),
            checked((int)decoder.OrientedPixelHeight));
        ValidateInputDimensions(dimensions);
        var scale = CalculateScale(dimensions);
        var transform = new Windows.Graphics.Imaging.BitmapTransform
        {
            ScaledWidth = checked((uint)Math.Max(1, (int)Math.Round(dimensions.Width * scale))),
            ScaledHeight = checked((uint)Math.Max(1, (int)Math.Round(dimensions.Height * scale))),
            InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            transform,
            Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
            Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
        cancellationToken.ThrowIfCancellationRequested();

        ValidateDecodedDimensions(dimensions, bitmap.PixelWidth, bitmap.PixelHeight);
        var encoded = await EncodeWindowsJpegAsync(bitmap, cancellationToken).ConfigureAwait(false);
        return new NormalizedAvatar(encoded, bitmap.PixelWidth, bitmap.PixelHeight);
    }

    private static async Task<byte[]> EncodeWindowsJpegAsync(
        Windows.Graphics.Imaging.SoftwareBitmap bitmap,
        CancellationToken cancellationToken)
    {
        foreach (var quality in JpegQualities)
        {
            using var output = new MemoryStream();
            using var randomAccessOutput = System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(output);
            var encoderOptions = new Windows.Graphics.Imaging.BitmapPropertySet
            {
                ["ImageQuality"] = new Windows.Graphics.Imaging.BitmapTypedValue(
                    quality / 100f,
                    Windows.Foundation.PropertyType.Single)
            };
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId,
                randomAccessOutput,
                encoderOptions);
            encoder.IsThumbnailGenerated = false;
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
            cancellationToken.ThrowIfCancellationRequested();

            var encoded = output.ToArray();
            if (encoded.Length <= MaximumAvatarBytes)
            {
                return encoded;
            }
        }

        throw new InvalidOperationException("Normalized avatar exceeds its byte limit.");
    }
#endif

    private static ImageDimensions CreateDimensions(int width, int height, int orientation) =>
        orientation is >= 5 and <= 8
            ? new ImageDimensions(width, height, height, width)
            : new ImageDimensions(width, height, width, height);

    private static void ValidateInputDimensions(ImageDimensions dimensions)
    {
        if (dimensions.Width <= 0
            || dimensions.Height <= 0
            || dimensions.Width > MaximumInputDimension
            || dimensions.Height > MaximumInputDimension
            || (long)dimensions.Width * dimensions.Height > MaximumInputPixels
            || dimensions.OrientedWidth <= 0
            || dimensions.OrientedHeight <= 0
            || (long)dimensions.Width * dimensions.Height
                != (long)dimensions.OrientedWidth * dimensions.OrientedHeight)
        {
            throw new InvalidOperationException("Selected image dimensions exceed the avatar limits.");
        }
    }

    private static void ValidateDecodedDimensions(ImageDimensions input, int actualWidth, int actualHeight)
    {
        var expected = CalculateTargetDimensions(input);
        if (actualWidth <= 0
            || actualHeight <= 0
            || actualWidth > MaximumAvatarDimension
            || actualHeight > MaximumAvatarDimension
            || Math.Abs(actualWidth - expected.Width) > 1
            || Math.Abs(actualHeight - expected.Height) > 1)
        {
            throw new InvalidOperationException("Selected image orientation or dimensions were not decoded correctly.");
        }
    }

    private static (int Width, int Height) CalculateTargetDimensions(ImageDimensions input)
    {
        var scale = CalculateScale(input);
        return (
            Math.Max(1, (int)Math.Round(input.OrientedWidth * scale)),
            Math.Max(1, (int)Math.Round(input.OrientedHeight * scale)));
    }

    private static double CalculateScale(ImageDimensions input) =>
        Math.Min(1d, MaximumAvatarDimension / (double)Math.Max(input.OrientedWidth, input.OrientedHeight));

    private static (int Width, int Height) InspectMetadataFreeJpeg(ReadOnlySpan<byte> content)
    {
        if (content.Length < 8
            || content[0] != 0xFF
            || content[1] != 0xD8
            || content[^2] != 0xFF
            || content[^1] != 0xD9)
        {
            throw new InvalidOperationException("Normalized avatar is not a valid JPEG image.");
        }

        var offset = 2;
        var width = 0;
        var height = 0;
        var foundScan = false;
        var foundEnd = false;
        var insideScan = false;
        while (offset < content.Length)
        {
            byte marker;
            if (insideScan)
            {
                while (offset < content.Length && content[offset] != 0xFF)
                {
                    offset++;
                }

                if (offset >= content.Length)
                {
                    break;
                }

                offset++;
                while (offset < content.Length && content[offset] == 0xFF)
                {
                    offset++;
                }

                if (offset >= content.Length)
                {
                    break;
                }

                marker = content[offset++];
                if (marker == 0x00 || marker is >= 0xD0 and <= 0xD7)
                {
                    continue;
                }

                insideScan = false;
            }
            else
            {
                if (content[offset++] != 0xFF)
                {
                    throw new InvalidOperationException("Normalized JPEG structure is invalid.");
                }

                while (offset < content.Length && content[offset] == 0xFF)
                {
                    offset++;
                }

                if (offset >= content.Length)
                {
                    break;
                }

                marker = content[offset++];
            }

            if (marker == 0xD9)
            {
                foundEnd = offset == content.Length;
                break;
            }

            if (marker is 0xD8 or 0x00)
            {
                throw new InvalidOperationException("Normalized JPEG structure is invalid.");
            }

            if (marker is >= 0xD0 and <= 0xD7 || marker == 0x01)
            {
                continue;
            }

            if (offset + 2 > content.Length)
            {
                throw new InvalidOperationException("Normalized JPEG structure is invalid.");
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(content.Slice(offset, 2));
            if (segmentLength < 2 || offset + segmentLength > content.Length)
            {
                throw new InvalidOperationException("Normalized JPEG structure is invalid.");
            }

            if (marker is 0xE1 or 0xED or 0xFE)
            {
                throw new InvalidOperationException("Normalized avatar contains EXIF, XMP, IPTC, or comment metadata.");
            }

            var payload = content.Slice(offset + 2, segmentLength - 2);
            if (IsStartOfFrame(marker))
            {
                if (payload.Length < 6)
                {
                    throw new InvalidOperationException("Normalized JPEG dimensions are invalid.");
                }

                var frameHeight = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(1, 2));
                var frameWidth = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(3, 2));
                if (width != 0 && (width != frameWidth || height != frameHeight))
                {
                    throw new InvalidOperationException("Normalized JPEG has conflicting dimensions.");
                }

                width = frameWidth;
                height = frameHeight;
            }

            offset += segmentLength;
            if (marker == 0xDA)
            {
                foundScan = true;
                insideScan = true;
            }
        }

        if (width <= 0 || height <= 0 || !foundScan || !foundEnd)
        {
            throw new InvalidOperationException("Normalized JPEG dimensions or scan data are missing.");
        }

        return (width, height);
    }

    private static bool IsStartOfFrame(byte marker) =>
        marker is >= 0xC0 and <= 0xCF && marker is not 0xC4 and not 0xC8 and not 0xCC;

    private readonly record struct ImageDimensions(
        int Width,
        int Height,
        int OrientedWidth,
        int OrientedHeight);

    private readonly record struct NormalizedAvatar(byte[] Content, int Width, int Height);
}
