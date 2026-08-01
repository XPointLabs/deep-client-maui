namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class IncomingCallPollingContractSmokeTests
{
    [Fact]
    public void MobileAndDesktopUseSharedBackoffAndPageCancellation()
    {
        var mobile = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "Pages", "ConversationsPage.xaml.cs");
        var desktop = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "Pages", "DesktopWorkspacePage.xaml.cs");
        var mobilePoll = mobile[mobile.IndexOf(
            "private async Task CheckIncomingCallsAsync",
            StringComparison.Ordinal)..];
        var desktopPoll = desktop[desktop.IndexOf(
            "private async Task CheckIncomingCallsAsync",
            StringComparison.Ordinal)..desktop.IndexOf(
            "private void RequestSynchronization",
            StringComparison.Ordinal)];

        Assert.Contains("IncomingCallPollingBackoff", mobile, StringComparison.Ordinal);
        Assert.Contains("IncomingCallPollingBackoff", desktop, StringComparison.Ordinal);
        Assert.Contains(
            "ReceiveIncomingOffersAsync(cancellationToken)",
            mobile,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReceiveIncomingOffersAsync(activity.Token)",
            desktop,
            StringComparison.Ordinal);
        Assert.Contains("incomingCallTimer.Interval = failure.NextDelay", mobile,
            StringComparison.Ordinal);
        Assert.Contains("incomingCallTimer.Interval = failure.NextDelay", desktop,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "LogException(\"ConversationsPage.IncomingCalls\", exception)",
            mobile,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "LogException(\"DesktopWorkspacePage.IncomingCalls\", exception)",
            desktop,
            StringComparison.Ordinal);
        Assert.Contains("IncomingCalls.Unexpected", mobile, StringComparison.Ordinal);
        Assert.Contains("IncomingCalls.Unexpected", desktop, StringComparison.Ordinal);
        Assert.True(Count(mobilePoll, "ThrowIfCancellationRequested()") >= 3);
        Assert.True(Count(desktopPoll, "ThrowIfCancellationRequested()") >= 3);
        Assert.Contains("ReferenceEquals(incomingCallPolling, polling)", mobilePoll,
            StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(incomingCallPolling, polling)", desktopPoll,
            StringComparison.Ordinal);
        Assert.DoesNotContain("catch (Exception) when", mobilePoll,
            StringComparison.Ordinal);
        Assert.DoesNotContain("catch (Exception) when", desktopPoll,
            StringComparison.Ordinal);

        var mobileSetup = mobile[mobile.IndexOf(
            "private Task ConfigureIncomingCallPollingAsync",
            StringComparison.Ordinal)..mobile.IndexOf(
            "private void ScheduleForegroundCatchUpSync",
            StringComparison.Ordinal)];
        Assert.Contains("IncomingCallPollingBackoff.IsCurrentActivity", mobileSetup,
            StringComparison.Ordinal);
        Assert.Contains("pageActivityCancellation", mobileSetup,
            StringComparison.Ordinal);
        Assert.True(
            mobile.IndexOf(
                "() => ConfigureIncomingCallPollingAsync(cancellationToken)",
                StringComparison.Ordinal) >= 0);
    }

    private static int Count(string value, string marker) =>
        value.Split(marker, StringSplitOptions.None).Length - 1;

    private static string ReadWorkspaceFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "Deep.Client.Maui.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine([directory!.FullName, .. parts]));
    }
}
