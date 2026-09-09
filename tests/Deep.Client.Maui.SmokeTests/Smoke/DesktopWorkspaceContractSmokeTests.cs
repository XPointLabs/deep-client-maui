namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class DesktopWorkspaceContractSmokeTests
{
    [Fact]
    public void DesktopSurfaceDeclaresSplitDetailComposerAndContextContracts()
    {
        var xaml = ReadWorkspaceFile("src", "Deep.Client.Maui", "Pages", "DesktopWorkspacePage.xaml");
        var codeBehind = ReadWorkspaceFile("src", "Deep.Client.Maui", "Pages", "DesktopWorkspacePage.xaml.cs");
        var viewModel = ReadWorkspaceFile("src", "Deep.Client.Maui.Core", "ViewModels", "DesktopWorkspaceViewModel.cs");

        Assert.Contains("AutomationId=\"DesktopWorkspace.ListPane\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationId=\"DesktopWorkspace.DetailPane\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationId=\"DesktopWorkspace.DirectMessages\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationId=\"DesktopWorkspace.GroupMessages\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Scrolled=\"OnDirectMessagesScrolled\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Scrolled=\"OnGroupMessagesScrolled\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationId=\"DesktopWorkspace.DirectDraft\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationId=\"DesktopWorkspace.GroupDraft\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationId=\"DesktopWorkspace.DirectVoicePlay\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationId=\"DesktopWorkspace.GroupVoicePlay\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationId=\"DesktopWorkspace.DirectVoice\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationId=\"DesktopWorkspace.GroupVoice\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationId=\"DesktopWorkspace.GroupMembersPanel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationId=\"DesktopWorkspace.ProfileSettings\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Clicked=\"OnOpenSettingsClicked\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Border.GestureRecognizers><TapGestureRecognizer Tapped=\"OnOpenSettingsClicked\"", xaml, StringComparison.Ordinal);
        Assert.Equal(2, Count(xaml, "MessageContextGestureBehavior Invoked="));
        Assert.Equal(2, Count(xaml, "TapGestureRecognizer Buttons=\"Secondary\""));
        Assert.Equal(2, Count(xaml, "ReactionChipTapped"));
        Assert.Equal(2, Count(xaml, "PointerPressed=\"OnVoicePointerPressed\""));
        Assert.Contains("OnContextSaveAttachmentClicked", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationId=\"DesktopWorkspace.DirectAttachmentSave\"", xaml,
            StringComparison.Ordinal);
        Assert.Contains("AutomationId=\"DesktopWorkspace.GroupAttachmentSave\"", xaml,
            StringComparison.Ordinal);
        Assert.Equal(2, Count(xaml, "Clicked=\"OnMessageAttachmentSaveClicked\""));
        Assert.Contains("AttachmentOpenService.SaveAsync(attachment, attachmentFiles)", codeBehind,
            StringComparison.Ordinal);
        Assert.Contains("OnContextShareAttachmentClicked", xaml, StringComparison.Ordinal);
        Assert.Contains("ResolveContextMenuTranslation", codeBehind, StringComparison.Ordinal);
        Assert.Contains("VoiceMessagePlaybackService", codeBehind, StringComparison.Ordinal);
        Assert.Contains("StopVoiceRecordingAndSendAsync", codeBehind, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMinutes(15)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CheckIncomingCallsAsync", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ShellRouteCatalog.Settings", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ResetMessageSearchUi", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ShouldScrollDirectMessagesToEnd", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ShouldScrollGroupMessagesToEnd", codeBehind, StringComparison.Ordinal);
        Assert.Contains("LastVisibleItemIndex >= viewModel.DirectChat.Messages.Count - 2", codeBehind, StringComparison.Ordinal);
        Assert.Contains("LastVisibleItemIndex >= viewModel.GroupChat.Messages.Count - 2", codeBehind, StringComparison.Ordinal);
        Assert.Contains("OnGroupMembersClicked", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("Opacity=\"0.6\"", xaml, StringComparison.Ordinal);

        Assert.Contains("SplitBreakpoint = 680", viewModel, StringComparison.Ordinal);
        Assert.Contains("DefaultConversationListWidth = 320", viewModel, StringComparison.Ordinal);
        Assert.Contains("MinimumConversationListWidth = 280", viewModel, StringComparison.Ordinal);
        Assert.Contains("MaximumConversationListWidth = 420", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void MessagingRootsStayRegisteredButShellFailsClosedUntilMsg01Composition()
    {
        var shellXaml = ReadWorkspaceFile("src", "Deep.Client.Maui", "AppShell.xaml");
        var shellCode = ReadWorkspaceFile("src", "Deep.Client.Maui", "AppShell.xaml.cs");
        var mauiProgram = ReadWorkspaceFile("src", "Deep.Client.Maui", "MauiProgram.cs");

        Assert.Contains("ContentTemplate=\"{DataTemplate pages:NetworkUnavailablePage}\"", shellXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ContentTemplate=\"{DataTemplate pages:ConversationsPage}\"", shellXaml, StringComparison.Ordinal);
        Assert.Contains("TryGetAuthenticatedMessagingRuntime() => null", shellCode, StringComparison.Ordinal);
        Assert.DoesNotContain("services.GetRequiredService<DesktopWorkspacePage>()", shellCode, StringComparison.Ordinal);
        Assert.Contains("IConversationActivationTarget", shellCode, StringComparison.Ordinal);
        Assert.Contains("IActiveComposerProvider", shellCode, StringComparison.Ordinal);
        Assert.Contains("AddTransient<ConversationsPage>()", mauiProgram, StringComparison.Ordinal);
        Assert.Contains("new DesktopWorkspaceViewModel(", mauiProgram, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<DesktopWorkspacePage>()", mauiProgram, StringComparison.Ordinal);

        var newConversation = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "Pages", "NewConversationPage.xaml.cs");
        Assert.Contains("IVerifiedDirectConversationActivationTarget", newConversation,
            StringComparison.Ordinal);
        Assert.Contains("viewModel.VerifiedConversation", newConversation,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SessionId.Parse", newConversation, StringComparison.Ordinal);
        Assert.Contains(
            "internal Task<bool> ActivateConversationAsync(ConversationId conversationId)",
            shellCode,
            StringComparison.Ordinal);
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
