namespace Deep.Client.Maui.Windows.Tests;

public sealed class DesktopWorkspacePlatformContractTests
{
    [Fact]
    public void DesktopMessagesUseTheNativeWindowsContextRequestPath()
    {
        var behavior = ReadWorkspaceFile(
            "src",
            "Deep.Client.Maui",
            "Controls",
            "MessageContextGestureBehavior.cs");
        var xaml = ReadWorkspaceFile(
            "src",
            "Deep.Client.Maui",
            "Pages",
            "DesktopWorkspacePage.xaml");
        var codeBehind = ReadWorkspaceFile(
            "src",
            "Deep.Client.Maui",
            "Pages",
            "DesktopWorkspacePage.xaml.cs");

        Assert.Contains("view.ContextRequested += OnWindowsContextRequested", behavior, StringComparison.Ordinal);
        Assert.Contains("view.RightTapped += OnWindowsRightTapped", behavior,
            StringComparison.Ordinal);
        Assert.Contains("args.Handled = true", behavior, StringComparison.Ordinal);
        Assert.Contains("OnDirectMessageContextRequested", xaml, StringComparison.Ordinal);
        Assert.Contains("OnGroupMessageContextRequested", xaml, StringComparison.Ordinal);
        Assert.Contains("TransformToVisual(detailElement)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("AutomationId=\"DesktopWorkspace.MessageContextMenu\"", xaml, StringComparison.Ordinal);
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
