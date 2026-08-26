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
    public void RepeatedConversationRowsExposeTheExactBoundTitleAsAccessibleName()
    {
        foreach (var (page, selector) in new[]
                 {
                     (LoadPage("ConversationsPage.xaml"),
                         "Conversations.ConversationRow"),
                     (LoadPage("DesktopWorkspacePage.xaml"),
                         "DesktopWorkspace.ConversationRow")
                 })
        {
            var row = Assert.Single(ElementsWithAutomationId(page.Root!, selector));
            Assert.Equal("{Binding Title}",
                row.Attribute("SemanticProperties.Description")?.Value);
        }
    }

    [Fact]
    public void SettingsExposeTheCanonicalSessionIdOnOneScrollableLine()
    {
        var settings = LoadPage("SettingsPage.xaml");
        var label = Assert.Single(ElementsWithAutomationId(
            settings.Root!, "Settings.SessionId"));
        Assert.Equal("Label", label.Name.LocalName);
        var scroller = Assert.IsType<XElement>(label.Parent);
        Assert.Equal("ScrollView", scroller.Name.LocalName);
        Assert.Equal("Horizontal", scroller.Attribute("Orientation")?.Value);
        Assert.Equal("{Binding SessionId}",
            label.Attribute("SemanticProperties.Description")?.Value);
        Assert.Equal("SessionIdLabel", label.Attributes().Single(attribute =>
            attribute.Name.LocalName == "Name").Value);

        Assert.Equal("NoWrap", label.Attribute("LineBreakMode")?.Value);
        Assert.Equal("1", label.Attribute("MaxLines")?.Value);
        Assert.Equal("620", label.Attribute("WidthRequest")?.Value);
        Assert.Equal("Start", label.Attribute("HorizontalOptions")?.Value);
        var source = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "Pages", "SettingsPage.xaml.cs"));
        Assert.Contains("return value;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("string.Join(Environment.NewLine", source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicalRouteMarkersAreDebugPhysicalOnlyAndPublishExactRouteProof()
    {
        var source = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "Pages", "DesktopWorkspacePage.xaml.cs"));
        var tracker = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "Services", "PhysicalMailboxRouteUsageTracker.cs"));
        var diagnostics = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "Services", "PrivacyMailboxRouteDiagnostics.cs"));

        Assert.Contains("#if DEBUG && DEEP_PHYSICAL_E2E", source,
            StringComparison.Ordinal);
        Assert.Contains("AutomationId = \"PhysicalE2E.RouteNodeMarker\"", source,
            StringComparison.Ordinal);
        Assert.Contains("AutomationId = \"PhysicalE2E.RouteProofMarker\"", source,
            StringComparison.Ordinal);
        Assert.Contains("ManagedIngressH2Contract.FramePath", source,
            StringComparison.Ordinal);
        Assert.Contains("string.Join(',', routes.Primary.Select", source,
            StringComparison.Ordinal);
        Assert.Contains("string.Join(',', routes.Fallback.Select", source,
            StringComparison.Ordinal);
        Assert.Contains("|selected={selectedRoute.Route}|entry={routerId}", source,
            StringComparison.Ordinal);
        Assert.Contains("physicalRouteDiagnostics.CurrentSelection", source,
            StringComparison.Ordinal);
        Assert.Contains("physicalRouteNodeMarker.Text = routerId ?? string.Empty;", source,
            StringComparison.Ordinal);
        Assert.Contains("physicalRouteNodeMarker.IsVisible = viewModel.IsDirectDetail", source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PhysicalE2E.RouteNodeMarker\" Text=", source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ITransportRouteProvider", source, StringComparison.Ordinal);
        Assert.Contains("physicalRouteUsageTracker.Changed += OnPhysicalRouteUsageChanged;",
            source, StringComparison.Ordinal);
        Assert.Contains("physicalRouteUsageTracker.Changed -= OnPhysicalRouteUsageChanged;",
            source, StringComparison.Ordinal);
        Assert.Contains("if (!isPageActive", source, StringComparison.Ordinal);
        Assert.Contains("active.AttemptId == usage.AttemptId", tracker,
            StringComparison.Ordinal);
        Assert.Contains("Convert.ToHexStringLower(usage.EntryRouterId.Span)", tracker,
            StringComparison.Ordinal);
        Assert.Contains("IPrivacyMailboxRouteSelectionObserver", diagnostics,
            StringComparison.Ordinal);
        Assert.Contains("diagnostics.ObserveSelection(selection, entryRouterId)", diagnostics,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicalRuntimeReadyMarkerRequiresSuccessfulAuthenticatedSync()
    {
        var conversations = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "Pages", "ConversationsPage.xaml.cs"));
        var desktop = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "Pages", "DesktopWorkspacePage.xaml.cs"));
        var physical = File.ReadAllText(WorkspacePath(
            "tests", "Deep.Client.Maui.UiTests", "StrictCrossPlatformUiTests.cs"));
        var runner = File.ReadAllText(WorkspacePath(
            "eng", "Invoke-PhysicalMau2CrossPlatform.ps1"));

        Assert.Contains("AutomationId = \"PhysicalE2E.RuntimeReadyMarker\"", conversations,
            StringComparison.Ordinal);
        Assert.Contains("SetPhysicalRuntimeReady(true);", conversations, StringComparison.Ordinal);
        Assert.Contains("SetPhysicalRuntimeReady(false);", conversations, StringComparison.Ordinal);
        Assert.Contains("AutomationId = \"PhysicalE2E.RuntimeReadyMarker\"", desktop,
            StringComparison.Ordinal);
        Assert.Contains("WorkspaceRoot.Children.Add(physicalRuntimeReadyMarker)", desktop,
            StringComparison.Ordinal);
        Assert.DoesNotContain("DetailContent.Children.Add(physicalRuntimeReadyMarker)", desktop,
            StringComparison.Ordinal);
        Assert.Contains("Text = \"pending\"", desktop, StringComparison.Ordinal);
        Assert.Contains("physicalRuntimeReadyMarker.Text = ready ? \"ready\" : $\"failed:{failureCode}\"", desktop,
            StringComparison.Ordinal);
        Assert.Contains("SetPhysicalRuntimeReady(true);", desktop, StringComparison.Ordinal);
        Assert.Contains("SetPhysicalRuntimeReady(false, viewModel.ConversationList.SyncFailureCode);", desktop,
            StringComparison.Ordinal);
        Assert.Contains("SetPhysicalRuntimeReady(false, \"page\");", desktop, StringComparison.Ordinal);
        Assert.Contains("options.App(\"PhysicalE2E.RuntimeReadyMarker\")", physical,
            StringComparison.Ordinal);
        Assert.Contains("windows.WaitForAutomationIdWithName(\n            \"PhysicalE2E.RuntimeReadyMarker\",\n            \"ready\"",
            physical, StringComparison.Ordinal);
        Assert.Contains("'PhysicalE2E.RuntimeReadyMarker'", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicalAttachmentPickerUsesExactFilenameInsteadOfAmbiguousItemRoot()
    {
        var physical = File.ReadAllText(WorkspacePath(
            "tests", "Deep.Client.Maui.UiTests", "StrictCrossPlatformUiTests.cs"));
        var runner = File.ReadAllText(WorkspacePath(
            "eng", "Invoke-PhysicalMau2CrossPlatform.ps1"));

        Assert.Contains(
            "android.TapExactResourceIdWithExactText(options.PickerFileResourceId, marker);",
            physical,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PickerDownloadsResourceId", physical, StringComparison.Ordinal);
        Assert.DoesNotContain("AndroidPickerDownloadsId", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("com.google.android.documentsui:id/item_root", runner,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicalWindowsAttachmentActionsUseExactCorrelatedMessageControls()
    {
        var physical = File.ReadAllText(WorkspacePath(
            "tests", "Deep.Client.Maui.UiTests", "StrictCrossPlatformUiTests.cs"));
        var windows = File.ReadAllText(WorkspacePath(
            "tests", "Deep.Client.Maui.UiTests", "WindowsUiSmokeTests.cs"));

        Assert.Contains(
            "\"DesktopWorkspace.DirectAttachmentSave\"",
            physical,
            StringComparison.Ordinal);
        Assert.Contains("windows.ActivateExact(attachment);", physical, StringComparison.Ordinal);
        Assert.Contains("windows.ActivateExact(preview);", physical, StringComparison.Ordinal);
        Assert.Contains(
            "WaitForAutomationIdWithName(\"DesktopWorkspace.DirectAttachmentFilename\", marker",
            physical,
            StringComparison.Ordinal);
        Assert.Contains("WaitForCorrelatedDescendant(", physical, StringComparison.Ordinal);
        Assert.Contains("Thread.Sleep(TimeSpan.FromSeconds(3));", physical,
            StringComparison.Ordinal);
        Assert.Contains("windows.FocusWindow();", physical, StringComparison.Ordinal);
        Assert.Contains("current = current.Parent;", windows, StringComparison.Ordinal);
        Assert.Contains("SnapshotForegroundWindowHandle()", windows, StringComparison.Ordinal);
        Assert.Contains("ChooseSingleFileFromForegroundPicker(", windows,
            StringComparison.Ordinal);
        Assert.Contains("GetForegroundWindow()", windows, StringComparison.Ordinal);
        Assert.Contains("previousForegroundHandle", windows, StringComparison.Ordinal);
        Assert.Contains("AutomationId.ValueOrDefault == \"1\"", windows,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicalRestartDurabilityOpensTheConversationBeforeInspectingMessages()
    {
        var physical = File.ReadAllText(WorkspacePath(
            "tests", "Deep.Client.Maui.UiTests", "StrictCrossPlatformUiTests.cs"));
        const string openConversation =
            "android.Tap(options.App(\"Conversations.ConversationRow\"));";

        Assert.Equal(3, physical.Split(openConversation, StringSplitOptions.None).Length - 1);
        Assert.True(
            physical.IndexOf(openConversation, StringComparison.Ordinal) <
            physical.IndexOf(
                "android.WaitForText(options.App(\"Chat.MessageBody\"), marker, TimeSpan.FromSeconds(60));",
                StringComparison.Ordinal));
        Assert.Contains(
            "restartedWindows.WaitForAutomationIdWithDescendantNameContaining(",
            physical,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"DesktopWorkspace.ConversationRow\",\n                    marker[..32],",
            physical,
            StringComparison.Ordinal);

        var windows = File.ReadAllText(WorkspacePath(
            "tests", "Deep.Client.Maui.UiTests", "WindowsUiSmokeTests.cs"));
        Assert.Contains("app.Close(killIfCloseFails: true);", windows, StringComparison.Ordinal);
        Assert.Contains("process.WaitForExit(TimeSpan.FromSeconds(15))", windows, StringComparison.Ordinal);
        Assert.Contains(
            "The exact launched Windows process did not exit before session disposal.",
            windows,
            StringComparison.Ordinal);
        Assert.Contains(
            "FindExactBinaryProcessIds(expectedPath, DateTime.MinValue).Count != 0",
            windows,
            StringComparison.Ordinal);
        Assert.Contains(
            "process.StartTime.ToUniversalTime() >= launchedAfterUtc",
            windows,
            StringComparison.Ordinal);
        Assert.Contains("return Application.Attach(exactProcessId.Value);", windows,
            StringComparison.Ordinal);
        Assert.Contains(
            "Windows launch produced more than one new exact target process.",
            windows,
            StringComparison.Ordinal);
        Assert.Contains(
            "FindAutomationIdWithDescendantNameContainingForRetry(",
            windows,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicalVoiceLaneUsesNativeCaptureAndVerifiedPlayback()
    {
        var physical = File.ReadAllText(WorkspacePath(
            "tests", "Deep.Client.Maui.UiTests", "StrictCrossPlatformUiTests.cs"));
        var desktop = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "Pages", "DesktopWorkspacePage.xaml.cs"));

        Assert.Contains("android.Hold(options.App(\"Chat.Voice\")", physical,
            StringComparison.Ordinal);
        Assert.Contains("windows.WaitForOneNewAutomationId(", physical,
            StringComparison.Ordinal);
        Assert.Contains("options.App(\"Chat.MessageBubble\")", physical,
            StringComparison.Ordinal);
        Assert.Contains("PhysicalE2E.VoicePlaybackState", physical,
            StringComparison.Ordinal);
        Assert.Contains("snapshot.IsPlaying", desktop, StringComparison.Ordinal);
        Assert.Contains("PhysicalE2E.VoicePlaybackState", desktop,
            StringComparison.Ordinal);
    }

    [Fact]
    public void VoiceCorrelationIdIsExposedOnlyByTheExistingPhysicalCompileGate()
    {
        var chat = LoadPage("ChatPage.xaml");
        var desktop = LoadPage("DesktopWorkspacePage.xaml");
        const string gatedBinding =
            "{Binding VoiceAttachmentId, Converter={StaticResource VoiceMessageSemanticDescription}}";

        var voiceActions = new[]
        {
            Assert.Single(ElementsWithAutomationId(chat.Root!, "Chat.VoicePlayButton")),
            Assert.Single(ElementsWithAutomationId(
                desktop.Root!, "DesktopWorkspace.DirectVoicePlay")),
            Assert.Single(ElementsWithAutomationId(
                desktop.Root!, "DesktopWorkspace.GroupVoicePlay"))
        };
        Assert.All(voiceActions, action => Assert.Equal(
            gatedBinding,
            action.Attribute("SemanticProperties.Description")?.Value));

        var converter = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "Pages",
            "VoiceMessageSemanticDescriptionConverter.cs"));
        Assert.Contains("#if DEBUG && DEEP_PHYSICAL_E2E", converter,
            StringComparison.Ordinal);
        Assert.Contains("return value as string ?? string.Empty;", converter,
            StringComparison.Ordinal);
        Assert.Contains("Воспроизвести голосовое сообщение", converter,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.GetEnvironmentVariable", converter,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicalCallLaneRequiresAuthenticatedIceBidirectionalMediaMuteAndRemoteHangup()
    {
        var physical = File.ReadAllText(WorkspacePath(
            "tests", "Deep.Client.Maui.UiTests", "StrictCrossPlatformUiTests.cs"));
        var callPage = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "Pages", "CallPage.xaml.cs"));
        var webRtc = File.ReadAllText(WorkspacePath(
            "src", "Deep.Client.Maui", "Resources", "Raw", "wwwroot", "call", "app.js"));

        Assert.Contains("case Mau2PhysicalPhase.Call:", physical,
            StringComparison.Ordinal);
        Assert.Contains("DesktopWorkspace.AudioCall", physical,
            StringComparison.Ordinal);
        Assert.Contains("android:id/alertTitle", physical,
            StringComparison.Ordinal);
        Assert.Contains("android:id/button1", physical,
            StringComparison.Ordinal);
        Assert.Contains("options.App(\"Call.MediaState\")", physical,
            StringComparison.Ordinal);
        Assert.Contains("windows.WaitForAutomationIdWithName(", physical,
            StringComparison.Ordinal);
        Assert.Contains("options.App(\"Call.Microphone\")", physical,
            StringComparison.Ordinal);
        Assert.Contains("options.App(\"Call.Hangup\")", physical,
            StringComparison.Ordinal);
        Assert.Contains("selectedIceCandidatePairObserved", physical,
            StringComparison.Ordinal);
        Assert.Contains("bidirectionalAudioRtpObserved", physical,
            StringComparison.Ordinal);

        Assert.Contains("report.type !== \"candidate-pair\"", webRtc,
            StringComparison.Ordinal);
        Assert.Contains("report.type === \"inbound-rtp\"", webRtc,
            StringComparison.Ordinal);
        Assert.Contains("report.type === \"outbound-rtp\"", webRtc,
            StringComparison.Ordinal);
        Assert.Contains("iceSelected: selectedCandidatePair", webRtc,
            StringComparison.Ordinal);
        Assert.Contains("inboundAudioActive: inboundAudioPackets > 0", webRtc,
            StringComparison.Ordinal);
        Assert.Contains("outboundAudioActive: outboundAudioPackets > 0", webRtc,
            StringComparison.Ordinal);
        Assert.Contains("Malformed WebRTC media state.", callPage,
            StringComparison.Ordinal);
        Assert.Contains("Malformed WebRTC control state.", callPage,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CallControlsExposeStablePrivacySafeSelectors()
    {
        var chat = LoadPage("ChatPage.xaml");
        var call = LoadPage("CallPage.xaml");

        AssertTappedAction(chat, "OnAudioCallClicked", "Chat.AudioCall");
        AssertTappedAction(chat, "OnVideoCallClicked", "Chat.VideoCall");
        AssertTappedAction(call, "OnMicrophoneClicked", "Call.Microphone");
        AssertTappedAction(call, "OnHangupClicked", "Call.Hangup");
        AssertSelectorExists([call], "Call.Root");
        AssertSelectorExists([call], "Call.Status");
        AssertSelectorExists([call], "Call.MediaState");
        AssertSelectorExists([call], "Call.MicrophoneState");
    }

    [Fact]
    public void E2eAutomationIdsAreLiteralPrivacySafeRolesAndPageSelectorsAreUnique()
    {
        var pages = new[]
        {
            LoadPage("ConversationsPage.xaml"),
            LoadPage("ChatPage.xaml"),
            LoadPage("GroupChatPage.xaml"),
            LoadPage("DesktopWorkspacePage.xaml"),
            LoadPage("CallPage.xaml")
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
        AssertSelectorExists(pages, "Chat.StagedAttachmentMetadata");
        AssertSelectorExists(pages, "DesktopWorkspace.AttachmentPickPhoto");
        AssertSelectorExists(pages, "DesktopWorkspace.AttachmentPickVideo");
        AssertSelectorExists(pages, "DesktopWorkspace.AttachmentPickFile");
        AssertSelectorExists(pages, "DesktopWorkspace.DirectStagedAttachmentMetadata");
        AssertSelectorExists(pages, "Chat.MessageBubble");
        AssertSelectorExists(pages, "Chat.ImagePreview");
        AssertSelectorExists(pages, "Chat.ImageMetadata");
        AssertSelectorExists(pages, "Chat.AttachmentMetadata");
        AssertSelectorExists(pages, "GroupChat.StagedAttachmentFilename");
        AssertSelectorExists(pages, "DesktopWorkspace.DirectMessageBubble");
        AssertSelectorExists(pages, "DesktopWorkspace.DirectMessageBody");
        AssertSelectorExists(pages, "DesktopWorkspace.DirectDeliveryStatus");
        AssertSelectorExists(pages, "DesktopWorkspace.DirectAttachmentMetadata");
        AssertSelectorExists(pages, "DesktopWorkspace.DirectImagePreview");
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
