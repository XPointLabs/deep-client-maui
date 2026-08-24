using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;
using Xunit.Sdk;

namespace Deep.Client.Maui.UiTests;

/// <summary>
/// Opt-in, physical Android↔Windows acceptance. This is intentionally not an emulator,
/// Appium, WinAppDriver, mocked transport, or coordinate-script lane. Every Android action
/// starts with a fresh uiautomator tree and one configured exact resource-id; every Windows
/// action is scoped to the spawned application's exact PID and one AutomationId.
/// </summary>
public sealed class StrictCrossPlatformUiTests
{
    [StrictCrossPlatformUiFact]
    public void Physical_android_and_windows_exchange_persist_and_decrypt_an_attachment()
    {
        var options = CrossPlatformOptions.Load();
        switch (options.Phase)
        {
            case Mau2PhysicalPhase.ProvisionIdentity:
                ProvisionOrPreserveIdentities(options);
                return;
            case Mau2PhysicalPhase.Attach:
                AttachToExistingProvisionedClients(options);
                return;
            case Mau2PhysicalPhase.PayloadMatrix:
                ExercisePayloadMatrixOnExistingProvisionedClients(options);
                return;
            case Mau2PhysicalPhase.Call:
                ExchangeAudioCallOnExistingProvisionedClients(options);
                return;
            case Mau2PhysicalPhase.RestartDurability:
                RestartAndAssertDeduplicatedReceive(options);
                return;
            case Mau2PhysicalPhase.ManualResendAfterRestart:
                ExerciseManualResendAfterRestart(options);
                return;
            case Mau2PhysicalPhase.AutomaticRetryAfterRestart:
                ExerciseAutomaticRetryAfterRestart(options);
                return;
            case Mau2PhysicalPhase.AckCrashWindow:
                ExerciseAckCrashWindow(options);
                return;
            case Mau2PhysicalPhase.NegativeRuntime:
                AssertInvalidWindowsRuntimesFailClosed(options);
                return;
            default:
                throw new InvalidOperationException("Unsupported physical MAU2 phase.");
        }
    }

    private static void ProvisionOrPreserveIdentities(CrossPlatformOptions options)
    {
        var evidence = CreatePhaseEvidence(options);
        var android = new AndroidUiautomatorClient(options);
        android.AssertPhysicalConnectedDevice();
        android.AssertInstalledPackage(options.ReadAndValidateApkMetadata());
        android.ColdStart();
        var androidSurface = android.WaitForExactlyOneResource(
            [
                options.App("Welcome.DisplayName"),
                options.App("Conversations.Root")
            ],
            TimeSpan.FromSeconds(45));
        var androidCreated = string.Equals(
            androidSurface, options.App("Welcome.DisplayName"), StringComparison.Ordinal);
        var androidIdentity = androidCreated
            ? CreateAndroidIdentity(android, options)
            : ReadAndroidIdentity(android, options);

        using var windows = WindowsUiSmokeTests.WindowsUiTestSession
            .CreateStrictWithAppData(options.WindowsAppDataRoot);
        var windowsSurface = WaitForExactlyOneWindowsSurface(
            windows,
            ["Welcome.DisplayName", "Conversations.NewConversation"],
            TimeSpan.FromSeconds(45));
        var windowsCreated = string.Equals(
            windowsSurface, "Welcome.DisplayName", StringComparison.Ordinal);
        var windowsIdentity = windowsCreated
            ? CreateWindowsIdentity(windows)
            : ReadWindowsIdentity(windows);
        Assert.NotEqual(androidIdentity, windowsIdentity);

        evidence.AddHash("androidIdentityHash", androidIdentity);
        evidence.AddHash("windowsIdentityHash", windowsIdentity);
        evidence.AddBoolean("androidIdentityCreated", androidCreated);
        evidence.AddBoolean("windowsIdentityCreated", windowsCreated);
        evidence.AddBoolean("destructiveResetPerformed", false);
        evidence.AddBoolean("authenticatedMau2EnvironmentValidated", true);
        CompletePhaseEvidence(options, evidence);
    }

    private static string WaitForExactlyOneWindowsSurface(
        WindowsUiSmokeTests.WindowsUiTestSession windows,
        IReadOnlyList<string> automationIds,
        TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            // One bounded tree snapshot is required here. WinUI can spend the
            // entire COM timeout proving that the first mutually exclusive id
            // is absent, preventing the existing surface from ever being read.
            var present = windows.FindPresentAutomationIds(automationIds)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (present.Length == 1) return present[0];
            if (present.Length > 1)
            {
                throw new InvalidOperationException(
                    "Windows exposed more than one mutually exclusive identity-provisioning surface.");
            }
            Thread.Sleep(200);
        }
        throw new InvalidOperationException(
            "Windows did not expose one closed identity-provisioning surface.");
    }

    private static void AttachToExistingProvisionedClients(CrossPlatformOptions options)
    {
        var evidence = CreatePhaseEvidence(options);
        var android = new AndroidUiautomatorClient(options);
        android.AssertPhysicalConnectedDevice();
        android.AssertInstalledPackage(options.ReadAndValidateApkMetadata());
        android.ColdStart();
        android.WaitForResource(options.App("Conversations.Root"), TimeSpan.FromSeconds(45));
        android.WaitForResource(options.App("PhysicalE2E.RuntimeReadyMarker"), TimeSpan.FromSeconds(45));
        var androidIdentity = ReadAndroidIdentity(android, options);

        using var windows = WindowsUiSmokeTests.WindowsUiTestSession.CreateStrictWithAppData(options.WindowsAppDataRoot);
        Require(windows.WaitForAutomationId("Conversations.NewConversation", TimeSpan.FromSeconds(45)), "Conversations.NewConversation");
        Require(windows.WaitForAutomationId("PhysicalE2E.RuntimeReadyMarker", TimeSpan.FromSeconds(45)), "PhysicalE2E.RuntimeReadyMarker");
        var windowsIdentity = ReadWindowsIdentity(windows);
        Assert.NotEqual(androidIdentity, windowsIdentity);

        evidence.AddHash("androidIdentityHash", androidIdentity);
        evidence.AddHash("windowsIdentityHash", windowsIdentity);
        evidence.AddBoolean("provisionedStatePreserved", true);
        evidence.AddBoolean("authenticatedMau2EnvironmentValidated", true);
        CompletePhaseEvidence(options, evidence);
    }

    private static void ExercisePayloadMatrixOnExistingProvisionedClients(CrossPlatformOptions options)
    {
        var evidence = CreatePhaseEvidence(options);
        var android = new AndroidUiautomatorClient(options);
        android.AssertPhysicalConnectedDevice();
        android.AssertInstalledPackage(options.ReadAndValidateApkMetadata());
        android.ColdStart();
        android.WaitForResource(options.App("Conversations.Root"), TimeSpan.FromSeconds(45));
        var androidIdentity = ReadAndroidIdentity(android, options);
        var windowsToAndroid = StrictCrossPlatformContracts.NewMarker("windows-to-android");
        var androidToWindows = StrictCrossPlatformContracts.NewMarker("android-to-windows");
        var genericName = Path.GetFileName(options.GenericFixturePath);
        var documentName = Path.GetFileName(options.DocumentFixturePath);
        var imageName = Path.GetFileName(options.ImageFixturePath);
        var genericSha256 = StrictCrossPlatformContracts.Sha256File(options.GenericFixturePath);
        var documentSha256 = StrictCrossPlatformContracts.Sha256File(options.DocumentFixturePath);
        var downloadsDirectory = options.ResolveProductionDownloadsDirectory();
        var createdDownloads = new List<string>();
        VoiceMatrixSnapshot? voiceSnapshot = null;

        try
        {
            using (var windows = WindowsUiSmokeTests.WindowsUiTestSession.CreateStrictWithAppData(options.WindowsAppDataRoot))
            {
                Require(windows.WaitForAutomationId("Conversations.NewConversation", TimeSpan.FromSeconds(45)), "Conversations.NewConversation");
                var windowsIdentity = ReadWindowsIdentity(windows);
                Assert.NotEqual(androidIdentity, windowsIdentity);

                // Contacts are created from identities kept in this process only. They
                // are deliberately never emitted into the run state or test evidence.
                AddAndroidContact(android, options, windowsIdentity);
                AddWindowsContact(windows, androidIdentity);
                var androidVoiceBaseline = android.SnapshotAccessibleTextSet(
                    options.App("Chat.VoicePlayButton"));
                var windowsVoiceBaseline = windows.SnapshotAutomationIdNames(
                    "DesktopWorkspace.DirectVoicePlay");
                SendWindowsMessageAndAssertSent(windows, windowsToAndroid);
                AssertWindowsXPointRouteObserved(windows);
                android.WaitForExactResourceTextCount(options.App("Chat.MessageBody"),
                    windowsToAndroid, 1, TimeSpan.FromSeconds(60));
                SendAndroidMessageAndAssertSent(android, options, androidToWindows);
                WaitForWindowsText(windows, "DesktopWorkspace.DirectMessageBody", androidToWindows);

                ExchangeAndroidDocument(android, windows, options, options.GenericFixturePath,
                    genericName, genericSha256, downloadsDirectory, createdDownloads,
                    verifyOpen: false);
                ExchangeAndroidDocument(android, windows, options, options.DocumentFixturePath,
                    documentName, documentSha256, downloadsDirectory, createdDownloads,
                    verifyOpen: true);
                ExchangeWindowsDocument(windows, android, options, options.GenericFixturePath,
                    genericName, genericSha256, verifyOpen: false);
                ExchangeWindowsDocument(windows, android, options, options.DocumentFixturePath,
                    documentName, documentSha256, verifyOpen: true);
                ExchangeInlineImagesBothDirections(android, windows, options, imageName);
                voiceSnapshot = ExchangeVoiceMessagesBothDirections(
                    android, windows, options, androidVoiceBaseline,
                    windowsVoiceBaseline);
            }

            var persistedVoice = voiceSnapshot ?? throw new InvalidOperationException(
                "Voice persistence markers were not captured.");

            android.ColdStart();
            android.WaitForResource(options.App("Conversations.Root"), TimeSpan.FromSeconds(45));
            android.Tap(options.App("Conversations.ConversationRow"));
            android.WaitForExactAccessibleTextSet(options.App("Chat.VoicePlayButton"),
                persistedVoice.AndroidExpectedAfterPhase, TimeSpan.FromSeconds(45));

            using var restartedWindows = WindowsUiSmokeTests.WindowsUiTestSession
                .CreateStrictWithAppData(options.WindowsAppDataRoot);
            restartedWindows.ActivateExact(Require(
                restartedWindows.WaitForAutomationId("DesktopWorkspace.ConversationRow", TimeSpan.FromSeconds(45)),
                "DesktopWorkspace.ConversationRow"));
            restartedWindows.WaitForExactAutomationIdNameSet(
                "DesktopWorkspace.DirectVoicePlay",
                persistedVoice.WindowsExpectedAfterPhase,
                TimeSpan.FromSeconds(45));
        }
        finally
        {
            android.DeletePushedFixture(genericName);
            android.DeletePushedFixture(documentName);
            android.DeletePushedMediaFixture(imageName);
            foreach (var path in createdDownloads)
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        evidence.AddHash("windowsToAndroidMarkerHash", windowsToAndroid);
        evidence.AddHash("androidToWindowsMarkerHash", androidToWindows);
        evidence.AddBoolean("windowsToAndroidReceived", true);
        evidence.AddBoolean("androidToWindowsReceived", true);
        evidence.AddBoolean("windowsXpointRouteObserved", true);
        evidence.AddBoolean("senderDeliveryStatusObserved", true);
        evidence.AddSafeValue("genericPlaintextSha256", genericSha256);
        evidence.AddSafeValue("documentPlaintextSha256", documentSha256);
        evidence.AddBoolean("genericAndDocumentMetadataVerifiedBothDirections", true);
        evidence.AddBoolean("attachmentOpenActionInvoked", true);
        evidence.AddBoolean("attachmentSavePlaintextSha256VerifiedBothDirections", true);
        evidence.AddBoolean("inlineImagePreviewAndMetadataVerified", true);
        evidence.AddBoolean("voiceDeliveredExactlyOnceBothDirections", true);
        evidence.AddBoolean("voicePlaybackStartedAndCompletedBothDirections", true);
        evidence.AddBoolean("voiceMessagesPersistedAcrossRestart", true);
        // There is no self-copy AutomationId/action in the product contract. A test
        // must not synthesize one or make a false pass claim.
        evidence.AddBoolean("selfCopyUiSupported", false);
        evidence.AddBoolean("authenticatedMau2EnvironmentValidated", true);
        CompletePhaseEvidence(options, evidence);
    }

    private static void ExerciseManualResendAfterRestart(CrossPlatformOptions options)
    {
        const string fault = "post-durable-response-drop";
        var evidence = CreatePhaseEvidence(options);
        var android = PrepareChaosAndroid(options, out var androidIdentity);
        var marker = StrictCrossPlatformContracts.NewMarker("manual-resend");
        var androidContact = StrictCrossPlatformContracts.NewMarker("manual-windows");
        var windowsContact = StrictCrossPlatformContracts.NewMarker("manual-android");
        var firstWindowsPid = 0;
        var controller = PhysicalChaosController.LoadRequired();
        Exception? operationFailure = null;
        try
        {
            using (var windows = WindowsUiSmokeTests.WindowsUiTestSession
                .CreateStrictWithAppData(options.WindowsAppDataRoot))
            {
                firstWindowsPid = windows.ProcessId;
                var windowsIdentity = ReadWindowsIdentity(windows);
                AddAndroidContact(android, options, windowsIdentity, androidContact);
                AddWindowsContact(windows, androidIdentity, windowsContact);
                android.WaitForExactResourceTextCount(options.App("Chat.MessageBody"),
                    marker, 0, TimeSpan.FromSeconds(2));
                controller.Begin(fault, "mailbox-store");
                SendWindowsMessage(windows, marker);
                var failed = Require(windows.WaitForCorrelatedDescendant(
                    "DesktopWorkspace.DirectMessageBubble",
                    "DesktopWorkspace.DirectMessageBody", marker,
                    "DesktopWorkspace.DirectDeliveryStatus", TimeSpan.FromSeconds(45),
                    "Не удалось отправить"),
                    "DesktopWorkspace.DirectDeliveryStatus:failed");
                Assert.Equal("Не удалось отправить", failed.Properties.Name.ValueOrDefault);
                Require(windows.WaitForCorrelatedDescendant(
                    "DesktopWorkspace.DirectMessageBubble",
                    "DesktopWorkspace.DirectMessageBody", marker,
                    "DesktopWorkspace.DirectRetry", TimeSpan.FromSeconds(15)),
                    "DesktopWorkspace.DirectRetry");
                controller.Status().AssertConsumed(
                    fault, "mailbox-store", attempts: 1, dispatches: 1,
                    successes: 1, postDrop: 1, preOutage: 0, ackDrop: 0);
            }

            using (var restarted = WindowsUiSmokeTests.WindowsUiTestSession
                .CreateStrictWithAppData(options.WindowsAppDataRoot))
            {
                StrictCrossPlatformContracts.AssertDistinctProcessIds(
                    firstWindowsPid, restarted.ProcessId);
                restarted.ActivateExact(Require(restarted.WaitForAutomationIdWithName(
                    "DesktopWorkspace.ConversationRow", windowsContact,
                    TimeSpan.FromSeconds(45)), "DesktopWorkspace.ConversationRow"));
                var retry = Require(restarted.WaitForCorrelatedDescendant(
                    "DesktopWorkspace.DirectMessageBubble",
                    "DesktopWorkspace.DirectMessageBody", marker,
                    "DesktopWorkspace.DirectRetry", TimeSpan.FromSeconds(45)),
                    "DesktopWorkspace.DirectRetry");
                restarted.ActivateExact(retry);
                var sent = Require(restarted.WaitForCorrelatedDescendant(
                    "DesktopWorkspace.DirectMessageBubble",
                    "DesktopWorkspace.DirectMessageBody", marker,
                    "DesktopWorkspace.DirectDeliveryStatus", TimeSpan.FromSeconds(60),
                    "Отправлено"), "DesktopWorkspace.DirectDeliveryStatus:sent");
                Assert.Equal("Отправлено", sent.Properties.Name.ValueOrDefault);
            }

            android.WaitForExactResourceTextCount(options.App("Chat.MessageBody"),
                marker, 1, TimeSpan.FromSeconds(60));
            var final = controller.Status();
            final.AssertConsumed(fault, "mailbox-store", attempts: 2,
                dispatches: 2, successes: 2, postDrop: 1, preOutage: 0, ackDrop: 0);
            var statusSha = controller.WriteVerifiedStatusEvidence(
                options.ArtifactDirectory, options.Phase, final);
            evidence.AddHash("operationMarkerHash", marker);
            evidence.AddSafeValue("chaosStatusSha256", statusSha);
            evidence.AddSafeValue("chaosDependencyManifestSha256",
                PhysicalChaosController.DependencyManifestSha256);
            evidence.AddSafeValue("chaosExecutionSnapshotSha256",
                controller.ExecutionSnapshotSha256);
            evidence.AddBoolean("senderFailedStatePersistedAcrossRestart", true);
            evidence.AddBoolean("manualRetryActionInvoked", true);
            evidence.AddBoolean("senderSentAfterExactRetry", true);
            evidence.AddBoolean("recipientRenderedExactlyOnce", true);
            evidence.AddBoolean("httpsStoreFaultConsumedExactlyOnce", true);
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }
        CompleteChaosPhase(options, evidence, controller, operationFailure);
    }

    private static void ExerciseAutomaticRetryAfterRestart(CrossPlatformOptions options)
    {
        const string fault = "pre-dispatch-outage";
        var evidence = CreatePhaseEvidence(options);
        var android = PrepareChaosAndroid(options, out var androidIdentity);
        var marker = StrictCrossPlatformContracts.NewMarker("automatic-retry");
        var androidContact = StrictCrossPlatformContracts.NewMarker("automatic-windows");
        var windowsContact = StrictCrossPlatformContracts.NewMarker("automatic-android");
        var firstWindowsPid = 0;
        var controller = PhysicalChaosController.LoadRequired();
        Exception? operationFailure = null;
        try
        {
            using (var windows = WindowsUiSmokeTests.WindowsUiTestSession
                .CreateStrictWithAppData(options.WindowsAppDataRoot))
            {
                firstWindowsPid = windows.ProcessId;
                var windowsIdentity = ReadWindowsIdentity(windows);
                AddAndroidContact(android, options, windowsIdentity, androidContact);
                AddWindowsContact(windows, androidIdentity, windowsContact);
                android.WaitForExactResourceTextCount(options.App("Chat.MessageBody"),
                    marker, 0, TimeSpan.FromSeconds(2));
                controller.Begin(fault, "mailbox-store");
                SendWindowsMessage(windows, marker);
                Require(windows.WaitForCorrelatedDescendant(
                    "DesktopWorkspace.DirectMessageBubble",
                    "DesktopWorkspace.DirectMessageBody", marker,
                    "DesktopWorkspace.DirectDeliveryStatus", TimeSpan.FromSeconds(45),
                    "Не удалось отправить"),
                    "DesktopWorkspace.DirectDeliveryStatus:failed");
                controller.Status().AssertConsumed(
                    fault, "mailbox-store", attempts: 1, dispatches: 0,
                    successes: 0, postDrop: 0, preOutage: 1, ackDrop: 0);
            }

            using (var restarted = WindowsUiSmokeTests.WindowsUiTestSession
                .CreateStrictWithAppData(options.WindowsAppDataRoot))
            {
                StrictCrossPlatformContracts.AssertDistinctProcessIds(
                    firstWindowsPid, restarted.ProcessId);
                restarted.ActivateExact(Require(restarted.WaitForAutomationIdWithName(
                    "DesktopWorkspace.ConversationRow", windowsContact,
                    TimeSpan.FromSeconds(45)), "DesktopWorkspace.ConversationRow"));
                var sent = Require(restarted.WaitForCorrelatedDescendant(
                    "DesktopWorkspace.DirectMessageBubble",
                    "DesktopWorkspace.DirectMessageBody", marker,
                    "DesktopWorkspace.DirectDeliveryStatus", TimeSpan.FromSeconds(90),
                    "Отправлено"), "DesktopWorkspace.DirectDeliveryStatus:sent");
                Assert.Equal("Отправлено", sent.Properties.Name.ValueOrDefault);
            }

            android.WaitForExactResourceTextCount(options.App("Chat.MessageBody"),
                marker, 1, TimeSpan.FromSeconds(60));
            var final = controller.Status();
            final.AssertConsumed(fault, "mailbox-store", attempts: 2,
                dispatches: 1, successes: 1, postDrop: 0, preOutage: 1, ackDrop: 0);
            var statusSha = controller.WriteVerifiedStatusEvidence(
                options.ArtifactDirectory, options.Phase, final);
            evidence.AddHash("operationMarkerHash", marker);
            evidence.AddSafeValue("chaosStatusSha256", statusSha);
            evidence.AddSafeValue("chaosDependencyManifestSha256",
                PhysicalChaosController.DependencyManifestSha256);
            evidence.AddSafeValue("chaosExecutionSnapshotSha256",
                controller.ExecutionSnapshotSha256);
            evidence.AddBoolean("senderRestartedWithDistinctPid", true);
            evidence.AddBoolean("manualRetryActionInvoked", false);
            evidence.AddBoolean("automaticRetryReachedSent", true);
            evidence.AddBoolean("recipientRenderedExactlyOnce", true);
            evidence.AddBoolean("httpsStoreFaultConsumedExactlyOnce", true);
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }
        CompleteChaosPhase(options, evidence, controller, operationFailure);
    }

    private static AndroidUiautomatorClient PrepareChaosAndroid(
        CrossPlatformOptions options,
        out string identity)
    {
        Mau2PhysicalPhaseContract.RequireChaosPhase(options.Phase);
        var android = new AndroidUiautomatorClient(options);
        android.AssertPhysicalConnectedDevice();
        android.AssertInstalledPackage(options.ReadAndValidateApkMetadata());
        android.ColdStart();
        android.WaitForResource(options.App("Conversations.Root"), TimeSpan.FromSeconds(45));
        identity = ReadAndroidIdentity(android, options);
        return android;
    }

    private static void CompleteChaosPhase(
        CrossPlatformOptions options,
        StrictCrossPlatformContracts.SanitizedEvidence evidence,
        PhysicalChaosController controller,
        Exception? operationFailure)
    {
        Exception? cleanupFailure = null;
        try { controller.EndAndAssertBaseline(); }
        catch (Exception exception) { cleanupFailure = exception; }
        if (operationFailure is not null && cleanupFailure is not null)
            throw new AggregateException(
                "Physical chaos operation and baseline cleanup both failed.",
                operationFailure, cleanupFailure);
        if (operationFailure is not null) throw operationFailure;
        if (cleanupFailure is not null) throw cleanupFailure;
        evidence.AddBoolean("chaosEndRestoredExactBaseline", true);
        evidence.AddBoolean("authenticatedMau2EnvironmentValidated", true);
        CompletePhaseEvidence(options, evidence);
    }

    private static void ExerciseAckCrashWindow(CrossPlatformOptions options)
    {
        const string fault = "post-durable-ack-response-drop";
        var evidence = CreatePhaseEvidence(options);
        var android = PrepareChaosAndroid(options, out var androidIdentity);
        var marker = StrictCrossPlatformContracts.NewMarker("ack-crash-window");
        var androidContact = StrictCrossPlatformContracts.NewMarker("ack-windows");
        var windowsContact = StrictCrossPlatformContracts.NewMarker("ack-android");
        var controller = PhysicalChaosController.LoadRequired();
        Exception? operationFailure = null;
        try
        {
            using var windows = WindowsUiSmokeTests.WindowsUiTestSession
                .CreateStrictWithAppData(options.WindowsAppDataRoot);
            var windowsIdentity = ReadWindowsIdentity(windows);
            AddAndroidContact(android, options, windowsIdentity, androidContact);
            AddWindowsContact(windows, androidIdentity, windowsContact);
            android.WaitForExactResourceTextCount(options.App("Chat.MessageBody"),
                marker, 0, TimeSpan.FromSeconds(2));
            controller.Begin(fault, "mailbox-ack");
            SendWindowsMessageAndAssertSent(windows, marker);
            android.WaitForExactResourceTextCount(options.App("Chat.MessageBody"),
                marker, 1, TimeSpan.FromSeconds(60));
            controller.Status().AssertConsumed(fault, "mailbox-ack", attempts: 1,
                dispatches: 1, successes: 1, postDrop: 0, preOutage: 0, ackDrop: 1);
            var ambiguousNode = android.WaitForCorrelatedDescendant(
                options.App("Chat.MessageBubble"),
                options.App("Chat.MessageBody"), marker,
                options.App("PhysicalE2E.AckCorrelation"), TimeSpan.FromSeconds(30));
            var ambiguous = PhysicalAckCorrelationEvidence.ParseExact(
                ambiguousNode.AccessibleText);
            Assert.Equal(PhysicalAckCorrelationEvidence.MarkerHashFor(marker),
                ambiguous.MarkerHash);
            Assert.Equal("ambiguous-attempted", ambiguous.State);
            Assert.Equal(1, ambiguous.AttemptCount);

            // The exact rendered row now proves its SQLCipher ACK outbox is in the
            // single outcome-unknown attempt state. Kill before any recovery attempt.
            android.ForceStop();

            android.ColdStart();
            android.WaitForResource(options.App("Conversations.Root"), TimeSpan.FromSeconds(45));
            android.TapExactResourceIdWithAccessibleText(
                options.App("Conversations.ConversationRow"), androidContact,
                TimeSpan.FromSeconds(45));
            android.WaitForExactResourceTextCount(options.App("Chat.MessageBody"),
                marker, 1, TimeSpan.FromSeconds(60));
            var expectedRecovered = ambiguous with
            {
                State = "recovered-durable",
                AttemptCount = 2
            };
            var recoveredNode = android.WaitForCorrelatedDescendant(
                options.App("Chat.MessageBubble"),
                options.App("Chat.MessageBody"), marker,
                options.App("PhysicalE2E.AckCorrelation"), TimeSpan.FromSeconds(90),
                expectedRecovered.CanonicalValue());
            var recovered = PhysicalAckCorrelationEvidence.ParseExact(
                recoveredNode.AccessibleText);
            Assert.Equal(ambiguous.MarkerHash, recovered.MarkerHash);
            Assert.Equal(ambiguous.CorrelationHash, recovered.CorrelationHash);
            controller.WaitForConsumed(fault, "mailbox-ack", attempts: 2,
                dispatches: 2, successes: 2, postDrop: 0, preOutage: 0, ackDrop: 1,
                timeout: TimeSpan.FromSeconds(90));
            var senderStatus = Require(windows.WaitForCorrelatedDescendant(
                "DesktopWorkspace.DirectMessageBubble",
                "DesktopWorkspace.DirectMessageBody", marker,
                "DesktopWorkspace.DirectDeliveryStatus", TimeSpan.FromSeconds(30),
                "Отправлено"), "DesktopWorkspace.DirectDeliveryStatus:sent");
            Assert.Equal("Отправлено", senderStatus.Properties.Name.ValueOrDefault);

            var final = controller.Status();
            final.AssertConsumed(fault, "mailbox-ack", attempts: 2,
                dispatches: 2, successes: 2, postDrop: 0, preOutage: 0, ackDrop: 1);
            var statusSha = controller.WriteVerifiedStatusEvidence(
                options.ArtifactDirectory, options.Phase, final);
            evidence.AddHash("operationMarkerHash", marker);
            evidence.AddSafeValue("chaosStatusSha256", statusSha);
            evidence.AddSafeValue("chaosDependencyManifestSha256",
                PhysicalChaosController.DependencyManifestSha256);
            evidence.AddSafeValue("chaosExecutionSnapshotSha256",
                controller.ExecutionSnapshotSha256);
            evidence.AddSafeValue("ackCorrelationEvidenceSha256",
                recovered.SanitizedEvidenceHash());
            evidence.AddBoolean("recipientDurablyRenderedBeforeCrash", true);
            evidence.AddBoolean("recipientRestartedAfterUnknownAckOutcome", true);
            evidence.AddBoolean("recipientRenderedExactlyOnceAfterRestart", true);
            evidence.AddBoolean("sameDurableAckRetriedOnceAfterRestart", true);
            evidence.AddBoolean("sameCanonicalAckCorrelationRecovered", true);
            evidence.AddBoolean("senderRemainedSent", true);
            evidence.AddBoolean("httpsAckFaultConsumedExactlyOnce", true);
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }
        CompleteChaosPhase(options, evidence, controller, operationFailure);
    }

    private static void RestartAndAssertDeduplicatedReceive(CrossPlatformOptions options)
    {
        var evidence = CreatePhaseEvidence(options);
        var android = new AndroidUiautomatorClient(options);
        android.AssertPhysicalConnectedDevice();
        android.AssertInstalledPackage(options.ReadAndValidateApkMetadata());
        android.ColdStart();
        android.WaitForResource(options.App("Conversations.Root"), TimeSpan.FromSeconds(45));
        var androidIdentity = ReadAndroidIdentity(android, options);
        var marker = StrictCrossPlatformContracts.NewMarker("restart-resend");
        int firstWindowsPid;
        using (var windows = WindowsUiSmokeTests.WindowsUiTestSession.CreateStrictWithAppData(options.WindowsAppDataRoot))
        {
            firstWindowsPid = windows.ProcessId;
            AddWindowsContact(windows, androidIdentity);
            SendWindowsMessage(windows, marker);
            android.Tap(options.App("Conversations.ConversationRow"));
            android.WaitForText(options.App("Chat.MessageBody"), marker, TimeSpan.FromSeconds(60));
        }

        android.ColdStart();
        android.WaitForResource(options.App("Conversations.Root"), TimeSpan.FromSeconds(45));
        android.Tap(options.App("Conversations.ConversationRow"));
        // FindExactlyOneResourceIdContainingText is intentionally an exact-once
        // assertion. A duplicate after restart is a failure, not a best-effort poll.
        android.WaitForText(options.App("Chat.MessageBody"), marker, TimeSpan.FromSeconds(45));

        using (var restartedWindows = WindowsUiSmokeTests.WindowsUiTestSession.CreateStrictWithAppData(options.WindowsAppDataRoot))
        {
            StrictCrossPlatformContracts.AssertDistinctProcessIds(firstWindowsPid, restartedWindows.ProcessId);
            restartedWindows.ActivateExact(Require(
                restartedWindows.WaitForAutomationIdWithDescendantNameContaining(
                    "DesktopWorkspace.ConversationRow",
                    marker[..32],
                    TimeSpan.FromSeconds(45)),
                "DesktopWorkspace.ConversationRow"));
            Require(restartedWindows.WaitForAutomationId("DesktopWorkspace.DirectDraft", TimeSpan.FromSeconds(45)), "DesktopWorkspace.DirectDraft");
            WaitForWindowsText(restartedWindows, "DesktopWorkspace.DirectMessageBody", marker);
        }

        evidence.AddHash("markerHash", marker);
        evidence.AddBoolean("androidRestarted", true);
        evidence.AddBoolean("windowsRestartedWithDistinctPid", true);
        evidence.AddBoolean("receivedExactlyOnceAfterRestart", true);
        // This is deliberately a restart-durability result, not a resend result.
        // Product resend controls are covered separately; their physical phases
        // require reviewed DevOps chaos evidence and cannot be inferred here.
        evidence.AddBoolean("authenticatedMau2EnvironmentValidated", true);
        CompletePhaseEvidence(options, evidence);
    }

    private static void ExchangeAudioCallOnExistingProvisionedClients(
        CrossPlatformOptions options)
    {
        const string ActiveMediaState = "Двусторонний аудиоканал активен";
        const string MicrophoneEnabledState = "Микрофон включен";
        const string MicrophoneMutedState = "Микрофон выключен";
        var evidence = CreatePhaseEvidence(options);
        var android = new AndroidUiautomatorClient(options);
        android.AssertPhysicalConnectedDevice();
        android.AssertInstalledPackage(options.ReadAndValidateApkMetadata());
        android.ColdStart();
        android.WaitForResource(options.App("Conversations.Root"), TimeSpan.FromSeconds(45));
        var androidIdentity = ReadAndroidIdentity(android, options);

        using var windows = WindowsUiSmokeTests.WindowsUiTestSession
            .CreateStrictWithAppData(options.WindowsAppDataRoot);
        var windowsIdentity = ReadWindowsIdentity(windows);
        AddAndroidContact(android, options, windowsIdentity);
        android.Tap(options.App("Chat.Back"));
        android.WaitForResource(options.App("Conversations.Root"), TimeSpan.FromSeconds(20));
        android.WaitForResource(
            options.App("PhysicalE2E.RuntimeReadyMarker"),
            TimeSpan.FromSeconds(45));
        AddWindowsContact(windows, androidIdentity);

        windows.ActivateExact(Require(
            windows.WaitForAutomationId(
                "DesktopWorkspace.AudioCall",
                TimeSpan.FromSeconds(15)),
            "DesktopWorkspace.AudioCall"));
        Assert.Null(android.FindOptional(options.App("Call.Root")));
        android.WaitForText(
            "android:id/alertTitle",
            "Аудиозвонок",
            TimeSpan.FromSeconds(45));
        android.TapExactResourceIdWithExactText("android:id/button1", "Ответить");
        android.WaitForResource(options.App("Call.Root"), TimeSpan.FromSeconds(30));
        _ = android.AllowMicrophonePermissionIfRequested(TimeSpan.FromSeconds(8));
        Require(
            windows.WaitForAutomationId("Call.Root", TimeSpan.FromSeconds(30)),
            "Call.Root");

        android.WaitForText(
            options.App("Call.MediaState"),
            ActiveMediaState,
            TimeSpan.FromSeconds(90));
        Require(
            windows.WaitForAutomationIdWithName(
                "Call.MediaState",
                ActiveMediaState,
                TimeSpan.FromSeconds(90)),
            "Call.MediaState");

        android.Tap(options.App("Call.Microphone"));
        android.WaitForText(
            options.App("Call.MicrophoneState"),
            MicrophoneMutedState,
            TimeSpan.FromSeconds(15));
        android.Tap(options.App("Call.Microphone"));
        android.WaitForText(
            options.App("Call.MicrophoneState"),
            MicrophoneEnabledState,
            TimeSpan.FromSeconds(15));

        android.Tap(options.App("Call.Hangup"));
        android.WaitForResource(
            options.App("Conversations.Root"),
            TimeSpan.FromSeconds(30));
        Require(
            windows.WaitForAutomationId(
                "DesktopWorkspace.AudioCall",
                TimeSpan.FromSeconds(30)),
            "DesktopWorkspace.AudioCall");
        Assert.Null(android.FindOptional(options.App("Call.Root")));
        Assert.Null(windows.FindAutomationId("Call.Root"));

        evidence.AddBoolean("outgoingOfferStarted", true);
        evidence.AddBoolean("incomingRingingObserved", true);
        evidence.AddBoolean("incomingAnswerAccepted", true);
        evidence.AddBoolean("selectedIceCandidatePairObserved", true);
        evidence.AddBoolean("bidirectionalAudioRtpObserved", true);
        evidence.AddBoolean("microphoneMuteApplied", true);
        evidence.AddBoolean("microphoneRestoreApplied", true);
        evidence.AddBoolean("remoteHangupObserved", true);
        evidence.AddBoolean("authenticatedMau2EnvironmentValidated", true);
        CompletePhaseEvidence(options, evidence);
    }

    private static void AssertInvalidWindowsRuntimesFailClosed(CrossPlatformOptions options)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("DEEP_TRANSPORT_PROTOCOL"),
                "authenticated-mau2", StringComparison.Ordinal) ||
            !string.Equals(Environment.GetEnvironmentVariable("DEEP_TRANSPORT_OWNERSHIP"),
                "user-managed", StringComparison.Ordinal) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DEEP_STORAGE_URL")))
        {
            throw new InvalidOperationException(
                "Negative runtime evidence requires authenticated MAU2 without a direct storage fallback.");
        }

        var evidence = CreatePhaseEvidence(options);
        var runState = Mau2PhysicalPhaseContract.RequireSanitizedRunStatePath();
        var cases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tampered-signature"] = "approval-signature",
            ["missing-authority"] = "inventory",
            ["android-runtime-on-windows"] = "platform-binding"
        };

        foreach (var (caseName, expectedFailureCode) in cases)
        {
            var appDataRoot = Mau2PhysicalPhaseContract.RequireProtectedNegativeCase(
                runState, caseName);

            using var windows = WindowsUiSmokeTests.WindowsUiTestSession
                .CreateStrictWithAppData(appDataRoot);
            windows.AssertStartupFailClosed(expectedFailureCode, TimeSpan.FromSeconds(45));
            evidence.AddBoolean(caseName + "Rejected", true);
        }

        evidence.AddBoolean("directFallbackAbsent", true);
        evidence.AddBoolean("rawFallbackAbsent", true);
        evidence.AddBoolean("canonicalWindowsRuntimePreserved", true);
        evidence.AddSafeValue(
            "androidNegativeRuntime",
            "shared-platform-binding-only-no-device-mutation");
        CompletePhaseEvidence(options, evidence);
    }

    private static StrictCrossPlatformContracts.SanitizedEvidence CreatePhaseEvidence(CrossPlatformOptions options)
    {
        var evidence = new StrictCrossPlatformContracts.SanitizedEvidence();
        evidence.AddSafeValue("schema", "deep.physical-mau2-phase.v1");
        evidence.AddSafeValue("phase", options.Phase.ToString());
        evidence.AddSafeValue("sourceCommit", options.SourceCommit);
        evidence.AddSafeValue("policyId", (ApprovedCrossPlatformPolicy.Current ?? throw new InvalidOperationException("Approved policy was not loaded.")).PolicyId);
        evidence.AddSafeValue("storageReplication", "shared-dev-storage-non-replicated");
        return evidence;
    }

    private static void CompletePhaseEvidence(CrossPlatformOptions options, StrictCrossPlatformContracts.SanitizedEvidence evidence)
    {
        evidence.AddBoolean("productionPackageUntouched", true);
        evidence.AddSafeValue("windowsOutputTreeSha256", options.WindowsOutputTreeSha256);
        evidence.AddSafeValue("status", "passed");
        evidence.Write(options.ResultPath);
    }

    private static string ReadAndroidIdentity(AndroidUiautomatorClient android, CrossPlatformOptions options)
    {
        android.Tap(options.App("Conversations.ProfileSettings"));
        var identity = StrictCrossPlatformContracts.RequireSessionId(android.WaitForResource(options.App("Settings.SessionId"), TimeSpan.FromSeconds(20)).AccessibleText, "Android settings");
        android.Tap(options.App("Settings.Back"));
        android.WaitForResource(options.App("Conversations.Root"), TimeSpan.FromSeconds(20));
        return identity;
    }

    private static string ReadWindowsIdentity(WindowsUiSmokeTests.WindowsUiTestSession windows)
    {
        windows.ActivateExact(Require(windows.WaitForAutomationId("DesktopWorkspace.ProfileSettings", TimeSpan.FromSeconds(20)), "DesktopWorkspace.ProfileSettings"));
        var identity = WaitForWindowsSessionId(windows);
        CloseWindowsSettings(windows);
        return identity;
    }

    private static string CreateAndroidIdentity(AndroidUiautomatorClient android, CrossPlatformOptions options)
    {
        android.WaitForResource(options.App("Welcome.DisplayName"), TimeSpan.FromSeconds(30));
        android.Type(options.App("Welcome.DisplayName"), StrictCrossPlatformContracts.NewMarker("android"));
        android.DismissKeyboard();
        android.Tap(options.App("Welcome.Create"));
        android.WaitForResource(options.App("Conversations.Root"), TimeSpan.FromSeconds(30));
        android.Tap(options.App("Conversations.ProfileSettings"));
        var identity = StrictCrossPlatformContracts.RequireSessionId(android.WaitForResource(options.App("Settings.SessionId"), TimeSpan.FromSeconds(20)).AccessibleText, "Android settings");
        android.Tap(options.App("Settings.Back"));
        return identity;
    }

    private static string CreateWindowsIdentity(WindowsUiSmokeTests.WindowsUiTestSession windows)
    {
        var displayName = Require(windows.WaitForAutomationId("Welcome.DisplayName", TimeSpan.FromSeconds(30)), "Welcome.DisplayName").AsTextBox();
        displayName.Text = StrictCrossPlatformContracts.NewMarker("windows");
        windows.ActivateExact(Require(windows.WaitForAutomationId("Welcome.Create", TimeSpan.FromSeconds(10)), "Welcome.Create"));
        windows.WaitForAutomationId("Conversations.NewConversation", TimeSpan.FromSeconds(30));
        windows.ActivateExact(Require(windows.WaitForAutomationId("DesktopWorkspace.ProfileSettings", TimeSpan.FromSeconds(20)), "DesktopWorkspace.ProfileSettings"));
        var identity = WaitForWindowsSessionId(windows);
        CloseWindowsSettings(windows);
        return identity;
    }

    private static string WaitForWindowsSessionId(
        WindowsUiSmokeTests.WindowsUiTestSession windows)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var candidate = windows
                .WaitForAutomationId("Settings.SessionId", TimeSpan.FromSeconds(1))?
                .Properties.Name.ValueOrDefault;
            if (candidate is not null &&
                System.Text.RegularExpressions.Regex.IsMatch(
                    candidate, "^(05|15|25)[0-9a-f]{64}$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            {
                return StrictCrossPlatformContracts.RequireSessionId(
                    candidate, "Windows settings");
            }

            Thread.Sleep(100);
        }

        throw new InvalidOperationException(
            "Windows settings did not publish its loaded Session identity before the deadline.");
    }

    private static void CloseWindowsSettings(WindowsUiSmokeTests.WindowsUiTestSession windows)
    {
        // WinUI Shell owns the desktop back surface. The Settings.Back border is
        // the compact/mobile affordance and is not present in the Windows UIA tree.
        windows.ActivateExact(Require(
            windows.WaitForAutomationId("NavigationViewBackButton", TimeSpan.FromSeconds(10)),
            "NavigationViewBackButton"));
        Require(
            windows.WaitForAutomationId("Conversations.NewConversation", TimeSpan.FromSeconds(20)),
            "Conversations.NewConversation");
    }

    private static void VerifyAndroidRejectsInvalidIdWithoutOpeningContact(AndroidUiautomatorClient android, CrossPlatformOptions options)
    {
        OpenAndroidNewConversation(android, options);
        var invalid = "35" + new string('0', 64);
        StrictCrossPlatformContracts.RequireInvalidSessionId(invalid);
        android.Type(options.App("NewConversation.SessionId"), invalid);
        android.Tap(options.App("NewConversation.Start"));
        var error = android.WaitForResource(options.App("NewConversation.Error"), TimeSpan.FromSeconds(20));
        Assert.False(string.IsNullOrWhiteSpace(error.Text));
        Assert.Null(android.FindOptional(options.App("Chat.Draft")));
        Assert.Null(android.FindOptional(options.App("Chat.MessageBody")));
        android.Tap(options.App("NewConversation.Back"));
        android.WaitForResource(options.App("Conversations.Root"), TimeSpan.FromSeconds(15));
        Assert.Null(android.FindOptional(options.App("Conversations.ConversationRow")));
    }

    private static void AddAndroidContact(
        AndroidUiautomatorClient android,
        CrossPlatformOptions options,
        string windowsIdentity,
        string? displayName = null)
    {
        OpenAndroidNewConversation(android, options);
        android.Type(options.App("NewConversation.SessionId"), windowsIdentity);
        android.Type(options.App("NewConversation.DisplayName"),
            displayName ?? StrictCrossPlatformContracts.NewMarker("windows-contact"));
        android.Tap(options.App("NewConversation.Start"));
        android.WaitForResource(options.App("Chat.Draft"), TimeSpan.FromSeconds(30));
    }

    private static void OpenAndroidNewConversation(AndroidUiautomatorClient android, CrossPlatformOptions options)
    {
        android.Tap(options.App("Conversations.NewConversationTop"));
        android.Tap(options.App("StartConversation.NewMessage"));
        android.WaitForResource(options.App("NewConversation.SessionId"), TimeSpan.FromSeconds(15));
    }

    private static void AddWindowsContact(
        WindowsUiSmokeTests.WindowsUiTestSession windows,
        string androidIdentity,
        string? displayName = null)
    {
        windows.ActivateExact(Require(windows.WaitForAutomationId("Conversations.NewConversation", TimeSpan.FromSeconds(20)), "Conversations.NewConversation"));
        windows.ActivateExact(Require(windows.WaitForAutomationId("StartConversation.NewMessage", TimeSpan.FromSeconds(15)), "StartConversation.NewMessage"));
        Require(windows.WaitForAutomationId("NewConversation.SessionId", TimeSpan.FromSeconds(15)), "NewConversation.SessionId").AsTextBox().Text = androidIdentity;
        Require(windows.WaitForAutomationId("NewConversation.DisplayName", TimeSpan.FromSeconds(10)), "NewConversation.DisplayName").AsTextBox().Text =
            displayName ?? StrictCrossPlatformContracts.NewMarker("android-contact");
        windows.ActivateExact(Require(windows.WaitForAutomationId("NewConversation.Start", TimeSpan.FromSeconds(10)), "NewConversation.Start"));
        Require(windows.WaitForAutomationId("DesktopWorkspace.DirectDraft", TimeSpan.FromSeconds(30)), "DesktopWorkspace.DirectDraft");
    }

    private static void SendWindowsMessage(WindowsUiSmokeTests.WindowsUiTestSession windows, string message)
    {
        Require(windows.WaitForAutomationId("DesktopWorkspace.DirectDraft", TimeSpan.FromSeconds(15)), "DesktopWorkspace.DirectDraft").AsTextBox().Text = message;
        windows.ActivateExact(Require(windows.WaitForAutomationId("DesktopWorkspace.DirectSend", TimeSpan.FromSeconds(10)), "DesktopWorkspace.DirectSend"));
    }

    private static void SendWindowsMessageAndAssertSent(
        WindowsUiSmokeTests.WindowsUiTestSession windows, string message)
    {
        SendWindowsMessage(windows, message);
        var status = Require(windows.WaitForCorrelatedDescendant(
            "DesktopWorkspace.DirectMessageBubble",
            "DesktopWorkspace.DirectMessageBody", message,
            "DesktopWorkspace.DirectDeliveryStatus", TimeSpan.FromSeconds(30),
            "Отправлено"),
            "DesktopWorkspace.DirectDeliveryStatus");
        Assert.Equal("Отправлено", status.Properties.Name.ValueOrDefault);
    }

    private static void AssertWindowsXPointRouteObserved(
        WindowsUiSmokeTests.WindowsUiTestSession windows)
    {
        var marker = Require(
            windows.WaitForAutomationId(
                "PhysicalE2E.RouteNodeMarker",
                TimeSpan.FromSeconds(30)),
            "PhysicalE2E.RouteNodeMarker");
        var routerId = marker.Properties.Name.ValueOrDefault ?? string.Empty;
        Assert.Equal(64, routerId.Length);
        Assert.All(routerId, static value =>
            Assert.True(value is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }

    private static void SendAndroidMessage(AndroidUiautomatorClient android, CrossPlatformOptions options, string message)
    {
        android.Type(options.App("Chat.Draft"), message);
        android.Tap(options.App("Chat.Send"));
    }

    private static void SendAndroidMessageAndAssertSent(
        AndroidUiautomatorClient android, CrossPlatformOptions options, string message)
    {
        SendAndroidMessage(android, options, message);
        var status = android.WaitForCorrelatedDescendant(
            options.App("Chat.MessageBubble"), options.App("Chat.MessageBody"), message,
            options.App("Chat.DeliveryStatus"), TimeSpan.FromSeconds(30),
            "Отправлено");
        Assert.Equal("Отправлено", status.AccessibleText);
    }

    private static void ExchangeAndroidDocument(
        AndroidUiautomatorClient android,
        WindowsUiSmokeTests.WindowsUiTestSession windows,
        CrossPlatformOptions options,
        string fixturePath,
        string fileName,
        string sha256,
        string downloadsDirectory,
        ICollection<string> createdDownloads,
        bool verifyOpen)
    {
        android.PushFixture(fixturePath, fileName);
        StageAndSendAndroidAttachment(android, options, fileName);
        var androidStatus = android.WaitForCorrelatedDescendant(
            options.App("Chat.MessageBubble"), options.App("Chat.AttachmentFilename"),
            fileName, options.App("Chat.DeliveryStatus"), TimeSpan.FromSeconds(30),
            "Отправлено");
        Assert.Equal("Отправлено", androidStatus.AccessibleText);
        var attachment = Require(windows.WaitForAutomationIdWithName(
            "DesktopWorkspace.DirectAttachmentFilename", fileName,
            TimeSpan.FromSeconds(60)), "DesktopWorkspace.DirectAttachmentFilename");
        Assert.Contains(fileName, attachment.Properties.Name.ValueOrDefault, StringComparison.Ordinal);
        var expectedMetadata = ExpectedAttachmentMetadata(fixturePath);
        var metadata = Require(windows.WaitForCorrelatedDescendant(
            "DesktopWorkspace.DirectMessageBubble",
            "DesktopWorkspace.DirectAttachmentFilename", fileName,
            "DesktopWorkspace.DirectAttachmentMetadata", TimeSpan.FromSeconds(15),
            expectedMetadata), "DesktopWorkspace.DirectAttachmentMetadata");
        Assert.Equal(expectedMetadata, metadata.Properties.Name.ValueOrDefault);
        SaveOpenAndVerifyWindowsAttachment(windows, downloadsDirectory,
            createdDownloads, fileName, sha256, verifyOpen);
    }

    private static void ExchangeWindowsDocument(
        WindowsUiSmokeTests.WindowsUiTestSession windows,
        AndroidUiautomatorClient android,
        CrossPlatformOptions options,
        string fixturePath,
        string fileName,
        string sha256,
        bool verifyOpen)
    {
        windows.ActivateExact(Require(windows.WaitForAutomationId(
            "DesktopWorkspace.DirectAttach", TimeSpan.FromSeconds(15)),
            "DesktopWorkspace.DirectAttach"));
        windows.ActivateExact(Require(windows.WaitForAutomationId(
            "DesktopWorkspace.AttachmentPickFile", TimeSpan.FromSeconds(15)),
            "DesktopWorkspace.AttachmentPickFile"));
        windows.ChooseSingleFileFromOwnedPicker(fixturePath, TimeSpan.FromSeconds(20));
        Require(windows.WaitForAutomationIdWithName(
            "DesktopWorkspace.DirectStagedAttachmentFilename", fileName,
            TimeSpan.FromSeconds(30)), "DesktopWorkspace.DirectStagedAttachmentFilename");
        windows.ActivateExact(Require(windows.WaitForAutomationId(
            "DesktopWorkspace.DirectSend", TimeSpan.FromSeconds(15)),
            "DesktopWorkspace.DirectSend"));
        var windowsStatus = Require(windows.WaitForCorrelatedDescendant(
            "DesktopWorkspace.DirectMessageBubble",
            "DesktopWorkspace.DirectAttachmentFilename", fileName,
            "DesktopWorkspace.DirectDeliveryStatus", TimeSpan.FromSeconds(30),
            "Отправлено"),
            "DesktopWorkspace.DirectDeliveryStatus");
        Assert.Equal("Отправлено", windowsStatus.Properties.Name.ValueOrDefault);

        var file = android.WaitForCorrelatedDescendant(
            options.App("Chat.MessageBubble"), options.App("Chat.AttachmentFilename"),
            fileName, options.App("Chat.AttachmentFilename"), TimeSpan.FromSeconds(60));
        Assert.Equal(fileName, file.AccessibleText);
        var metadata = android.WaitForCorrelatedDescendant(
            options.App("Chat.MessageBubble"), options.App("Chat.AttachmentFilename"),
            fileName, options.App("Chat.AttachmentMetadata"), TimeSpan.FromSeconds(15));
        Assert.Equal(ExpectedAttachmentMetadata(fixturePath), metadata.AccessibleText);

        if (verifyOpen)
        {
            android.Tap(file);
            android.Tap(options.App("Chat.AttachmentOpen"));
            android.WaitForExternalActivity(TimeSpan.FromSeconds(15));
            android.PressBack();
            android.WaitForMessageBubbleContaining(options.App("Chat.AttachmentFilename"),
                fileName, TimeSpan.FromSeconds(20));
        }

        var before = android.SnapshotDownloadPaths();
        file = android.WaitForCorrelatedDescendant(
            options.App("Chat.MessageBubble"), options.App("Chat.AttachmentFilename"),
            fileName, options.App("Chat.AttachmentFilename"), TimeSpan.FromSeconds(15));
        android.Tap(file);
        android.Tap(options.App("Chat.AttachmentSave"));
        android.WaitForSavedPlaintext(before, fileName, sha256, options.ArtifactDirectory,
            TimeSpan.FromSeconds(30));
    }

    private static void ExchangeInlineImagesBothDirections(
        AndroidUiautomatorClient android,
        WindowsUiSmokeTests.WindowsUiTestSession windows,
        CrossPlatformOptions options,
        string imageName)
    {
        var expectedSentName = Path.ChangeExtension(imageName, ".jpg");
        var previousWindowsImages = windows.CountAutomationId(
            "DesktopWorkspace.DirectImagePreview");
        android.PushMediaFixture(options.ImageFixturePath, imageName);
        android.Tap(options.App("Chat.Attach"));
        android.Tap(options.App("Chat.PickPhoto"));
        android.TapExactResourceIdWithExactText(options.PickerFileResourceId, imageName);
        if (options.PickerConfirmResourceId is not null)
            android.Tap(options.PickerConfirmResourceId);
        android.WaitForText(options.App("Chat.StagedAttachmentFilename"),
            expectedSentName, TimeSpan.FromSeconds(30));
        var androidStagedMetadata = android.WaitForResource(
            options.App("Chat.StagedAttachmentMetadata"),
            TimeSpan.FromSeconds(15)).AccessibleText;
        AssertCanonicalImageMetadata(androidStagedMetadata, expectedSentName);
        android.Tap(options.App("Chat.Send"));

        var preview = Require(windows.WaitForOneNewAutomationId(
            "DesktopWorkspace.DirectImagePreview", previousWindowsImages,
            TimeSpan.FromSeconds(60)), "DesktopWorkspace.DirectImagePreview");
        var metadata = preview.Properties.Name.ValueOrDefault ?? string.Empty;
        AssertCanonicalImageMetadata(metadata, expectedSentName);
        Assert.Equal(androidStagedMetadata, metadata);

        var previousAndroidImages = android.CountResourceId(options.App("Chat.ImagePreview"));
        var previousAndroidImageMetadata = android.CountResourceId(
            options.App("Chat.ImageMetadata"));
        windows.ActivateExact(Require(windows.WaitForAutomationId(
            "DesktopWorkspace.DirectAttach", TimeSpan.FromSeconds(15)),
            "DesktopWorkspace.DirectAttach"));
        windows.ActivateExact(Require(windows.WaitForAutomationId(
            "DesktopWorkspace.AttachmentPickPhoto", TimeSpan.FromSeconds(15)),
            "DesktopWorkspace.AttachmentPickPhoto"));
        windows.ChooseSingleFileFromOwnedPicker(options.ImageFixturePath,
            TimeSpan.FromSeconds(20));
        Require(windows.WaitForAutomationIdWithName(
            "DesktopWorkspace.DirectStagedAttachmentFilename", expectedSentName,
            TimeSpan.FromSeconds(30)), "DesktopWorkspace.DirectStagedAttachmentFilename");
        var windowsStagedMetadata = Require(windows.WaitForAutomationId(
            "DesktopWorkspace.DirectStagedAttachmentMetadata", TimeSpan.FromSeconds(15)),
            "DesktopWorkspace.DirectStagedAttachmentMetadata")
            .Properties.Name.ValueOrDefault ?? string.Empty;
        AssertCanonicalImageMetadata(windowsStagedMetadata, expectedSentName);
        windows.ActivateExact(Require(windows.WaitForAutomationId(
            "DesktopWorkspace.DirectSend", TimeSpan.FromSeconds(15)),
            "DesktopWorkspace.DirectSend"));

        _ = android.WaitForOneNewResourceId(options.App("Chat.ImagePreview"),
            previousAndroidImages, TimeSpan.FromSeconds(60));
        var androidMetadata = android.WaitForOneNewResourceId(
            options.App("Chat.ImageMetadata"), previousAndroidImageMetadata,
            TimeSpan.FromSeconds(15)).AccessibleText;
        AssertCanonicalImageMetadata(androidMetadata, expectedSentName);
        Assert.Equal(windowsStagedMetadata, androidMetadata);
    }

    private static void AssertCanonicalImageMetadata(string value, string expectedFileName)
    {
        var metadata = StrictCrossPlatformContracts.ParseCanonicalImageMetadata(value);
        Assert.Equal(expectedFileName, metadata.FileName);
        Assert.Equal("image/jpeg", metadata.MimeType);
        Assert.True(metadata.SizeBytes > 0);
        Assert.Equal(1, metadata.Width);
        Assert.Equal(1, metadata.Height);
        Assert.Equal(value, metadata.ToString());
    }

    private static string ExpectedAttachmentMetadata(string fixturePath)
    {
        var size = new FileInfo(fixturePath).Length;
        var kind = string.Equals(Path.GetExtension(fixturePath), ".pdf",
            StringComparison.OrdinalIgnoreCase) ? "PDF" : "Файл";
        var formatted = size < 1024
            ? $"{size} Б"
            : size < 1024 * 1024
                ? $"{size / 1024d:0.#} КБ"
                : $"{size / 1024d / 1024d:0.#} МБ";
        return $"{kind} · {formatted}";
    }

    private static VoiceMatrixSnapshot ExchangeVoiceMessagesBothDirections(
        AndroidUiautomatorClient android,
        WindowsUiSmokeTests.WindowsUiTestSession windows,
        CrossPlatformOptions options,
        IReadOnlySet<string> androidBaseline,
        IReadOnlySet<string> windowsBaseline)
    {
        var androidExpectedAfterFirst = checked(androidBaseline.Count + 1);
        var windowsExpectedAfterFirst = checked(windowsBaseline.Count + 1);
        android.Hold(options.App("Chat.Voice"), TimeSpan.FromSeconds(4));
        android.WaitForExactResourceCount(options.App("Chat.VoicePlayButton"),
            androidExpectedAfterFirst, TimeSpan.FromSeconds(30));
        var windowsReceived = Require(windows.WaitForOneNewAutomationId(
            "DesktopWorkspace.DirectVoicePlay", windowsBaseline.Count,
            TimeSpan.FromSeconds(60)),
            "DesktopWorkspace.DirectVoicePlay");
        var firstVoiceId = windowsReceived.Properties.Name.ValueOrDefault ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(firstVoiceId));
        Assert.Contains(firstVoiceId, android.SnapshotAccessibleTextSet(
            options.App("Chat.VoicePlayButton")));
        windows.ActivateExact(windowsReceived);
        Require(windows.WaitForAutomationIdWithName("PhysicalE2E.VoicePlaybackState",
            "playing", TimeSpan.FromSeconds(10)), "PhysicalE2E.VoicePlaybackState:playing");
        Require(windows.WaitForAutomationIdWithName("PhysicalE2E.VoicePlaybackState",
            "completed", TimeSpan.FromSeconds(20)), "PhysicalE2E.VoicePlaybackState:completed");

        var voiceButton = Require(windows.WaitForAutomationId(
            "DesktopWorkspace.DirectVoice", TimeSpan.FromSeconds(15)),
            "DesktopWorkspace.DirectVoice");
        windows.HoldExact(voiceButton, TimeSpan.FromSeconds(4));
        var androidReceived = android.WaitForOneNewResourceId(
            options.App("Chat.VoicePlayButton"), androidExpectedAfterFirst,
            TimeSpan.FromSeconds(60));
        var secondVoiceId = androidReceived.AccessibleText;
        Assert.False(string.IsNullOrWhiteSpace(secondVoiceId));
        Assert.NotEqual(firstVoiceId, secondVoiceId);
        windows.WaitForExactAutomationIdCount("DesktopWorkspace.DirectVoicePlay",
            checked(windowsBaseline.Count + 2), TimeSpan.FromSeconds(30));
        android.Tap(androidReceived);
        android.WaitForText(options.App("PhysicalE2E.VoicePlaybackState"),
            "playing", TimeSpan.FromSeconds(10));
        android.WaitForText(options.App("PhysicalE2E.VoicePlaybackState"),
            "completed", TimeSpan.FromSeconds(20));

        var androidExpected = androidBaseline.Append(firstVoiceId).Append(secondVoiceId)
            .ToHashSet(StringComparer.Ordinal);
        var windowsExpected = windowsBaseline.Append(firstVoiceId).Append(secondVoiceId)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(checked(androidBaseline.Count + 2), androidExpected.Count);
        Assert.Equal(checked(windowsBaseline.Count + 2), windowsExpected.Count);
        android.WaitForExactAccessibleTextSet(options.App("Chat.VoicePlayButton"),
            androidExpected, TimeSpan.FromSeconds(15));
        windows.WaitForExactAutomationIdNameSet("DesktopWorkspace.DirectVoicePlay",
            windowsExpected, TimeSpan.FromSeconds(15));
        return new VoiceMatrixSnapshot(androidExpected, windowsExpected);
    }

    private sealed record VoiceMatrixSnapshot(
        IReadOnlySet<string> AndroidExpectedAfterPhase,
        IReadOnlySet<string> WindowsExpectedAfterPhase);

    private static void StageAndSendAndroidAttachment(AndroidUiautomatorClient android, CrossPlatformOptions options, string marker)
    {
        android.Tap(options.App("Chat.Attach"));
        android.Tap(options.App("Chat.PickFile"));
        // DocumentsUI opens on Recent, where the freshly pushed fixture is
        // visible. Select the exact filename/resource-id pair; item_root is
        // ambiguous because every visible file card uses it.
        android.TapExactResourceIdWithExactText(options.PickerFileResourceId, marker);
        if (options.PickerConfirmResourceId is not null)
        {
            android.Tap(options.PickerConfirmResourceId);
        }
        android.WaitForText(options.App("Chat.StagedAttachmentFilename"), marker, TimeSpan.FromSeconds(30));
        android.Tap(options.App("Chat.Send"));
    }

    private static void SaveOpenAndVerifyWindowsAttachment(WindowsUiSmokeTests.WindowsUiTestSession windows, string downloadsDirectory, ICollection<string> createdDownloads, string marker, string expectedSha256, bool verifyOpen)
    {
        var attachment = Require(windows.WaitForAutomationIdWithName("DesktopWorkspace.DirectAttachmentFilename", marker, TimeSpan.FromSeconds(45)), "DesktopWorkspace.DirectAttachmentFilename");
        if (verifyOpen)
        {
            windows.RequestContextMenuOnAncestor(attachment, "DesktopWorkspace.DirectMessageBubble");
            windows.ActivateExact(Require(windows.WaitForAutomationId("DesktopWorkspace.AttachmentOpen", TimeSpan.FromSeconds(15)), "DesktopWorkspace.AttachmentOpen"));
            // Open may replace the attachment menu. Re-select the same exact correlated filename before Save.
            attachment = Require(windows.WaitForAutomationIdWithName("DesktopWorkspace.DirectAttachmentFilename", marker, TimeSpan.FromSeconds(15)), "DesktopWorkspace.DirectAttachmentFilename");
        }
        windows.RequestContextMenuOnAncestor(attachment, "DesktopWorkspace.DirectMessageBubble");
        var before = StrictCrossPlatformContracts.SnapshotDownloads(downloadsDirectory);
        windows.ActivateExact(Require(windows.WaitForAutomationId("DesktopWorkspace.AttachmentSave", TimeSpan.FromSeconds(15)), "DesktopWorkspace.AttachmentSave"));
        var saved = before.WaitForNewCorrelatedFile(marker, TimeSpan.FromSeconds(30), createdDownloads);
        Assert.Equal(expectedSha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(saved))));
    }

    private static void ReDownloadAndVerifyWindowsAttachment(WindowsUiSmokeTests.WindowsUiTestSession windows, string downloadsDirectory, ICollection<string> createdDownloads, string marker, string expectedSha256)
    {
        var attachment = Require(windows.WaitForAutomationIdWithName("DesktopWorkspace.DirectAttachmentFilename", marker, TimeSpan.FromSeconds(30)), "DesktopWorkspace.DirectAttachmentFilename");
        windows.RequestContextMenuOnAncestor(attachment, "DesktopWorkspace.DirectMessageBubble");
        var before = StrictCrossPlatformContracts.SnapshotDownloads(downloadsDirectory);
        windows.ActivateExact(Require(windows.WaitForAutomationId("DesktopWorkspace.AttachmentSave", TimeSpan.FromSeconds(15)), "DesktopWorkspace.AttachmentSave"));
        var saved = before.WaitForNewCorrelatedFile(marker, TimeSpan.FromSeconds(30), createdDownloads);
        Assert.NotEqual(createdDownloads.First(), saved);
        Assert.Equal(expectedSha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(saved))));
    }

    private static void WaitForWindowsText(WindowsUiSmokeTests.WindowsUiTestSession windows, string automationId, string text) =>
        Assert.NotNull(windows.WaitForAutomationIdWithName(automationId, text, TimeSpan.FromSeconds(45)));

    private static AutomationElement Require(AutomationElement? element, string selector) => element ?? throw new InvalidOperationException($"Required AutomationId was not found: {selector}.");
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class StrictCrossPlatformUiFactAttribute : FactAttribute
{
    public StrictCrossPlatformUiFactAttribute()
    {
        Skip = CrossPlatformOptions.NotRunReason();
        if (Skip is null) Skip = WindowsDesktopGate.NotRunReason();
    }
}

internal sealed class CrossPlatformOptions
{
    private static readonly string[] RequiredAppRoles =
    [
        "Startup.Status", "Welcome.DisplayName", "Welcome.Create", "Conversations.Root", "PhysicalE2E.RuntimeReadyMarker", "Conversations.ProfileSettings",
        "Conversations.NewConversationTop", "Conversations.ConversationRow", "Settings.SessionId", "Settings.Back",
        "StartConversation.NewMessage", "NewConversation.SessionId", "NewConversation.DisplayName", "NewConversation.Start",
        "NewConversation.Error", "NewConversation.Back", "Chat.Back", "Chat.Draft", "Chat.Send", "Chat.MessageBody", "Chat.MessageBubble",
        "Chat.Attach", "Chat.PickFile", "Chat.PickPhoto", "Chat.StagedAttachmentFilename",
        "Chat.StagedAttachmentMetadata",
        "Chat.AttachmentFilename", "Chat.AttachmentMetadata", "Chat.AttachmentOpen",
        "Chat.AttachmentSave", "Chat.MessageAttachmentOpen", "Chat.MessageAttachmentSave",
        "Chat.ImagePreview", "Chat.ImageMetadata",
        "Chat.DeliveryStatus", "Chat.Retry", "Chat.Voice", "Chat.VoicePlayButton",
        "PhysicalE2E.VoicePlaybackState", "PhysicalE2E.AckCorrelation", "Call.Root", "Call.Status",
        "Call.MediaState", "Call.Microphone", "Call.MicrophoneState", "Call.Hangup"
    ];
    private readonly Dictionary<string, string> androidSelectors;
    private CrossPlatformOptions(Mau2PhysicalPhase phase, string serial, string adbPath, string apkPath, string aaptPath, string apksignerPath, string genericFixturePath, string documentFixturePath, string imageFixturePath, string artifactDirectory, string windowsAppDataRoot, Dictionary<string, string> selectors, string pickerFile, string? pickerConfirm, string fingerprint, string model, string sourceCommit, string windowsExeSha256, string windowsOutputTreeSha256, string releaseInvocationId, string policySha256)
    {
        AndroidSerial = serial; AdbPath = adbPath; ApkPath = apkPath; AaptPath = aaptPath; ApksignerPath = apksignerPath; GenericFixturePath = genericFixturePath; DocumentFixturePath = documentFixturePath; ImageFixturePath = imageFixturePath; ArtifactDirectory = artifactDirectory;
        androidSelectors = selectors; PickerFileResourceId = pickerFile; PickerConfirmResourceId = pickerConfirm;
        Phase = phase; WindowsAppDataRoot = windowsAppDataRoot;
        ResultPath = Path.Combine(artifactDirectory, Mau2PhysicalPhaseContract.GetResultFileName(phase)); InvocationId = Guid.NewGuid().ToString("N"); DeviceFingerprint = fingerprint; DeviceModel = model; SourceCommit = sourceCommit; WindowsExeSha256 = windowsExeSha256; WindowsOutputTreeSha256 = windowsOutputTreeSha256;
        ReleaseInvocationId = releaseInvocationId;
        PolicySha256 = policySha256;
    }
    internal string AndroidSerial { get; }
    internal Mau2PhysicalPhase Phase { get; }
    internal string AdbPath { get; }
    internal string ApkPath { get; }
    internal string AaptPath { get; }
    internal string ApksignerPath { get; }
    internal string GenericFixturePath { get; }
    internal string DocumentFixturePath { get; }
    internal string ImageFixturePath { get; }
    internal string ArtifactDirectory { get; }
    internal string WindowsAppDataRoot { get; }
    internal string PickerFileResourceId { get; }
    internal string? PickerConfirmResourceId { get; }
    internal string ResultPath { get; }
    internal string InvocationId { get; }
    internal string DeviceFingerprint { get; }
    internal string DeviceModel { get; }
    internal string SourceCommit { get; }
    internal string WindowsExeSha256 { get; }
    internal string WindowsOutputTreeSha256 { get; }
    internal string ReleaseInvocationId { get; }
    internal string PolicySha256 { get; }
    internal string App(string role) => androidSelectors.TryGetValue(role, out var id) ? id : throw new InvalidOperationException($"Missing Android selector for {role}.");

    internal static string? NotRunReason()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("DEEP_STRICT_CROSS_PLATFORM_UI"), "1", StringComparison.Ordinal)) return "NOT-RUN: set DEEP_STRICT_CROSS_PLATFORM_UI=1 on an approved unlocked physical Android and Windows UI lab.";
        var required = new[] { "DEEP_E2E_ANDROID_SERIAL", "DEEP_E2E_ADB", "DEEP_E2E_ANDROID_APK", "DEEP_E2E_AAPT", "DEEP_E2E_APKSIGNER", "DEEP_E2E_GENERIC_FIXTURE", "DEEP_E2E_DOCUMENT_FIXTURE", "DEEP_E2E_IMAGE_FIXTURE", "DEEP_E2E_ARTIFACTS", "DEEP_E2E_ANDROID_SELECTORS_JSON", "DEEP_E2E_ANDROID_PICKER_FILE_ID", "DEEP_MAUI_EXE", "DEEP_E2E_APPDATA_ROOT", "DEEP_E2E_BOOTSTRAP", "DEEP_E2E_ANDROID_POLICY", "DEEP_E2E_REPOSITORY_ROOT", "DEEP_MR_X_PUBLIC_KEY_SHA256", "DEEP_RELEASE_INVOCATION_ID", "DEEP_MAU2_E2E_PHASE", "DEEP_MAU2_E2E_RUN_STATE" };
        var missing = required.Where(key => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key))).ToArray();
        return missing.Length == 0 ? null : "NOT-RUN: missing physical lane prerequisites: " + string.Join(", ", missing);
    }

    internal static CrossPlatformOptions Load()
    {
        if (NotRunReason() is { } reason) throw new InvalidOperationException(reason);
        var phase = Mau2PhysicalPhaseContract.LoadRequired();
        Mau2PhysicalPhaseContract.RequireSanitizedRunStatePath();
        if (!string.Equals(Environment.GetEnvironmentVariable("DEEP_TRANSPORT_PROTOCOL"), "authenticated-mau2", StringComparison.Ordinal) ||
            !string.Equals(Environment.GetEnvironmentVariable("DEEP_TRANSPORT_OWNERSHIP"), "user-managed", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Physical MAU2 E2E requires the exact authenticated-mau2/user-managed runtime environment.");
        }
        if (!string.Equals(Environment.GetEnvironmentVariable("DEEP_E2E_BOOTSTRAP"), "live", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Physical cross-platform UI requires DEEP_E2E_BOOTSTRAP=live; stub is not evidence.");
        var selectors = ParseSelectors(Environment.GetEnvironmentVariable("DEEP_E2E_ANDROID_SELECTORS_JSON")!);
        var pickerFile = Environment.GetEnvironmentVariable("DEEP_E2E_ANDROID_PICKER_FILE_ID")!; var pickerConfirm = Environment.GetEnvironmentVariable("DEEP_E2E_ANDROID_PICKER_CONFIRM_ID");
        StrictCrossPlatformContracts.ValidateResourceId(pickerFile, "picker file selector");
        if (!string.IsNullOrWhiteSpace(pickerConfirm))
        {
            StrictCrossPlatformContracts.ValidateResourceId(pickerConfirm, "picker confirm selector");
        }
        else
        {
            pickerConfirm = null;
        }
        var repositoryRootValue = Environment.GetEnvironmentVariable("DEEP_E2E_REPOSITORY_ROOT")!;
        if (!Path.IsPathFullyQualified(repositoryRootValue) || !Directory.Exists(repositoryRootValue))
            throw new InvalidOperationException("The physical runner must provide an absolute existing repository root.");
        var repositoryRoot = Path.GetFullPath(repositoryRootValue);
        var policy = ApprovedCrossPlatformPolicy.Load(
            Environment.GetEnvironmentVariable("DEEP_E2E_ANDROID_POLICY")!,
            repositoryRoot,
            Environment.GetEnvironmentVariable("DEEP_MR_X_PUBLIC_KEY_SHA256")!);
        if (!string.Equals(Environment.GetEnvironmentVariable("DEEP_E2E_ANDROID_SERIAL"), policy.Device.Serial, StringComparison.Ordinal)) throw new InvalidOperationException("Configured Android serial does not match approved inventory.");
        var apk = RequirePinnedPath("DEEP_E2E_ANDROID_APK", policy.Apk.Path); var genericFixture = RequireAbsoluteFile("DEEP_E2E_GENERIC_FIXTURE"); var documentFixture = RequireAbsoluteFile("DEEP_E2E_DOCUMENT_FIXTURE"); var imageFixture = RequireAbsoluteFile("DEEP_E2E_IMAGE_FIXTURE"); var adb = RequirePinnedPath("DEEP_E2E_ADB", policy.Adb.Path); var aapt = RequirePinnedPath("DEEP_E2E_AAPT", policy.Aapt.Path); var apksigner = RequirePinnedPath("DEEP_E2E_APKSIGNER", policy.Apksigner.Path); var artifacts = Path.GetFullPath(Environment.GetEnvironmentVariable("DEEP_E2E_ARTIFACTS")!);
        var windowsExe = Environment.GetEnvironmentVariable("DEEP_MAUI_EXE")!;
        var appDataRoot = Environment.GetEnvironmentVariable("DEEP_E2E_APPDATA_ROOT")!;
        if (!Path.IsPathFullyQualified(windowsExe) || !File.Exists(windowsExe) || !Path.IsPathFullyQualified(appDataRoot) || !string.Equals(Path.GetFullPath(windowsExe), policy.WindowsExePath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Configured Windows executable or app-data root is invalid.");
        StrictCrossPlatformContracts.RequirePinnedFile(windowsExe, policy.WindowsExeSha256, "Windows executable");
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(windowsExe)), policy.WindowsOutputDirectoryPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Configured Windows executable is outside the signed output tree.");
        StrictCrossPlatformContracts.RequirePinnedTree(policy.WindowsOutputDirectoryPath, policy.WindowsOutputTreeSha256, "Windows output tree");
        var windowsHash = StrictCrossPlatformContracts.Sha256File(windowsExe);
        var windowsOutputTreeHash = StrictCrossPlatformContracts.Sha256Tree(policy.WindowsOutputDirectoryPath);
        Directory.CreateDirectory(artifacts);
        var commit = StrictCrossPlatformContracts.RequireCurrentCommit(repositoryRoot, policy.SourceCommit);
        policy.ValidateTool(adb, "adb");
        policy.ValidateTool(aapt, "aapt");
        policy.ValidateTool(apksigner, "apksigner");
        var releaseInvocation = Environment.GetEnvironmentVariable("DEEP_RELEASE_INVOCATION_ID")!;
        if (!System.Text.RegularExpressions.Regex.IsMatch(releaseInvocation, "^[a-f0-9]{32}$")) throw new InvalidOperationException("Release invocation ID must be fresh 32-hex.");
        return new CrossPlatformOptions(phase, policy.Device.Serial, adb, apk, aapt, apksigner, genericFixture, documentFixture, imageFixture, artifacts, Path.GetFullPath(appDataRoot), selectors, pickerFile, pickerConfirm, policy.Device.Fingerprint, policy.Device.Model, commit, windowsHash, windowsOutputTreeHash, releaseInvocation, policy.PolicySha256);
    }
    internal StrictCrossPlatformContracts.ApkMetadata ReadAndValidateApkMetadata()
    {
        var policy = ApprovedCrossPlatformPolicy.Current ?? throw new InvalidOperationException("Approved policy was not loaded.");
        var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(ApkPath)));
        if (new FileInfo(ApkPath).Length != policy.Apk.SizeBytes || !string.Equals(sha, policy.Apk.Sha256, StringComparison.Ordinal)) throw new InvalidOperationException("Selected APK does not match the signed size/SHA-256.");
        var badging = AndroidUiautomatorClient.Run(AaptPath, ["dump", "badging", ApkPath]);
        if (badging.ExitCode != 0) throw new InvalidOperationException("aapt failed to inspect the configured APK.");
        var metadata = StrictCrossPlatformContracts.ApkMetadata.ParseAaptBadging(badging.Output, sha);
        if (!string.Equals(metadata.PackageName, StrictCrossPlatformContracts.AndroidPackage, StringComparison.Ordinal) || metadata.VersionCode != policy.Apk.VersionCode || metadata.VersionName != policy.Apk.VersionName) throw new InvalidOperationException("APK package/version does not match approved inventory.");
        var signer = AndroidUiautomatorClient.Run(ApksignerPath, ["verify", "--print-certs", ApkPath]);
        if (signer.ExitCode != 0) throw new InvalidOperationException("apksigner could not verify the configured APK.");
        var digest = System.Text.RegularExpressions.Regex.Match(signer.Output, "SHA-256[^:]*digest:\\s*(?<digest>[A-Fa-f0-9:]{64,95})", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!digest.Success) throw new InvalidOperationException("apksigner did not emit a certificate SHA-256 digest.");
        var actual = digest.Groups["digest"].Value.Replace(":", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        if (!string.Equals(actual, policy.Apk.SigningDigest, StringComparison.Ordinal)) throw new InvalidOperationException("APK signer does not match approved inventory.");
        return metadata.WithSigningDigest(actual);
    }

    private static string RequireAbsoluteFile(string key) { var path = Environment.GetEnvironmentVariable(key)!; if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new InvalidOperationException($"{key} must be an existing absolute path."); return Path.GetFullPath(path); }
    private static string RequirePinnedPath(string key, string approvedPath) { var path = RequireAbsoluteFile(key); if (!string.Equals(path, Path.GetFullPath(approvedPath), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"{key} is not the approved exact path."); return path; }
    internal string ResolveProductionDownloadsDirectory() =>
        ResolveProductionDownloadsDirectoryAsync().GetAwaiter().GetResult().Path;

    private static async Task<Windows.Storage.StorageFolder> ResolveProductionDownloadsDirectoryAsync()
    {
        const string folderName = "Deep";
        var downloadsPath = Windows.Storage.UserDataPaths.GetDefault().Downloads;
        if (string.IsNullOrWhiteSpace(downloadsPath))
            throw new IOException("The Windows downloads directory is unavailable.");
        var downloads = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(downloadsPath);
        var existing = await downloads.TryGetItemAsync(folderName);
        if (existing is Windows.Storage.StorageFolder folder) return folder;
        if (existing is not null)
            throw new IOException("The Deep downloads destination is not a directory.");
        try
        {
            return await downloads.CreateFolderAsync(
                folderName,
                Windows.Storage.CreationCollisionOption.FailIfExists);
        }
        catch (Exception creationFailure)
        {
            var raced = await downloads.TryGetItemAsync(folderName);
            return raced as Windows.Storage.StorageFolder
                ?? throw new IOException(
                    "The Deep downloads directory could not be created or reopened.",
                    creationFailure);
        }
    }
    private static Dictionary<string, string> ParseSelectors(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json); if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) throw new InvalidOperationException("Android selector JSON must be an object.");
        var items = doc.RootElement.EnumerateObject().ToArray();
        if (items.Length != RequiredAppRoles.Length || items.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != items.Length || !items.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(RequiredAppRoles.OrderBy(x => x, StringComparer.Ordinal))) throw new InvalidOperationException("Android selector JSON must contain exactly the canonical role key set with no duplicates or extras.");
        var map = new Dictionary<string, string>(StringComparer.Ordinal); foreach (var item in items) { if (item.Value.ValueKind != System.Text.Json.JsonValueKind.String) throw new InvalidOperationException("Android selector values must be literal strings."); var value = item.Value.GetString()!; StrictCrossPlatformContracts.ValidateResourceId(value, "Android selector"); if (!value.StartsWith(StrictCrossPlatformContracts.AndroidPackage + ":id/", StringComparison.Ordinal) || !map.TryAdd(item.Name, value)) throw new InvalidOperationException("Android selector is outside the E2E package or duplicated."); }
        if (map.Values.Distinct(StringComparer.Ordinal).Count() != map.Count) throw new InvalidOperationException("Android selector literals must be unique."); return map;
    }
}

internal sealed class ApprovedCrossPlatformPolicy
{
    internal sealed record ToolPin(string Path, string Sha256, string Version, string[] VersionArguments);
    internal sealed record ApkPin(string Path, long SizeBytes, string Sha256, string VersionCode, string VersionName, string SigningDigest);
    internal sealed record DevicePin(string Serial, string Fingerprint, string Model, string Product, string Hardware, int Sdk, string Characteristics);

    private ApprovedCrossPlatformPolicy(string sourceCommit, string policyId, string approvalReceiptSha256, string mrXPublicKeySha256, string windowsExePath, string windowsExeSha256, string windowsOutputDirectoryPath, string windowsOutputTreeSha256, string policySha256, ToolPin adb, ToolPin aapt, ToolPin apksigner, ApkPin apk, DevicePin device)
    {
        SourceCommit = sourceCommit; PolicyId = policyId; ApprovalReceiptSha256 = approvalReceiptSha256; MrXPublicKeySha256 = mrXPublicKeySha256; WindowsExePath = windowsExePath; WindowsExeSha256 = windowsExeSha256; WindowsOutputDirectoryPath = windowsOutputDirectoryPath; WindowsOutputTreeSha256 = windowsOutputTreeSha256; PolicySha256 = policySha256; Adb = adb; Aapt = aapt; Apksigner = apksigner; Apk = apk; Device = device;
    }

    internal static ApprovedCrossPlatformPolicy? Current { get; private set; }
    internal string SourceCommit { get; }
    internal string PolicyId { get; }
    internal string ApprovalReceiptSha256 { get; }
    internal string MrXPublicKeySha256 { get; }
    internal string WindowsExePath { get; }
    internal string WindowsExeSha256 { get; }
    internal string WindowsOutputDirectoryPath { get; }
    internal string WindowsOutputTreeSha256 { get; }
    internal string PolicySha256 { get; }
    internal ToolPin Adb { get; }
    internal ToolPin Aapt { get; }
    internal ToolPin Apksigner { get; }
    internal ApkPin Apk { get; }
    internal DevicePin Device { get; }

    internal static ApprovedCrossPlatformPolicy Load(string path, string repositoryRoot, string mrXPublicKeySha256)
    {
        var expectedPolicyPath = Path.Combine(repositoryRoot, ".secrets", "android-lab", "approved-policy.json");
        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(expectedPolicyPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Cross-platform evidence requires the established protected Android lab policy path.");
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(mrXPublicKeySha256, "^[a-f0-9]{64}$") || mrXPublicKeySha256.All(character => character == '0'))
        {
            throw new InvalidOperationException("A nonzero externally pinned Mr. X public-key SHA-256 is required.");
        }
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.GetProperty("schema").GetString() != "deep.survival.android-lab-policy.v1" ||
            !root.GetProperty("provisioned").GetBoolean() ||
            root.GetProperty("synthetic").GetBoolean())
        {
            throw new InvalidOperationException("Cross-platform evidence requires a provisioned non-synthetic Android lab policy.");
        }

        var approval = root.GetProperty("approval");
        var signature = root.GetProperty("signature");
        if (approval.GetProperty("state").GetString() != "approved" ||
            approval.GetProperty("approvedBy").GetString() != "Mr. X" ||
            signature.GetProperty("algorithm").GetString() != "Ed25519" ||
            signature.GetProperty("publicKeySha256").GetString() != mrXPublicKeySha256)
        {
            throw new InvalidOperationException("Android lab inventory lacks exact Mr. X approval/signature bindings.");
        }
        var receiptPath = ResolveProtectedPath(repositoryRoot, approval.GetProperty("receiptRelativePath").GetString()!);
        var publicKeyPath = ResolveProtectedPath(repositoryRoot, signature.GetProperty("publicKeyRelativePath").GetString()!);
        var signaturePath = ResolveProtectedPath(repositoryRoot, signature.GetProperty("signatureRelativePath").GetString()!);
        var payloadPath = ResolveProtectedPath(repositoryRoot, signature.GetProperty("signedPayloadRelativePath").GetString()!);
        StrictCrossPlatformContracts.RequirePinnedFile(receiptPath, approval.GetProperty("receiptSha256").GetString()!, "Mr. X approval receipt");
        StrictCrossPlatformContracts.RequirePinnedFile(publicKeyPath, mrXPublicKeySha256, "Mr. X public key");
        StrictCrossPlatformContracts.RequirePinnedFile(payloadPath, signature.GetProperty("signedPayloadSha256").GetString()!, "Mr. X signed payload");
        if (!File.Exists(signaturePath)) throw new InvalidOperationException("Mr. X detached signature is absent.");
        using var payloadDocument = System.Text.Json.JsonDocument.Parse(File.ReadAllText(payloadPath));
        var payload = payloadDocument.RootElement;
        foreach (var scalar in new[] { "schema", "provisioned", "synthetic", "sourceCommitSha", "policyId" })
        {
            if (root.GetProperty(scalar).GetRawText() != payload.GetProperty(scalar).GetRawText()) throw new InvalidOperationException($"Signed payload does not bind {scalar}.");
        }
        foreach (var section in new[] { "approval", "tools", "device", "application", "crossPlatform" })
        {
            var policyNode = System.Text.Json.Nodes.JsonNode.Parse(root.GetProperty(section).GetRawText());
            var payloadNode = System.Text.Json.Nodes.JsonNode.Parse(payload.GetProperty(section).GetRawText());
            if (!System.Text.Json.Nodes.JsonNode.DeepEquals(policyNode, payloadNode)) throw new InvalidOperationException($"Signed payload does not bind {section}.");
        }
        VerifySignature(repositoryRoot, publicKeyPath, signaturePath, payloadPath);

        var tools = root.GetProperty("tools");
        ToolPin ReadTool(string name)
        {
            var value = tools.GetProperty(name);
            return new ToolPin(
                ResolveProtectedPath(repositoryRoot, value.GetProperty("relativePath").GetString()!),
                value.GetProperty("sha256").GetString()!,
                value.GetProperty("version").GetString()!,
                value.GetProperty("versionArguments").EnumerateArray().Select(x => x.GetString()!).ToArray());
        }

        var apk = root.GetProperty("application");
        var device = root.GetProperty("device");
        var crossPlatform = root.GetProperty("crossPlatform");
        if (apk.GetProperty("packageId").GetString() != StrictCrossPlatformContracts.AndroidPackage ||
            device.GetProperty("kernelQemu").GetString() != "0" ||
            device.GetProperty("class").GetString() != "physical-managed-dedicated" ||
            !device.GetProperty("dedicated").GetBoolean() ||
            device.GetProperty("inventoryState").GetString() != "approved" ||
            device.GetProperty("inventoryApprovedBy").GetString() != "Mr. X" ||
            device.GetProperty("inventoryApprovalReceiptSha256").GetString() != approval.GetProperty("receiptSha256").GetString())
        {
            throw new InvalidOperationException("Signed application/device inventory is not the approved physical dedicated contract.");
        }
        var parsed = new ApprovedCrossPlatformPolicy(
            root.GetProperty("sourceCommitSha").GetString()!,
            root.GetProperty("policyId").GetString()!,
            approval.GetProperty("receiptSha256").GetString()!,
            mrXPublicKeySha256,
            ResolveRepositoryPath(repositoryRoot, crossPlatform.GetProperty("windowsExecutableRelativePath").GetString()!),
            crossPlatform.GetProperty("windowsExecutableSha256").GetString()!,
            ResolveRepositoryPath(repositoryRoot, crossPlatform.GetProperty("windowsOutputDirectoryRelativePath").GetString()!),
            crossPlatform.GetProperty("windowsOutputTreeSha256").GetString()!,
            StrictCrossPlatformContracts.Sha256File(path),
            ReadTool("adb"),
            ReadTool("aapt"),
            ReadTool("apksigner"),
            new ApkPin(
                ResolveRepositoryPath(repositoryRoot, apk.GetProperty("apkRelativePath").GetString()!),
                apk.GetProperty("apkSizeBytes").GetInt64(),
                apk.GetProperty("apkSha256").GetString()!,
                apk.GetProperty("versionCode").ToString(),
                apk.GetProperty("versionName").GetString()!,
                apk.GetProperty("signingCertificateSha256").GetString()!),
            new DevicePin(
                device.GetProperty("serial").GetString()!,
                device.GetProperty("fingerprint").GetString()!,
                device.GetProperty("model").GetString()!,
                device.GetProperty("product").GetString()!,
                device.GetProperty("hardware").GetString()!,
                device.GetProperty("sdk").GetInt32(),
                device.GetProperty("characteristics").GetString()!));
        parsed.ValidateShape();
        Current = parsed;
        return parsed;
    }

    internal void ValidateTool(string path, string role)
    {
        var pin = role switch { "adb" => Adb, "aapt" => Aapt, "apksigner" => Apksigner, _ => throw new InvalidOperationException("Unknown tool role.") };
        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(pin.Path), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"{role} path is caller-controlled.");
        StrictCrossPlatformContracts.RequirePinnedFile(path, pin.Sha256, role);
        StrictCrossPlatformContracts.RequireExactVersion(
            StrictCrossPlatformContracts.RunBounded(path, pin.VersionArguments, TimeSpan.FromSeconds(15)),
            pin.Version,
            role);
    }

    private void ValidateShape()
    {
        static void Hex(string value, int length, string role)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(value, $"^[a-f0-9]{{{length}}}$") || value.All(character => character == '0')) throw new InvalidOperationException($"{role} is malformed.");
        }
        Hex(SourceCommit, 40, "source commit"); Hex(PolicyId, 64, "policy ID"); Hex(ApprovalReceiptSha256, 64, "approval receipt"); Hex(MrXPublicKeySha256, 64, "Mr. X key"); Hex(WindowsExeSha256, 64, "Windows hash"); Hex(WindowsOutputTreeSha256, 64, "Windows output tree hash"); Hex(Apk.Sha256, 64, "APK hash"); Hex(Apk.SigningDigest, 64, "APK signer");
        foreach (var tool in new[] { Adb, Aapt, Apksigner })
        {
            Hex(tool.Sha256, 64, "tool hash");
            if (!Path.IsPathFullyQualified(tool.Path) || tool.VersionArguments.Length is < 1 or > 4 || string.IsNullOrWhiteSpace(tool.Version)) throw new InvalidOperationException("Approved tool definition is malformed.");
        }
        if (!string.Equals(Path.GetFileName(Adb.Path), "adb.exe", StringComparison.OrdinalIgnoreCase) ||
            !new[] { "aapt.exe", "aapt2.exe" }.Contains(Path.GetFileName(Aapt.Path), StringComparer.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(Apksigner.Path), "apksigner.bat", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Approved tool paths do not match their fixed roles.");
        }
        if (!Path.IsPathFullyQualified(Apk.Path) || Apk.SizeBytes <= 0 || string.IsNullOrWhiteSpace(Apk.VersionCode) || string.IsNullOrWhiteSpace(Apk.VersionName) || Device.Sdk is < 26 or > 100) throw new InvalidOperationException("Approved APK/device definition is malformed.");
        StrictCrossPlatformContracts.RequirePhysicalDeviceInventory(Device.Fingerprint, Device.Model, Device.Product, Device.Hardware, Device.Characteristics, Device.Sdk);
    }

    private static string ResolveProtectedPath(string repositoryRoot, string relative)
    {
        if (relative.Contains('\\') || relative.Split('/').Contains("..") || !relative.StartsWith(".secrets/android-lab/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Signed protected path is not canonical.");
        }
        return ResolveRepositoryPath(repositoryRoot, relative);
    }

    private static string ResolveRepositoryPath(string repositoryRoot, string relative)
    {
        if (Path.IsPathFullyQualified(relative) || relative.Contains('\\') || relative.Split('/').Contains(".."))
        {
            throw new InvalidOperationException("Signed repository path is not canonical.");
        }
        var root = Path.GetFullPath(repositoryRoot);
        var resolved = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Signed path escapes the repository.");
        }
        return resolved;
    }

    private static void VerifySignature(string repositoryRoot, string publicKeyPath, string signaturePath, string payloadPath)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(dotnet) || !Path.IsPathFullyQualified(dotnet) || !File.Exists(dotnet))
        {
            dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        }
        var verifier = Path.Combine(repositoryRoot, "eng", "Deep.AndroidLab.PolicyVerifier", "Deep.AndroidLab.PolicyVerifier.csproj");
        var result = StrictCrossPlatformContracts.RunBounded(
            dotnet,
            ["run", "--project", verifier, "-c", "Release", "--no-build", "--no-restore", "--", "verify", publicKeyPath, signaturePath, payloadPath],
            TimeSpan.FromSeconds(30));
        if (result.ExitCode != 0) throw new InvalidOperationException("Mr. X Ed25519 signature verification failed.");
    }
}

internal sealed class AndroidUiautomatorClient
{
    private readonly CrossPlatformOptions options;
    internal AndroidUiautomatorClient(CrossPlatformOptions options) => this.options = options;
    internal void AssertPhysicalConnectedDevice()
    {
        var policy = ApprovedCrossPlatformPolicy.Current ?? throw new InvalidOperationException("Approved policy was not loaded.");
        RequireSuccess(Adb("get-state"), expectedOutput: "device");
        RequireExactProperty("ro.kernel.qemu", "0");
        RequireExactProperty("ro.build.fingerprint", policy.Device.Fingerprint);
        RequireExactProperty("ro.product.model", policy.Device.Model);
        RequireExactProperty("ro.product.name", policy.Device.Product);
        RequireExactProperty("ro.hardware", policy.Device.Hardware);
        RequireExactProperty("ro.build.version.sdk", policy.Device.Sdk.ToString(System.Globalization.CultureInfo.InvariantCulture));
        RequireExactProperty("ro.build.characteristics", policy.Device.Characteristics);
    }
    internal void AssertInstalledPackage(StrictCrossPlatformContracts.ApkMetadata apk)
    {
        var pathResult = Adb("shell", "pm", "path", StrictCrossPlatformContracts.AndroidPackage);
        var detailsResult = Adb("shell", "dumpsys", "package", StrictCrossPlatformContracts.AndroidPackage);
        RequireSuccess(pathResult); RequireSuccess(detailsResult);
        var path = pathResult.Output;
        var details = detailsResult.Output;
        var installedPath = path.Split('\n').Select(x => x.Trim()).SingleOrDefault(x => x.StartsWith("package:", StringComparison.Ordinal))?.Substring("package:".Length) ?? throw new InvalidOperationException("The E2E package has no unique installed APK path.");
        var installedHashResult = Adb("shell", "sha256sum", installedPath);
        RequireSuccess(installedHashResult);
        var installedSha = installedHashResult.Output.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
        // ReadAndValidateApkMetadata already verifies the exact local bytes with
        // apksigner. Android 12 dumpsys exposes only a short internal signature
        // handle, not the certificate digest; exact installed APK SHA-256 equality
        // therefore carries the verified signer binding without parsing OEM text.
        if (!details.Contains("versionCode=" + apk.VersionCode, StringComparison.Ordinal) ||
            !details.Contains("versionName=" + apk.VersionName, StringComparison.Ordinal) ||
            !string.Equals(installedSha, apk.Sha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Installed E2E package metadata or exact APK identity does not match the apksigner-verified APK.");
    }
    internal void ClearE2ePackageData() => RequireSuccess(Adb("shell", "pm", "clear", StrictCrossPlatformContracts.AndroidPackage));
    internal void ColdStart()
    {
        // Android can restore the last MAUI navigation stack when monkey targets an
        // already-running task. A physical phase must begin at the app root, while
        // preserving all production data, so close the process before every launch.
        ForceStop();
        RequireSuccess(Adb("shell", "monkey", "-p", StrictCrossPlatformContracts.AndroidPackage, "1"));
    }
    internal void ForceStop() => RequireSuccess(Adb("shell", "am", "force-stop", StrictCrossPlatformContracts.AndroidPackage));
    internal void PushFixture(string source, string marker) { var target = "/sdcard/Download/" + marker; RequireSuccess(Adb("push", source, target)); }
    internal void DeletePushedFixture(string marker) => RequireSuccess(Adb("shell", "rm", "-f", "/sdcard/Download/" + marker));
    internal void PushMediaFixture(string source, string marker)
    {
        var target = "/sdcard/Pictures/" + marker;
        RequireSuccess(Adb("push", source, target));
        RequireSuccess(Adb("shell", "am", "broadcast", "-a",
            "android.intent.action.MEDIA_SCANNER_SCAN_FILE", "-d", "file://" + target));
    }
    internal void DeletePushedMediaFixture(string marker) =>
        RequireSuccess(Adb("shell", "rm", "-f", "/sdcard/Pictures/" + marker));
    internal StrictCrossPlatformContracts.AndroidNode WaitForResource(string resourceId, TimeSpan timeout) => Wait(resourceId, null, timeout);
    internal int CountResourceId(string resourceId) =>
        StrictCrossPlatformContracts.FindAllResourceIds(Dump(), resourceId).Length;
    internal IReadOnlySet<string> SnapshotAccessibleTextSet(string resourceId)
    {
        var values = StrictCrossPlatformContracts.FindAllResourceIds(Dump(), resourceId)
            .Select(static node => node.AccessibleText)
            .ToArray();
        if (values.Any(string.IsNullOrWhiteSpace)
            || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new InvalidOperationException(
                "Android correlated resource markers must be non-empty and unique.");
        return values.ToHashSet(StringComparer.Ordinal);
    }
    internal void WaitForExactAccessibleTextSet(
        string resourceId,
        IReadOnlySet<string> expected,
        TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < until)
        {
            try
            {
                var actual = SnapshotAccessibleTextSet(resourceId);
                if (actual.Count > expected.Count)
                    throw new InvalidOperationException(
                        "Android rendered unexpected correlated resource markers.");
                if (actual.SetEquals(expected)) return;
            }
            catch (Exception exception) { last = exception; }
            Thread.Sleep(250);
        }
        throw new InvalidOperationException(
            "Android did not preserve the exact correlated resource-marker set.", last);
    }
    internal void WaitForExactResourceTextCount(
        string resourceId,
        string exactText,
        int expected,
        TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            var count = StrictCrossPlatformContracts.CountResourceIdsWithAccessibleText(
                Dump(), resourceId, exactText);
            if (count == expected) return;
            if (count > expected)
                throw new InvalidOperationException(
                    "Android rendered duplicate exact-text resources.");
            Thread.Sleep(250);
        }
        throw new InvalidOperationException(
            "Android did not render the exact expected text count.");
    }
    internal void WaitForExactResourceCount(string resourceId, int expected, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            var count = CountResourceId(resourceId);
            if (count == expected) return;
            if (count > expected)
                throw new InvalidOperationException("Android rendered duplicate correlated resources.");
            Thread.Sleep(250);
        }
        throw new InvalidOperationException("Android did not render the exact expected resource count.");
    }
    internal StrictCrossPlatformContracts.AndroidNode WaitForOneNewResourceId(
        string resourceId,
        int previousCount,
        TimeSpan timeout)
    {
        if (previousCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(previousCount));
        }

        var until = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < until)
        {
            try
            {
                var matches = StrictCrossPlatformContracts.FindAllResourceIds(
                    Dump(), resourceId);
                if (matches.Length > previousCount + 1)
                {
                    throw new InvalidOperationException(
                        "More than one new exact Android resource appeared.");
                }
                if (matches.Length == previousCount + 1)
                {
                    return matches[^1];
                }
            }
            catch (Exception exception)
            {
                last = exception;
            }
            Thread.Sleep(250);
        }

        throw new InvalidOperationException(
            $"Android resource-id did not increase by exactly one: {resourceId}.",
            last);
    }
    internal void WaitForText(string resourceId, string text, TimeSpan timeout) { var node = WaitByMarker(resourceId, text, timeout); Assert.Contains(text, node.Text, StringComparison.Ordinal); }
    internal StrictCrossPlatformContracts.AndroidNode WaitForCorrelatedDescendant(
        string ancestorResourceId, string correlationResourceId, string correlationText,
        string targetResourceId, TimeSpan timeout, string? targetAccessibleText = null)
    {
        var until = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < until)
        {
            try
            {
                var target = StrictCrossPlatformContracts.FindExactlyOneCorrelatedDescendant(
                    Dump(), ancestorResourceId, correlationResourceId, correlationText,
                    targetResourceId);
                if (targetAccessibleText is not null && !string.Equals(
                        target.AccessibleText, targetAccessibleText, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Correlated Android message target has not reached its exact state.");
                return target;
            }
            catch (Exception exception) { last = exception; }
            Thread.Sleep(250);
        }
        throw new InvalidOperationException("Correlated Android message target was not observed.", last);
    }
    internal StrictCrossPlatformContracts.AndroidNode WaitForMessageBubbleContaining(
        string descendantResourceId, string exactText, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < until)
        {
            try
            {
                return StrictCrossPlatformContracts.FindExactlyOneResourceIdContainingDescendantText(
                    Dump(), options.App("Chat.MessageBubble"), descendantResourceId, exactText);
            }
            catch (Exception exception) { last = exception; }
            Thread.Sleep(250);
        }
        throw new InvalidOperationException("Correlated Android message bubble was not observed.", last);
    }
    internal StrictCrossPlatformContracts.AndroidNode WaitForLastResourceIdContainingDescendant(string resourceId, string descendantResourceId, TimeSpan timeout) { var until = DateTime.UtcNow + timeout; Exception? last = null; while (DateTime.UtcNow < until) { try { return StrictCrossPlatformContracts.FindLastResourceIdContainingDescendant(Dump(), resourceId, descendantResourceId); } catch (Exception ex) { last = ex; } Thread.Sleep(250); } throw new InvalidOperationException($"Last Android {resourceId} did not contain {descendantResourceId}.", last); }
    internal StrictCrossPlatformContracts.AndroidNode? FindOptional(string resourceId) => StrictCrossPlatformContracts.FindOptionalResourceId(Dump(), resourceId);
    internal string WaitForExactlyOneResource(IReadOnlyList<string> resourceIds, TimeSpan timeout)
    {
        if (resourceIds.Count == 0 || resourceIds.Any(string.IsNullOrWhiteSpace) ||
            resourceIds.Distinct(StringComparer.Ordinal).Count() != resourceIds.Count)
        {
            throw new InvalidOperationException("Android provisioning resource IDs must be nonempty and distinct.");
        }
        var until = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < until)
        {
            string[] present;
            try
            {
                var hierarchy = Dump();
                present = resourceIds
                    .Where(resourceId => StrictCrossPlatformContracts.FindOptionalResourceId(
                        hierarchy, resourceId) is not null)
                    .ToArray();
            }
            catch (Exception exception)
            {
                last = exception;
                Thread.Sleep(250);
                continue;
            }
            if (present.Length == 1) return present[0];
            if (present.Length > 1)
            {
                throw new InvalidOperationException(
                    "Android exposed more than one mutually exclusive identity-provisioning surface.");
            }
            Thread.Sleep(250);
        }
        throw new InvalidOperationException(
            "Android did not expose one closed identity-provisioning surface.", last);
    }
    internal void Tap(string resourceId) { var node = WaitForResource(resourceId, TimeSpan.FromSeconds(15)); var point = node.Bounds.Center; RequireSuccess(Adb("shell", "input", "tap", point.X.ToString(System.Globalization.CultureInfo.InvariantCulture), point.Y.ToString(System.Globalization.CultureInfo.InvariantCulture))); }
    internal void Tap(StrictCrossPlatformContracts.AndroidNode node) { var point = node.Bounds.Center; RequireSuccess(Adb("shell", "input", "tap", point.X.ToString(System.Globalization.CultureInfo.InvariantCulture), point.Y.ToString(System.Globalization.CultureInfo.InvariantCulture))); }
    internal void Hold(string resourceId, TimeSpan duration) { if (duration < TimeSpan.FromMilliseconds(700) || duration > TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(duration)); var node = WaitForResource(resourceId, TimeSpan.FromSeconds(15)); var point = node.Bounds.Center; RequireSuccess(Adb("shell", "input", "swipe", point.X.ToString(System.Globalization.CultureInfo.InvariantCulture), point.Y.ToString(System.Globalization.CultureInfo.InvariantCulture), point.X.ToString(System.Globalization.CultureInfo.InvariantCulture), point.Y.ToString(System.Globalization.CultureInfo.InvariantCulture), ((int)duration.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture))); }
    internal void Hold(StrictCrossPlatformContracts.AndroidNode node, TimeSpan duration) { if (duration < TimeSpan.FromMilliseconds(700) || duration > TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(duration)); var point = node.Bounds.Center; RequireSuccess(Adb("shell", "input", "swipe", point.X.ToString(System.Globalization.CultureInfo.InvariantCulture), point.Y.ToString(System.Globalization.CultureInfo.InvariantCulture), point.X.ToString(System.Globalization.CultureInfo.InvariantCulture), point.Y.ToString(System.Globalization.CultureInfo.InvariantCulture), ((int)duration.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture))); }
    internal void TapExactResourceIdWithExactText(string resourceId, string text) { var node = WaitByText(resourceId, text, TimeSpan.FromSeconds(15)); Assert.Equal(text, node.Text); var point = node.Bounds.Center; RequireSuccess(Adb("shell", "input", "tap", point.X.ToString(System.Globalization.CultureInfo.InvariantCulture), point.Y.ToString(System.Globalization.CultureInfo.InvariantCulture))); }
    internal void TapExactResourceIdWithAccessibleText(
        string resourceId,
        string exactText,
        TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < until)
        {
            try
            {
                var matches = StrictCrossPlatformContracts.FindAllResourceIds(
                        Dump(), resourceId)
                    .Where(node => string.Equals(
                        node.AccessibleText, exactText, StringComparison.Ordinal))
                    .ToArray();
                if (matches.Length > 1)
                    throw new InvalidOperationException(
                        "Android resource-id/accessible-text pair was ambiguous.");
                if (matches.Length == 1)
                {
                    var point = matches[0].Bounds.Center;
                    RequireSuccess(Adb("shell", "input", "tap",
                        point.X.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        point.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    return;
                }
            }
            catch (Exception exception) { last = exception; }
            Thread.Sleep(250);
        }
        throw new InvalidOperationException(
            "Android exact resource-id/accessible-text pair was not observed.", last);
    }
    internal bool AllowMicrophonePermissionIfRequested(TimeSpan timeout)
    {
        string[] permissionButtons =
        [
            "com.android.permissioncontroller:id/permission_allow_one_time_button",
            "com.android.permissioncontroller:id/permission_allow_foreground_only_button",
            "com.android.permissioncontroller:id/permission_allow_button"
        ];
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            var hierarchy = Dump();
            var selected = permissionButtons
                .Select(resourceId => StrictCrossPlatformContracts.FindOptionalResourceId(
                    hierarchy,
                    resourceId))
                .FirstOrDefault(static node => node is not null);
            if (selected is not null)
            {
                var point = selected.Bounds.Center;
                RequireSuccess(Adb(
                    "shell",
                    "input",
                    "tap",
                    point.X.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    point.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                return true;
            }
            Thread.Sleep(200);
        }
        return false;
    }
    internal void Type(string resourceId, string value) { Tap(resourceId); RequireSuccess(Adb("shell", "input", "text", value)); }
    internal void DismissKeyboard() { RequireSuccess(Adb("shell", "input", "keyevent", "KEYCODE_BACK")); }
    internal void PressBack() => RequireSuccess(Adb("shell", "input", "keyevent", "KEYCODE_BACK"));
    internal void WaitForExternalActivity(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            var result = Adb("shell", "dumpsys", "activity", "activities");
            RequireSuccess(result);
            var resumed = result.Output.Split('\n')
                .FirstOrDefault(line => line.Contains("ResumedActivity", StringComparison.Ordinal));
            if (resumed is not null && !resumed.Contains(
                    StrictCrossPlatformContracts.AndroidPackage + "/", StringComparison.Ordinal))
                return;
            Thread.Sleep(250);
        }
        throw new InvalidOperationException(
            "Attachment Open did not hand off to an external Android activity.");
    }
    internal IReadOnlySet<string> SnapshotDownloadPaths()
    {
        var result = Adb("shell", "find", "/sdcard/Download", "-type", "f");
        RequireSuccess(result);
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static value => value.Trim())
            .Where(static value => value.StartsWith("/sdcard/Download/", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
    }
    internal void WaitForSavedPlaintext(
        IReadOnlySet<string> before,
        string originalFileName,
        string expectedSha256,
        string artifactDirectory,
        TimeSpan timeout)
    {
        var stem = Path.GetFileNameWithoutExtension(originalFileName);
        var extension = Path.GetExtension(originalFileName);
        var correlated = new System.Text.RegularExpressions.Regex(
            "^" + System.Text.RegularExpressions.Regex.Escape(stem)
            + "(?: \\([2-9][0-9]*\\))?"
            + System.Text.RegularExpressions.Regex.Escape(extension) + "$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            var matches = SnapshotDownloadPaths()
                .Where(path => !before.Contains(path)
                    && correlated.IsMatch(path.Split('/')[^1]))
                .ToArray();
            if (matches.Length > 1)
                throw new InvalidOperationException(
                    "Android Save produced more than one correlated plaintext file.");
            if (matches.Length == 1)
            {
                var local = Path.Combine(artifactDirectory,
                    "android-saved-" + Guid.NewGuid().ToString("N") + extension);
                RequireSuccess(Adb("pull", matches[0], local));
                try
                {
                    Assert.Equal(expectedSha256,
                        StrictCrossPlatformContracts.Sha256File(local));
                }
                finally
                {
                    if (File.Exists(local)) File.Delete(local);
                    RequireSuccess(Adb("shell", "rm", "-f", matches[0]));
                }
                return;
            }
            Thread.Sleep(250);
        }
        throw new InvalidOperationException(
            "Android Save did not create one correlated plaintext file.");
    }
    private StrictCrossPlatformContracts.AndroidNode Wait(string resourceId, string? expectedText, TimeSpan timeout) { var until = DateTime.UtcNow + timeout; Exception? last = null; while (DateTime.UtcNow < until) { try { var node = StrictCrossPlatformContracts.FindExactlyOneResourceId(Dump(), resourceId); if (expectedText is null || string.Equals(node.Text, expectedText, StringComparison.Ordinal)) return node; } catch (Exception ex) { last = ex; } Thread.Sleep(250); } throw new InvalidOperationException($"Required Android resource-id did not reach its expected state: {resourceId}.", last); }
    private StrictCrossPlatformContracts.AndroidNode WaitByText(string resourceId, string text, TimeSpan timeout) { var until = DateTime.UtcNow + timeout; Exception? last = null; while (DateTime.UtcNow < until) { try { return StrictCrossPlatformContracts.FindExactlyOneResourceIdWithText(Dump(), resourceId, text); } catch (Exception ex) { last = ex; } Thread.Sleep(250); } throw new InvalidOperationException($"Required Android resource-id/text pair did not reach its expected state: {resourceId}.", last); }
    private StrictCrossPlatformContracts.AndroidNode WaitByMarker(string resourceId, string text, TimeSpan timeout) { var until = DateTime.UtcNow + timeout; Exception? last = null; while (DateTime.UtcNow < until) { try { return StrictCrossPlatformContracts.FindExactlyOneResourceIdContainingText(Dump(), resourceId, text); } catch (Exception ex) { last = ex; } Thread.Sleep(250); } throw new InvalidOperationException($"Required Android resource-id/text marker did not reach its expected state: {resourceId}.", last); }
    private string Dump() { var result = Adb("exec-out", "uiautomator", "dump", "/dev/tty"); RequireSuccess(result); return StrictCrossPlatformContracts.ExtractExactUiHierarchy(result.Output); }
    private void RequireExactProperty(string name, string expected)
    {
        var result = Adb("shell", "getprop", name);
        RequireSuccess(result);
        if (!string.Equals(result.Output.Trim(), expected, StringComparison.Ordinal)) throw new InvalidOperationException($"Approved device property mismatch: {name}.");
    }
    private StrictCrossPlatformContracts.ProcessResult Adb(params string[] args) => Run(options.AdbPath, ["-s", options.AndroidSerial, .. args]);
    internal static StrictCrossPlatformContracts.ProcessResult Run(string fileName, IReadOnlyList<string> args) => StrictCrossPlatformContracts.RunBounded(fileName, args, TimeSpan.FromSeconds(30));
    private static void RequireSuccess(StrictCrossPlatformContracts.ProcessResult result, string? expectedOutput = null) { if (result.ExitCode != 0 || (expectedOutput is not null && !string.Equals(result.Output.Trim(), expectedOutput, StringComparison.Ordinal))) throw new InvalidOperationException("ADB/aapt command failed without emitting its raw output into evidence."); }
}

internal static class WindowsDesktopGate
{
    private const uint DesktopReadObjects = 0x0001;
    private const int UoiName = 2;

    internal static string? NotRunReason()
    {
        if (!OperatingSystem.IsWindows()) return "NOT-RUN: the physical cross-platform lane requires Windows.";
        var desktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == IntPtr.Zero) return "NOT-RUN: Windows input desktop is unavailable (the session may be locked).";
        try
        {
            var name = new StringBuilder(128);
            if (!GetUserObjectInformation(desktop, UoiName, name, name.Capacity, out _) || !string.Equals(name.ToString(), "Default", StringComparison.Ordinal))
                return "NOT-RUN: Windows is locked or does not expose the interactive Default desktop.";
        }
        finally { CloseDesktop(desktop); }

        return null;
    }

    internal static void EnsureUnlocked()
    {
        if (NotRunReason() is { } reason) throw new InvalidOperationException(reason);
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder info, int length, out int needed);
}
