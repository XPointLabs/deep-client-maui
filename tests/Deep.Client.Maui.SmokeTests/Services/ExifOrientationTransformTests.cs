using Deep.Client.Maui.Services;

namespace Deep.Client.Maui.SmokeTests.Services;

public sealed class ExifOrientationTransformTests
{
    [Theory]
    [InlineData(1, 0, false, false)]
    [InlineData(2, 0, true, false)]
    [InlineData(3, 180, false, false)]
    [InlineData(4, 180, true, false)]
    [InlineData(5, 90, true, true)]
    [InlineData(6, 90, false, true)]
    [InlineData(7, -90, true, true)]
    [InlineData(8, -90, false, true)]
    public void MapsEveryExifOrientation(
        int orientation,
        int rotationDegrees,
        bool mirrorsHorizontally,
        bool swapsDimensions)
    {
        var transform = ExifOrientationTransform.FromExifValue(orientation);

        Assert.Equal(rotationDegrees, transform.RotationDegrees);
        Assert.Equal(mirrorsHorizontally, transform.MirrorsHorizontally);
        Assert.Equal(swapsDimensions, transform.SwapsDimensions);
        Assert.Equal(orientation == 1, transform.IsIdentity);
    }

    [Theory]
    [InlineData(6, 90)]
    [InlineData(8, -90)]
    public void OrientationsSixAndEightRotateInOppositeDirections(int orientation, int rotationDegrees)
    {
        var transform = ExifOrientationTransform.FromExifValue(orientation);

        Assert.Equal(rotationDegrees, transform.RotationDegrees);
        Assert.False(transform.MirrorsHorizontally);
        Assert.True(transform.SwapsDimensions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void RejectsInvalidExifOrientation(int orientation)
    {
        Assert.Throws<InvalidOperationException>(() => ExifOrientationTransform.FromExifValue(orientation));
    }
}
