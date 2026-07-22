using Deep.Client.Maui.StrictGates;

namespace Deep.Client.Maui.Windows.Tests;

public sealed class WindowsInteractiveSessionProbeTests
{
    [Theory]
    [InlineData(0, 10, 0, false)]
    [InlineData(1, 10, 0, true)]
    [InlineData(-1, 10, 0, false)]
    [InlineData(0, 6, 1, true)]
    [InlineData(1, 6, 1, false)]
    public void SessionFlagClassification_FailsClosedAndHandlesDocumentedWindows7Reversal(
        int sessionFlags,
        int major,
        int minor,
        bool expected)
    {
        Assert.Equal(
            expected,
            WindowsInteractiveSessionProbe.IsUnlockedSessionFlag(
                sessionFlags,
                new Version(major, minor)));
    }
}
