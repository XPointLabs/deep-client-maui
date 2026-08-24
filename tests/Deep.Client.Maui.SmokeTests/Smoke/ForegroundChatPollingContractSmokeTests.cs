namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class ForegroundChatPollingContractSmokeTests
{
    [Fact]
    public void OpenChatKeepsBoundedPollingEvenWhenPushClaimsAvailability()
    {
        var page = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "Pages", "ChatPage.xaml.cs");
        var configure = page[page.IndexOf(
            "private async Task ConfigureAutoReceiveAsync",
            StringComparison.Ordinal)..page.IndexOf(
            "private async void OnAutoReceiveTick",
            StringComparison.Ordinal)];

        Assert.Contains("IsPushDrivenSyncAvailableAsync", configure, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(30)", configure, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(10)", configure, StringComparison.Ordinal);
        Assert.Contains("EnsureAutoReceive(pushDriven", configure, StringComparison.Ordinal);
        Assert.DoesNotContain("autoReceiveTimer?.Stop()", configure, StringComparison.Ordinal);
        Assert.Contains("autoReceiveTimer.Interval = interval", configure, StringComparison.Ordinal);
        Assert.Contains("Foreground chat synchronization failed", page, StringComparison.Ordinal);
    }

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
