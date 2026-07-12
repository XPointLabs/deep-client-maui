namespace Deep.Client.Maui.Windows.Tests;

public sealed class WindowsPlatformContractTests
{
    [Fact]
    public void AppLockUsesHwndConsentInteropBehindTheDocumentedOsGate()
    {
        var source = ReadWorkspaceFile(
            "src",
            "Deep.Client.Maui",
            "Platforms",
            "Windows",
            "WindowsAppLockService.cs");

        Assert.Contains("OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)", source, StringComparison.Ordinal);
        Assert.Contains("WinRT.Interop.WindowNative.GetWindowHandle(window)", source, StringComparison.Ordinal);
        Assert.Contains("UserConsentVerifierInterop", source, StringComparison.Ordinal);
        Assert.Contains(".RequestVerificationForWindowAsync(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RealityReadinessUsesRandomPortsAndRequiresTheXrayOwnerPid()
    {
        var source = ReadWorkspaceFile(
            "src",
            "Deep.Client.Maui",
            "Platforms",
            "Windows",
            "WindowsRealityTransport.cs");

        Assert.Contains("RandomNumberGenerator.GetInt32", source, StringComparison.Ordinal);
        Assert.Contains("TcpTableClass.OwnerPidListener", source, StringComparison.Ordinal);
        Assert.Contains("row.LocalAddress == LoopbackAddress", source, StringComparison.Ordinal);
        Assert.Contains("processId == process!.Id", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectAsync(IPAddress.Loopback, localPort", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NotificationSuccessFlagsAreDerivedFromCompletedRegistrations()
    {
        var source = ReadWorkspaceFile(
            "src",
            "Deep.Client.Maui",
            "Platforms",
            "Windows",
            "WindowsPushNotificationService.cs");

        Assert.Contains("pushInitialized = manager is not null", source, StringComparison.Ordinal);
        Assert.Contains("appNotificationsInitialized = appNotificationManager is not null", source, StringComparison.Ordinal);
        Assert.Contains("manager.PushReceived -= OnPushReceived", source, StringComparison.Ordinal);
        Assert.Contains("candidate.NotificationInvoked -= OnNotificationInvoked", source, StringComparison.Ordinal);
    }

    private static string ReadWorkspaceFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deep.Client.Maui.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine([directory!.FullName, .. parts]));
    }
}
