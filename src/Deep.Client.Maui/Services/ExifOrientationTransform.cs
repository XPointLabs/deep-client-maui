namespace Deep.Client.Maui.Services;

internal readonly record struct ExifOrientationTransform(
    int RotationDegrees,
    bool MirrorsHorizontally)
{
    internal bool IsIdentity => RotationDegrees == 0 && !MirrorsHorizontally;

    internal bool SwapsDimensions => RotationDegrees is 90 or -90;

    internal static ExifOrientationTransform FromExifValue(int orientation) =>
        orientation switch
        {
            1 => new ExifOrientationTransform(0, false),
            2 => new ExifOrientationTransform(0, true),
            3 => new ExifOrientationTransform(180, false),
            4 => new ExifOrientationTransform(180, true),
            5 => new ExifOrientationTransform(90, true),
            6 => new ExifOrientationTransform(90, false),
            7 => new ExifOrientationTransform(-90, true),
            8 => new ExifOrientationTransform(-90, false),
            _ => throw new InvalidOperationException("Image has invalid orientation metadata.")
        };
}
