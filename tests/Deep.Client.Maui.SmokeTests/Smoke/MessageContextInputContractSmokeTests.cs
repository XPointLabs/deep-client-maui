namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class MessageContextInputContractSmokeTests
{
    [Fact]
    public void MessageTemplatesUseNativeContextInputAndInteractiveReactions()
    {
        var behavior = ReadWorkspaceFile(
            "src",
            "Deep.Client.Maui",
            "Controls",
            "MessageContextGestureBehavior.cs");
        var chat = ReadWorkspaceFile("src", "Deep.Client.Maui", "Pages", "ChatPage.xaml");
        var group = ReadWorkspaceFile("src", "Deep.Client.Maui", "Pages", "GroupChatPage.xaml");

        Assert.Contains("view.LongClick += OnAndroidLongClick", behavior, StringComparison.Ordinal);
        Assert.Contains("FeedbackConstants.LongPress", behavior, StringComparison.Ordinal);
        Assert.Contains("e.Handled = true", behavior, StringComparison.Ordinal);
        Assert.Contains("view.ContextRequested += OnWindowsContextRequested", behavior, StringComparison.Ordinal);
        Assert.Contains("args.Handled = true", behavior, StringComparison.Ordinal);

        Assert.Equal(7, Count(chat, "MessageContextGestureBehavior"));
        Assert.Equal(7, Count(group, "MessageContextGestureBehavior"));
        Assert.Equal(4, Count(chat, "Tapped=\"OnReactionChipTapped\""));
        Assert.Equal(4, Count(group, "Tapped=\"OnReactionChipTapped\""));
        Assert.DoesNotContain("OnMessagePointer", chat, StringComparison.Ordinal);
        Assert.DoesNotContain("OnMessagePointer", group, StringComparison.Ordinal);
    }

    private static int Count(string value, string marker) =>
        value.Split(marker, StringSplitOptions.None).Length - 1;

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
