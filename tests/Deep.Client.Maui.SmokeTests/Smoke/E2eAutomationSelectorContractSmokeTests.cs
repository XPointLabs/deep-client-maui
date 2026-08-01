using System.Xml.Linq;

namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class E2eAutomationSelectorContractSmokeTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2009/xaml";

    [Fact]
    public void EveryMessageTemplateExposesTheCompleteFixedSelectorContract()
    {
        AssertMessageTemplates(
            LoadPage("ChatPage.xaml"),
            "Chat",
            ["ChatPlainTextMessageTemplate", "ChatTextMessageTemplate", "ChatImageMessageTemplate", "ChatVoiceMessageTemplate", "ChatAttachmentMessageTemplate"]);
        AssertMessageTemplates(
            LoadPage("GroupChatPage.xaml"),
            "GroupChat",
            ["GroupChatPlainTextMessageTemplate", "GroupChatTextMessageTemplate", "GroupChatImageMessageTemplate", "GroupChatVoiceMessageTemplate", "GroupChatAttachmentMessageTemplate"]);
    }

    [Fact]
    public void AppOwnedAttachmentActionsExposeFixedOpenAndSaveSelectors()
    {
        var chat = LoadPage("ChatPage.xaml");
        var group = LoadPage("GroupChatPage.xaml");
        var desktop = LoadPage("DesktopWorkspacePage.xaml");

        AssertTappedAction(chat, "OnOpenAttachmentClicked", "Chat.AttachmentOpen");
        AssertTappedAction(chat, "OnSaveAttachmentClicked", "Chat.AttachmentSave");
        AssertTappedAction(chat, "OnPickFileClicked", "Chat.PickFile");
        AssertTappedAction(chat, "OnOpenSelectedMessageAttachment", "Chat.MessageAttachmentOpen");
        AssertTappedAction(chat, "OnSaveSelectedMessageAttachment", "Chat.MessageAttachmentSave");
        AssertTappedAction(group, "OnOpenAttachmentClicked", "GroupChat.AttachmentOpen");
        AssertTappedAction(group, "OnSaveAttachmentClicked", "GroupChat.AttachmentSave");
        AssertTappedAction(group, "OnOpenSelectedMessageAttachment", "GroupChat.MessageAttachmentOpen");
        AssertTappedAction(group, "OnSaveSelectedMessageAttachment", "GroupChat.MessageAttachmentSave");
        AssertClickedAction(desktop, "OnContextOpenAttachmentClicked", "DesktopWorkspace.AttachmentOpen");
        AssertClickedAction(desktop, "OnContextSaveAttachmentClicked", "DesktopWorkspace.AttachmentSave");
    }

    [Fact]
    public void ManualResendSelectorsAreCommandBackedOnDesktop()
    {
        var desktop = LoadPage("DesktopWorkspacePage.xaml");

        AssertDesktopRetry(
            desktop,
            "DesktopWorkspace.DirectRetry",
            "BindingContext.DirectChat.RetryMessageCommand");
        AssertDesktopRetry(
            desktop,
            "DesktopWorkspace.GroupRetry",
            "BindingContext.GroupChat.RetryMessageCommand");
    }

    [Fact]
    public void E2eAutomationIdsAreLiteralPrivacySafeRolesAndPageSelectorsAreUnique()
    {
        var pages = new[]
        {
            LoadPage("ConversationsPage.xaml"),
            LoadPage("ChatPage.xaml"),
            LoadPage("GroupChatPage.xaml"),
            LoadPage("DesktopWorkspacePage.xaml")
        };

        foreach (var automationId in pages
            .SelectMany(page => page.Root!.DescendantsAndSelf())
                     .Attributes("AutomationId"))
        {
            Assert.DoesNotContain("{", automationId.Value, StringComparison.Ordinal);
            Assert.DoesNotContain("Binding", automationId.Value, StringComparison.OrdinalIgnoreCase);
            Assert.Matches("^[A-Za-z][A-Za-z0-9.]*$", automationId.Value);
        }

        AssertUniqueSelector(pages, "Conversations.ProfileSettings");
        AssertUniqueSelector(pages, "Conversations.Refresh");
        AssertUniqueSelector(pages, "Chat.StagedAttachmentRow");
        AssertUniqueSelector(pages, "Chat.RemoveStagedAttachment");
        AssertUniqueSelector(pages, "GroupChat.StagedAttachmentRow");
        AssertUniqueSelector(pages, "GroupChat.RemoveStagedAttachment");
        AssertUniqueSelector(pages, "DesktopWorkspace.ProfileSettings");
        AssertUniqueSelector(pages, "DesktopWorkspace.ConversationList");

        AssertSelectorExists(pages, "Conversations.ConversationRow");
        AssertSelectorExists(pages, "DesktopWorkspace.ConversationRow");
        AssertSelectorExists(pages, "Chat.StagedAttachmentFilename");
        AssertSelectorExists(pages, "GroupChat.StagedAttachmentFilename");
        AssertSelectorExists(pages, "DesktopWorkspace.DirectMessageBubble");
        AssertSelectorExists(pages, "DesktopWorkspace.DirectMessageBody");
        AssertSelectorExists(pages, "DesktopWorkspace.DirectDeliveryStatus");
        AssertSelectorExists(pages, "DesktopWorkspace.DirectRetry");
        AssertSelectorExists(pages, "DesktopWorkspace.GroupMessageBubble");
        AssertSelectorExists(pages, "DesktopWorkspace.GroupMessageBody");
        AssertSelectorExists(pages, "DesktopWorkspace.GroupDeliveryStatus");
        AssertSelectorExists(pages, "DesktopWorkspace.GroupRetry");
    }

    private static void AssertMessageTemplates(
        XDocument page,
        string selectorPrefix,
        IReadOnlyCollection<string> expectedKeys)
    {
        var templates = page
            .Descendants()
            .Where(element => element.Name.LocalName == "DataTemplate")
            .Where(element => element.Attribute(XamlNamespace + "Key")?.Value.EndsWith("MessageTemplate", StringComparison.Ordinal) == true)
            .ToDictionary(element => element.Attribute(XamlNamespace + "Key")!.Value, StringComparer.Ordinal);

        Assert.Equal(
            expectedKeys.OrderBy(key => key, StringComparer.Ordinal),
            templates.Keys.OrderBy(key => key, StringComparer.Ordinal));

        foreach (var (key, template) in templates)
        {
            var templateGrid = Assert.Single(template.Elements(), element => element.Name.LocalName == "Grid");
            var bubble = Assert.Single(templateGrid.Elements(), element => element.Name.LocalName == "Border");
            Assert.Equal($"{selectorPrefix}.MessageBubble", bubble.Attribute("AutomationId")?.Value);
            Assert.Equal("Входящее сообщение", bubble.Attribute("SemanticProperties.Description")?.Value);

            Assert.Single(ElementsWithAutomationId(bubble, $"{selectorPrefix}.MessageBody"));
            Assert.Single(ElementsWithAutomationId(bubble, $"{selectorPrefix}.DeliveryStatus"));
            var retry = Assert.Single(ElementsWithAutomationId(bubble, $"{selectorPrefix}.Retry"));
            Assert.Equal("{Binding IsRetryAvailable}", retry.Attribute("IsVisible")?.Value);
            Assert.Equal("{Binding IsRetryAvailable}", retry.Attribute("IsEnabled")?.Value);
            Assert.Equal("{Binding .}", retry.Attribute("CommandParameter")?.Value);
            Assert.Contains("RetryMessageCommand", retry.Attribute("Command")?.Value, StringComparison.Ordinal);

            var outgoingTrigger = Assert.Single(
                bubble.Descendants(),
                element =>
                    element.Name.LocalName == "DataTrigger" &&
                    element.Attribute("Binding")?.Value.Contains("Direction", StringComparison.Ordinal) == true &&
                    element.Attribute("Value")?.Value.Contains("Outgoing", StringComparison.Ordinal) == true);
            var semanticSetter = Assert.Single(
                outgoingTrigger.Descendants(),
                element =>
                    element.Name.LocalName == "Setter" &&
                    element.Attribute("Property")?.Value == "SemanticProperties.Description");
            Assert.Equal("Исходящее сообщение", semanticSetter.Attribute("Value")?.Value);

            if (key.Contains("Image", StringComparison.Ordinal) ||
                key.Contains("Voice", StringComparison.Ordinal) ||
                key.Contains("Attachment", StringComparison.Ordinal))
            {
                Assert.Single(ElementsWithAutomationId(bubble, $"{selectorPrefix}.AttachmentCard"));
            }

            if (key.Contains("Attachment", StringComparison.Ordinal))
            {
                Assert.Single(ElementsWithAutomationId(bubble, $"{selectorPrefix}.AttachmentFilename"));
            }
        }
    }

    private static void AssertTappedAction(XDocument page, string handler, string expectedAutomationId)
    {
        var gesture = Assert.Single(
            page.Descendants(),
            element =>
                element.Name.LocalName == "TapGestureRecognizer" &&
                element.Attribute("Tapped")?.Value == handler);
        var action = gesture.Ancestors().FirstOrDefault(element => element.Attribute("AutomationId") is not null);
        Assert.NotNull(action);
        Assert.Equal(expectedAutomationId, action.Attribute("AutomationId")?.Value);
    }

    private static void AssertDesktopRetry(
        XDocument page,
        string automationId,
        string commandPath)
    {
        var retry = Assert.Single(ElementsWithAutomationId(page.Root!, automationId));
        Assert.Equal("{Binding IsRetryAvailable}", retry.Attribute("IsVisible")?.Value);
        Assert.Equal("{Binding IsRetryAvailable}", retry.Attribute("IsEnabled")?.Value);
        Assert.Equal("{Binding .}", retry.Attribute("CommandParameter")?.Value);
        Assert.Contains(commandPath, retry.Attribute("Command")?.Value, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(
            retry.Attribute("SemanticProperties.Description")?.Value));
    }

    private static void AssertClickedAction(XDocument page, string handler, string expectedAutomationId)
    {
        var action = Assert.Single(page.Descendants(), element => element.Attribute("Clicked")?.Value == handler);
        Assert.Equal(expectedAutomationId, action.Attribute("AutomationId")?.Value);
    }

    private static IEnumerable<XElement> ElementsWithAutomationId(XElement root, string automationId) =>
        root.DescendantsAndSelf().Where(element => element.Attribute("AutomationId")?.Value == automationId);

    private static void AssertUniqueSelector(IEnumerable<XDocument> pages, string automationId) =>
        Assert.Single(
            pages.SelectMany(page => page.Root!.DescendantsAndSelf()),
            element => element.Attribute("AutomationId")?.Value == automationId);

    private static void AssertSelectorExists(IEnumerable<XDocument> pages, string automationId) =>
        Assert.Contains(
            pages.SelectMany(page => page.Root!.DescendantsAndSelf()),
            element => element.Attribute("AutomationId")?.Value == automationId);

    private static XDocument LoadPage(string fileName) =>
        XDocument.Load(WorkspacePath("src", "Deep.Client.Maui", "Pages", fileName), LoadOptions.SetLineInfo);

    private static string WorkspacePath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deep.Client.Maui.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory!.FullName, .. parts]);
    }
}
