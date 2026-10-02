namespace Deep.Client.Maui.Clean.Tests;

public sealed class Did2ProbeVisualShellTests
{
    [Fact]
    public void ProbePreservesAccountAndRecoveryAutomationWithoutLegacyNavigation()
    {
        var shell = ReadSource("AppShell.Did2.cs");
        var project = ReadSource("Deep.Client.Maui.csproj");

        foreach (var id in new[]
        {
            "Page.Welcome", "Welcome.DisplayName", "Welcome.CreateAccount",
            "Page.Settings", "Settings.Identity", "Settings.RevealPhrase",
            "Settings.RecoveryPhrase", "Settings.CopyPhrase",
            "Settings.HidePhrase", "Settings.DeletePhrase",
            "Did2Probe.TransportUnavailable"
        })
            Assert.Contains($"\"{id}\"", shell, StringComparison.Ordinal);

        Assert.Contains("<MauiXaml Remove=\"App.xaml;AppShell.xaml;Pages\\**\\*.xaml;Styles\\**\\*.xaml\" />",
            project, StringComparison.Ordinal);
        Assert.DoesNotContain("SessionId", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("ChatPage", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("GroupChatPage", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateContactsView", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateGroupsView", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void PhraseIsExplicitlyHiddenAndUnavailableActionsAreNotPresented()
    {
        var shell = ReadSource("AppShell.Did2.cs");
        var theme = ReadSource(Path.Combine("CleanUi", "DeepTheme.cs"));

        Assert.Contains("protected override void OnDisappearing()", shell,
            StringComparison.Ordinal);
        Assert.Contains("hideSensitive?.Invoke();", shell,
            StringComparison.Ordinal);
        Assert.Contains("account.HideRecoveryPhrase();", shell,
            StringComparison.Ordinal);
        Assert.Contains("phrase.Text = string.Empty;", shell,
            StringComparison.Ordinal);
        Assert.Contains("IsSpellCheckEnabled = false", shell,
            StringComparison.Ordinal);
        Assert.Contains("IsTextPredictionEnabled = false", shell,
            StringComparison.Ordinal);
        Assert.Contains("Контакты, сообщения, вложения и группы недоступны", shell,
            StringComparison.Ordinal);
        Assert.Contains("Label.FontAutoScalingEnabledProperty, Value = true", theme,
            StringComparison.Ordinal);
        Assert.Contains("Button.FontAutoScalingEnabledProperty, Value = true", theme,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CleanWorkspaceConnectsOwnedTextCommandsWithoutLegacyOrUnwiredAttachments()
    {
        var shell = ReadSource("AppShell.Did2.cs");
        var messages = ReadSource("AppShell.Did2.Messaging.cs");

        foreach (var id in new[]
        {
            "Did2Workspace.Root", "Did2Workspace.Sidebar",
            "Did2Workspace.Chats", "Did2Workspace.Contacts",
            "Did2Workspace.Groups", "Did2Workspace.Settings",
            "Did2Workspace.MobileChats", "Did2Workspace.MobileContacts",
            "Did2Workspace.MobileGroups", "Did2Workspace.MobileSettings",
            "Did2Workspace.ConversationList", "Did2Workspace.EmptyConversation",
            "Page.Conversations", "Page.Contacts", "Page.Groups"
        })
            Assert.Contains($"\"{id}\"", shell + messages, StringComparison.Ordinal);

        Assert.Contains("section = destination;", shell, StringComparison.Ordinal);
        Assert.Contains("Переписка станет доступна после подключения проверенного DID2-транспорта.",
            shell, StringComparison.Ordinal);
        Assert.Contains("Command = messaging.SendTextCommand", messages);
        Assert.Contains("start.Command = messaging.StartContactCommand", messages);
        Assert.Contains("accept.Command = messaging.AcceptContactCommand", messages);
        Assert.Contains("if (!messaging.HasRuntime) return CreateEmptyChatsSection();", messages);
        Assert.DoesNotContain("Attachment.Send", messages);
        Assert.DoesNotContain("GroupChatPage", messages);
    }

    private static string ReadSource(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Deep.Client.Maui", name);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException($"MAUI source {name} is unavailable.");
    }
}
