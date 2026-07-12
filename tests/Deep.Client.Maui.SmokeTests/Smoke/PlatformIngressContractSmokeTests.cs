namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class PlatformIngressContractSmokeTests
{
    [Fact]
    public void IngressQueueUsesBoundedAtomicPersistenceAndForegroundCatchUp()
    {
        var source = ReadWorkspaceFile("src", "Deep.Client.Maui", "Services", "PlatformBoundaryImplementations.cs");
        var background = source[(source.IndexOf("public sealed class MauiBackgroundTaskService", StringComparison.Ordinal))..source.IndexOf("public static class BackgroundSyncBridge", StringComparison.Ordinal)];

        Assert.Contains("FileOptions.WriteThrough", source, StringComparison.Ordinal);
        Assert.Contains("File.Move(temporaryPath, path, overwrite: true)", source, StringComparison.Ordinal);
        Assert.Contains("MaxShareQueueBytes", source, StringComparison.Ordinal);
        Assert.Contains("MaxNotificationQueueBytes", source, StringComparison.Ordinal);
        Assert.Contains("public static IReadOnlyList<T> Read", source, StringComparison.Ordinal);
        Assert.Contains("public static bool Remove", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public static IReadOnlyList<T> Drain", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run", background, StringComparison.Ordinal);
        Assert.Contains("TryPublishForegroundCatchUp", background, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidShareIngressIsNarrowAndRequiresReadGrant()
    {
        var activity = ReadWorkspaceFile("src", "Deep.Client.Maui", "Platforms", "Android", "ShareIngressActivity.cs");
        var manifest = ReadWorkspaceFile("src", "Deep.Client.Maui", "Platforms", "Android", "AndroidManifest.xml");

        Assert.Contains("Intent.ActionSend", activity, StringComparison.Ordinal);
        Assert.Contains("Intent.ActionSendMultiple", activity, StringComparison.Ordinal);
        Assert.Contains("GetParcelableArrayListExtra", activity, StringComparison.Ordinal);
        Assert.Contains("GrantReadUriPermission", activity, StringComparison.Ordinal);
        Assert.Contains("MaxFileBytes", activity, StringComparison.Ordinal);
        Assert.Contains("Exported = true", activity, StringComparison.Ordinal);
        Assert.DoesNotContain("android.intent.action.SEND", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidOutgoingPhotosUseOneMetadataFreeExifOrientationPipeline()
    {
        var transform = ReadWorkspaceFile("src", "Deep.Client.Maui", "Services", "ExifOrientationTransform.cs");
        var orientation = ReadWorkspaceFile("src", "Deep.Client.Maui", "Services", "AndroidExifOrientationNormalizer.cs");
        var transcoder = ReadWorkspaceFile("src", "Deep.Client.Maui", "Services", "AndroidImageTranscoder.cs");
        var avatar = ReadWorkspaceFile("src", "Deep.Client.Maui", "Services", "ProfileAvatarSync.cs");
        var codec = ReadWorkspaceFile("src", "Deep.Client.Maui", "Services", "PlatformBoundaryImplementations.cs");
        var share = ReadWorkspaceFile("src", "Deep.Client.Maui", "Platforms", "Android", "ShareIngressActivity.cs");

        for (var exifValue = 2; exifValue <= 8; exifValue++)
        {
            Assert.Contains($"{exifValue} => new ExifOrientationTransform", transform, StringComparison.Ordinal);
        }

        Assert.Contains("matrix.PostRotate(transform.RotationDegrees)", orientation, StringComparison.Ordinal);
        Assert.Contains("matrix.PostScale(-1, 1)", orientation, StringComparison.Ordinal);
        Assert.Contains("AndroidExifOrientationNormalizer.ReadOrientation(content)", avatar, StringComparison.Ordinal);
        Assert.Contains("AndroidExifOrientationNormalizer.ApplyIfNeeded(decoded, orientation)", avatar, StringComparison.Ordinal);
        var resizeIndex = transcoder.IndexOf("AndroidBitmap.CreateScaledBitmap", StringComparison.Ordinal);
        var decodedDisposeIndex = transcoder.IndexOf("decoded.Dispose()", StringComparison.Ordinal);
        var orientationIndex = transcoder.IndexOf("ApplyIfNeeded(working, orientation)", StringComparison.Ordinal);
        var workingDisposeIndex = transcoder.IndexOf("working.Dispose()", StringComparison.Ordinal);
        var encodeIndex = transcoder.IndexOf("EncodeMetadataFreeJpeg", StringComparison.Ordinal);
        Assert.True(transcoder.IndexOf("ReadOrientation(sourcePath", StringComparison.Ordinal) < resizeIndex);
        Assert.True(resizeIndex < decodedDisposeIndex);
        Assert.True(decodedDisposeIndex < orientationIndex);
        Assert.True(orientationIndex < workingDisposeIndex);
        Assert.True(workingDisposeIndex < encodeIndex);
        Assert.Contains("AndroidImageTranscoder.TranscodeToMetadataFreeJpeg", codec, StringComparison.Ordinal);
        Assert.Contains("MauiMediaCodecService.TranscodeImageAndroid", share, StringComparison.Ordinal);
        Assert.Contains("ShouldTranscodeImage(actualMime)", share, StringComparison.Ordinal);
        Assert.Contains("\"image/heic\"", share, StringComparison.Ordinal);
        Assert.DoesNotContain("actualMime!.StartsWith(\"image/\"", share, StringComparison.Ordinal);
        Assert.Contains("WithJpegExtension(fileName)", share, StringComparison.Ordinal);
        Assert.Contains("new MediaTranscodeRequest(temporaryPath, \"image/jpeg\"", share, StringComparison.Ordinal);
        Assert.Contains("bitmap.Compress", transcoder, StringComparison.Ordinal);
        Assert.DoesNotContain("SetAttribute", transcoder, StringComparison.Ordinal);
    }

    [Fact]
    public void PushIngressPersistsEncryptedWorkBeforeAsyncAuthenticationAndAuthenticatesBeforeSync()
    {
        var android = ReadWorkspaceFile("src", "Deep.Client.Maui", "Platforms", "Android", "DeepFirebaseMessagingService.cs");
        var job = ReadWorkspaceFile("src", "Deep.Client.Maui", "Platforms", "Android", "DeepSyncJobService.cs");
        var notificationCoordinator = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "Services", "IncomingMessageNotificationCoordinator.cs");
        var windowsPush = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "Platforms", "Windows", "WindowsPushNotificationService.cs");
        var ios = ReadWorkspaceFile("src", "Deep.Client.Maui", "Platforms", "iOS", "AppDelegate.cs");
        var handler = ReadWorkspaceFile("src", "Deep.Client.Maui", "Services", "MauiPushNotificationService.cs");
        var replayCache = ReadWorkspaceFile("src", "Deep.Client.Maui", "Services", "PushNotificationReplayCache.cs");

        Assert.DoesNotContain("GetAwaiter().GetResult", android, StringComparison.Ordinal);
        Assert.DoesNotContain("AuthenticateAsync", android, StringComparison.Ordinal);
        Assert.DoesNotContain("MauiBackgroundSyncRunner", android, StringComparison.Ordinal);
        Assert.DoesNotContain("await ", android[android.IndexOf("public override void OnMessageReceived", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains("TryNormalizeEnvelopeData", android, StringComparison.Ordinal);
        Assert.Contains("AndroidBackgroundSyncScheduler.SchedulePush", android, StringComparison.Ordinal);
        Assert.Contains("builder.SetExtras(extras)", job, StringComparison.Ordinal);
        Assert.Contains("PushUnsubscribeRetryBootstrapper", job, StringComparison.Ordinal);
        Assert.Contains("BackgroundSyncBridge.PublishScheduledSync,", job, StringComparison.Ordinal);
        Assert.True(job.IndexOf(".AuthenticateAsync(", StringComparison.Ordinal) <
                    job.IndexOf("MauiBackgroundSyncRunner.SynchronizeDeferredCompletionAsync", StringComparison.Ordinal));
        Assert.True(job.IndexOf("MauiBackgroundSyncRunner.SynchronizeDeferredCompletionAsync", StringComparison.Ordinal) <
                    job.IndexOf("AndroidPushNotificationPresenter.Show", StringComparison.Ordinal));
        Assert.DoesNotContain("synchronization.Inbox.HasUserVisibleMessages", job, StringComparison.Ordinal);
        Assert.Contains("ListPendingIncomingMessageNotificationIdsAsync", job, StringComparison.Ordinal);
        Assert.Contains("MarkIncomingMessageNotificationsPresentedAsync", job, StringComparison.Ordinal);
        Assert.Contains("IncomingMessageNotificationCoordinator", job, StringComparison.Ordinal);
        Assert.Contains("CreateNotificationBatchId", notificationCoordinator, StringComparison.Ordinal);
        Assert.Contains("catch", job[job.IndexOf("MauiBackgroundSyncRunner.SynchronizeDeferredCompletionAsync", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.True(notificationCoordinator.IndexOf("await presentAsync(", StringComparison.Ordinal) <
                    notificationCoordinator.IndexOf("acknowledgeAsync(presentationIds", StringComparison.Ordinal));
        Assert.Contains("while (pending.Count > 0)", notificationCoordinator, StringComparison.Ordinal);
        Assert.Contains("state.ShouldSuppressNotification(item.ConversationId)", notificationCoordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("acknowledgeAsync(suppressedIds", notificationCoordinator, StringComparison.Ordinal);
        Assert.Contains("ExcludedConversations", notificationCoordinator, StringComparison.Ordinal);
        Assert.Contains("rearmPendingWork();", notificationCoordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("shouldPresentMessageNotification", job, StringComparison.Ordinal);
        Assert.True(job.IndexOf("await PresentPendingIncomingMessageNotificationAsync(cancellationToken)", StringComparison.Ordinal) <
                    job.IndexOf("BackgroundSyncBridge.MarkHandled();", StringComparison.Ordinal));
        Assert.Contains("StableNotificationId", android, StringComparison.Ordinal);
        Assert.DoesNotContain("GetHashCode(StringComparison.Ordinal)", android, StringComparison.Ordinal);
        Assert.True(windowsPush.IndexOf(".TrySynchronizeAsync(services", StringComparison.Ordinal) <
                    windowsPush.IndexOf("new IncomingMessageNotificationCoordinator", StringComparison.Ordinal));
        Assert.True(windowsPush.IndexOf("new IncomingMessageNotificationCoordinator", StringComparison.Ordinal) <
                    windowsPush.IndexOf("ShowGenericNotification(notificationId)", StringComparison.Ordinal));

        Assert.Contains("completionHandler(UIBackgroundFetchResult.NoData)", ios, StringComparison.Ordinal);
        Assert.Contains("authentication.Status == PushAuthenticationStatus.Rejected", ios, StringComparison.Ordinal);
        Assert.Contains("PushNotificationCrypto.IsPayloadCurrentlyValid", handler, StringComparison.Ordinal);
        Assert.Contains("MauiPushNotificationReplayStore.TryAccept", handler, StringComparison.Ordinal);
        Assert.True(replayCache.IndexOf("beforePersistAcceptance?.Invoke();", StringComparison.Ordinal) <
                    replayCache.IndexOf("entries.Add(new ReplayEntry", StringComparison.Ordinal));
    }

    [Fact]
    public void ActiveConversationSuppressionUsesSingletonTrackerAndPlatformForegroundLifecycle()
    {
        var tracker = ReadWorkspaceFile(
            "src", "Deep.Client.Maui.Core", "Services", "IActiveConversationTracker.cs");
        var program = ReadWorkspaceFile("src", "Deep.Client.Maui", "MauiProgram.cs");
        var androidActivity = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "Platforms", "Android", "MainActivity.cs");
        var windowsApp = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "Platforms", "Windows", "App.xaml.cs");
        var chatPage = ReadWorkspaceFile("src", "Deep.Client.Maui", "Pages", "ChatPage.xaml.cs");
        var groupChatPage = ReadWorkspaceFile("src", "Deep.Client.Maui", "Pages", "GroupChatPage.xaml.cs");

        Assert.Contains("AddSingleton<IActiveConversationTracker, ActiveConversationTracker>()", program, StringComparison.Ordinal);
        Assert.Contains("lock (gate)", tracker, StringComparison.Ordinal);
        Assert.Contains("activeRegistrationId", tracker, StringComparison.Ordinal);
        Assert.Contains("SetApplicationForeground(true)", androidActivity, StringComparison.Ordinal);
        Assert.Contains("SetApplicationForeground(false)", androidActivity, StringComparison.Ordinal);
        Assert.Contains("WindowActivationState.Deactivated", windowsApp, StringComparison.Ordinal);
        Assert.Contains("SetApplicationForeground(false)", windowsApp, StringComparison.Ordinal);
        Assert.Contains("SetApplicationForeground(true)", windowsApp, StringComparison.Ordinal);
        Assert.Contains("activeConversationTracker.ActivateConversation(conversationId)", chatPage, StringComparison.Ordinal);
        Assert.Contains("ConversationId.ForOneToOne(SessionId.Parse", chatPage, StringComparison.Ordinal);
        Assert.Contains("activeConversationLease?.Dispose()", chatPage, StringComparison.Ordinal);
        Assert.Contains("activeConversationTracker.ActivateConversation(conversationId)", groupChatPage, StringComparison.Ordinal);
        Assert.Contains("ConversationId.Parse(raw.ToString()!)", groupChatPage, StringComparison.Ordinal);
        Assert.Contains("activeConversationLease?.Dispose()", groupChatPage, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidVoiceCaptureCancelsReadLoopBeforeStoppingAudioRecord()
    {
        var recorder = ReadWorkspaceFile("src", "Deep.Client.Maui", "Services", "MauiVoiceMessageRecorder.cs");
        var stopMethod = recorder[recorder.IndexOf("private static async Task StopRecordingAsync", StringComparison.Ordinal)..recorder.IndexOf("private static async Task WriteWavAsync", StringComparison.Ordinal)];

        Assert.True(
            stopMethod.IndexOf("activeCancellation.Cancel();", StringComparison.Ordinal) <
            stopMethod.IndexOf("activeRecorder.Stop();", StringComparison.Ordinal));

        var cancelMethod = recorder[recorder.IndexOf("public async Task CancelAsync", StringComparison.Ordinal)..];
        Assert.True(
            cancelMethod.IndexOf("activeCancellation?.Cancel();", StringComparison.Ordinal) <
            cancelMethod.IndexOf("activeRecorder?.Stop();", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ChatPage.xaml.cs")]
    [InlineData("GroupChatPage.xaml.cs")]
    public void AndroidVoiceGestureUsesOnlyNativeRawTouchPipeline(string pageFile)
    {
        var page = ReadWorkspaceFile("src", "Deep.Client.Maui", "Pages", pageFile);

        Assert.Contains("if (OperatingSystem.IsAndroid())", page, StringComparison.Ordinal);
        Assert.Contains("e.Handled = true;", page, StringComparison.Ordinal);
        Assert.Contains("case MotionEventActions.Cancel:", page, StringComparison.Ordinal);
        Assert.Contains("voiceCancelBySwipe = true;", page, StringComparison.Ordinal);
        Assert.Contains("ApplyVoiceRecordingGestureProgress(1);", page, StringComparison.Ordinal);
        Assert.Contains("if (e.Action == MotionEventActions.Up)", page, StringComparison.Ordinal);
        Assert.Contains("if (e.Action == MotionEventActions.Cancel)", page, StringComparison.Ordinal);
        Assert.Contains("_ = FinishVoiceRecordingGestureAsync();", page, StringComparison.Ordinal);
        Assert.Contains("FinishVoiceRecordingGestureAsync(forceCancel: true)", page, StringComparison.Ordinal);
        Assert.Contains("voiceGestureCompletionGate", page, StringComparison.Ordinal);
        Assert.Contains("voiceButtonPlatformView.Touch -= OnVoiceButtonPlatformTouch", page, StringComparison.Ordinal);
    }

    [Fact]
    public void PlatformPushCapabilitiesMatchImplementedProviders()
    {
        var service = ReadWorkspaceFile("src", "Deep.Client.Maui", "Services", "MauiPushNotificationService.cs");
        var bridge = ReadWorkspaceFile("src", "Deep.Client.Maui", "Services", "PushTokenBridge.cs");
        var windows = ReadWorkspaceFile("src", "Deep.Client.Maui", "Platforms", "Windows", "WindowsPushNotificationService.cs");

        Assert.Contains("#elif IOS", service, StringComparison.Ordinal);
        Assert.DoesNotContain("IOS || MACCATALYST", service, StringComparison.Ordinal);
        Assert.Contains("return \"wns\"", service, StringComparison.Ordinal);
        Assert.Contains("WindowsPushNotificationService.GetChannelUriAsync", service, StringComparison.Ordinal);
        Assert.Contains("PushTokenBridge.WaitForTokenAsync", service, StringComparison.Ordinal);
        Assert.Contains("TaskCompletionSource<string>", bridge, StringComparison.Ordinal);
        Assert.Contains("manager.PushReceived += OnPushReceived", windows, StringComparison.Ordinal);
        Assert.True(windows.IndexOf("manager.PushReceived += OnPushReceived", StringComparison.Ordinal) <
                    windows.IndexOf("manager.Register();", StringComparison.Ordinal));
        Assert.Contains("PushNotificationBackgroundHandler", windows, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidShareActivationDoesNotPerformFileIoOrBlockTheUiThread()
    {
        var activity = ReadWorkspaceFile("src", "Deep.Client.Maui", "Platforms", "Android", "ShareIngressActivity.cs");
        var onCreate = activity[activity.IndexOf("protected override void OnCreate", StringComparison.Ordinal)..activity.IndexOf("private bool TryCreateWorkerIntent", StringComparison.Ordinal)];

        Assert.DoesNotContain("GetAwaiter().GetResult", activity, StringComparison.Ordinal);
        Assert.DoesNotContain("PersistUri(", onCreate, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenInputStream", onCreate, StringComparison.Ordinal);
        Assert.DoesNotContain("File.", onCreate, StringComparison.Ordinal);
        Assert.Contains("StartService", onCreate, StringComparison.Ordinal);
        Assert.Contains("FinishOnMainThread", onCreate, StringComparison.Ordinal);
        Assert.Contains("public sealed class ShareIngressService : Service", activity, StringComparison.Ordinal);
        Assert.Contains("ProcessIntentAsync", activity, StringComparison.Ordinal);
        Assert.DoesNotContain("IntentService", activity, StringComparison.Ordinal);
        Assert.Contains("TryEnqueueFromBackground", activity, StringComparison.Ordinal);
        Assert.Contains("GrantPersistableUriPermission", activity, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidShareIngressKeepsDuplicatePayloadsAndCleansOnlyRejectedOrCompletedCopies()
    {
        var activity = ReadWorkspaceFile("src", "Deep.Client.Maui", "Platforms", "Android", "ShareIngressActivity.cs");
        var queue = ReadWorkspaceFile("src", "Deep.Client.Maui", "Services", "PlatformBoundaryImplementations.cs");

        Assert.Contains("ShareIngressEnqueueResult.AlreadyPending", queue, StringComparison.Ordinal);
        Assert.Contains("DurableIngressEnqueueResult.AlreadyPresent", queue, StringComparison.Ordinal);
        Assert.Contains("return DurableIngressEnqueueResult.AlreadyPresent", queue, StringComparison.Ordinal);
        Assert.Contains("ShareIngressEnqueueResult.AlreadyCompleted or ShareIngressEnqueueResult.Rejected", activity, StringComparison.Ordinal);
        var enqueueBranch = activity[activity.IndexOf("var enqueueResult", StringComparison.Ordinal)..];
        Assert.Contains("payload = null;", enqueueBranch, StringComparison.Ordinal);
        Assert.True(
            enqueueBranch.IndexOf("ShareIngressActivity.DeletePersistedFiles(payload);", StringComparison.Ordinal) <
            enqueueBranch.IndexOf("payload = null;", StringComparison.Ordinal));
    }

    [Fact]
    public void ShellAcknowledgesIngressOnlyAfterSuccessfulApplication()
    {
        var shell = ReadWorkspaceFile("src", "Deep.Client.Maui", "AppShell.xaml.cs");

        Assert.Contains("await ingressGate.WaitAsync();", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("WaitAsync(0)", shell, StringComparison.Ordinal);
        Assert.Contains("NotificationActionBridge.MarkHandled(action)", shell, StringComparison.Ordinal);
        Assert.Contains("MauiShareExtensionBridge.MarkHandled(share)", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("NotificationActionBridge.Publish(action)", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("MauiShareExtensionBridge.EnqueueInBackground(share)", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void LogoutCommitsSharedSignOutBeforePurgingPlatformArtifacts()
    {
        var coordinator = ReadWorkspaceFile("src", "Deep.Client.Maui", "Services", "MauiAccountLogoutCoordinator.cs");
        var settings = ReadWorkspaceFile("src", "Deep.Client.Maui", "Pages", "SettingsPage.xaml.cs");

        Assert.True(
            coordinator.IndexOf("TryUnregisterPushAsync", StringComparison.Ordinal) <
            coordinator.IndexOf("runtime.Accounts.SignOutAsync", StringComparison.Ordinal));
        Assert.True(
            coordinator.IndexOf("runtime.Accounts.SignOutAsync", StringComparison.Ordinal) <
            coordinator.IndexOf("MauiAccountArtifactPurger.PurgeAsync", StringComparison.Ordinal));
        Assert.Contains("SignOutAsync(CancellationToken.None)", coordinator, StringComparison.Ordinal);
        Assert.Contains("TryCleanupAsync", coordinator, StringComparison.Ordinal);
        Assert.Contains("AttachmentOpenService.PurgeCacheAsync", coordinator, StringComparison.Ordinal);
        Assert.Contains("ContactAvatarStore.PurgeAll", coordinator, StringComparison.Ordinal);
        Assert.Contains("catch (Exception exception)", coordinator, StringComparison.Ordinal);
        Assert.Contains("pushRegistration.UnregisterAsync(timeout.Token)", coordinator, StringComparison.Ordinal);
        Assert.Contains("MauiShareExtensionBridge.PurgePending", coordinator, StringComparison.Ordinal);
        Assert.Contains("NotificationActionBridge.Purge", coordinator, StringComparison.Ordinal);
        Assert.Contains("MauiPushNotificationReplayStore.Clear", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("wipe-local-on-next-launch", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void ForegroundPushWorkIsAcknowledgedOnlyAfterSuccessfulSynchronization()
    {
        var page = ReadWorkspaceFile("src", "Deep.Client.Maui", "Pages", "ConversationsPage.xaml.cs");
        var syncPump = page[page.IndexOf("private async Task RunSyncPumpAsync", StringComparison.Ordinal)..page.IndexOf("private async void OnConversationTapped", StringComparison.Ordinal)];

        Assert.Contains("var synchronized = await viewModel.SyncAsync", syncPump, StringComparison.Ordinal);
        Assert.Contains("if (!synchronized)", syncPump, StringComparison.Ordinal);
        Assert.True(
            syncPump.IndexOf("if (!synchronized)", StringComparison.Ordinal) <
            syncPump.IndexOf("BackgroundSyncBridge.MarkHandled();", StringComparison.Ordinal));
    }

    [Fact]
    public void MessagePushDoesNotDisableIncomingCallPolling()
    {
        var page = ReadWorkspaceFile("src", "Deep.Client.Maui", "Pages", "ConversationsPage.xaml.cs");
        var callPolling = page[page.IndexOf("private Task ConfigureIncomingCallPollingAsync", StringComparison.Ordinal)..page.IndexOf("private void ScheduleForegroundCatchUpSync", StringComparison.Ordinal)];

        Assert.Contains("EnsureIncomingCallPolling();", callPolling, StringComparison.Ordinal);
        Assert.DoesNotContain("syncPollingPolicy", callPolling, StringComparison.Ordinal);
        Assert.DoesNotContain("incomingCallTimer?.Stop()", callPolling, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidOutboxRetryIsPeriodicNetworkRequiredAndIdempotentlyScheduled()
    {
        var job = ReadWorkspaceFile("src", "Deep.Client.Maui", "Platforms", "Android", "DeepSyncJobService.cs");
        var regular = job[job.IndexOf("public static void ScheduleRegularRetry", StringComparison.Ordinal)..job.IndexOf("public static void CancelRegularRetry", StringComparison.Ordinal)];

        Assert.Contains("GetPendingJob(RegularRetryJobId)", regular, StringComparison.Ordinal);
        Assert.Contains("SetRequiredNetworkType(NetworkType.Any)", regular, StringComparison.Ordinal);
        Assert.Contains("SetPeriodic", regular, StringComparison.Ordinal);
        Assert.Contains("RegularRetryJobId", regular, StringComparison.Ordinal);
        Assert.Contains("MauiBackgroundSyncRunner.SynchronizeDeferredCompletionAsync", job, StringComparison.Ordinal);
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
