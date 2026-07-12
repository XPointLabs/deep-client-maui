using System.Text;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Features;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Platform;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.ViewModels.Tests.ViewModels;

public sealed class ClientLiveAcceptanceTests
{
    [Fact]
    public async Task ClientViewModels_RunLaunchCriticalFlowThroughLiveLocalInfrastructure_WhenConfigured()
    {
        var storageUrl = Environment.GetEnvironmentVariable("DEEP_STORAGE_URL");
        var routerUrls = Environment.GetEnvironmentVariable("XNODE_URLS");
        var fileUrl = Environment.GetEnvironmentVariable("DEEP_FILE_URL");
        var pushUrl = Environment.GetEnvironmentVariable("DEEP_PUSH_URL");
        var callUrl = Environment.GetEnvironmentVariable("DEEP_CALL_SIGNALING_BASE_URL")
            ?? Environment.GetEnvironmentVariable("DEEP_CALL_SIGNALING_URL");
        if ((string.IsNullOrWhiteSpace(storageUrl) && string.IsNullOrWhiteSpace(routerUrls)) ||
            string.IsNullOrWhiteSpace(fileUrl) ||
            string.IsNullOrWhiteSpace(pushUrl) ||
            string.IsNullOrWhiteSpace(callUrl))
        {
            return;
        }

        var aliceRuntime = CreateRuntime(storageUrl, routerUrls);
        var bobRuntime = CreateRuntime(storageUrl, routerUrls);
        var attachmentFiles = new HttpAttachmentFileTransport(
            new HttpClient(),
            new HttpAttachmentFileTransportOptions(fileUrl));
        var attachmentBytes = Encoding.UTF8.GetBytes($"client-live-attachment-{Guid.NewGuid():N}");

        var aliceOnboarding = new OnboardingViewModel(aliceRuntime) { DisplayName = "Alice Live Client" };
        var bobOnboarding = new OnboardingViewModel(bobRuntime) { DisplayName = "Bob Live Client" };
        await aliceOnboarding.RegisterAsync();
        await bobOnboarding.RegisterAsync();

        Assert.Null(aliceOnboarding.ErrorMessage);
        Assert.Null(bobOnboarding.ErrorMessage);
        Assert.NotNull(aliceOnboarding.Account);
        Assert.NotNull(bobOnboarding.Account);
        Assert.Equal(aliceOnboarding.Account!.SessionId.Value, aliceOnboarding.SessionId);
        Assert.Equal(bobOnboarding.Account!.SessionId.Value, bobOnboarding.SessionId);

        var alicePhrase = await aliceRuntime.Accounts.GetRecoveryPhraseAsync();
        var bobPhrase = await bobRuntime.Accounts.GetRecoveryPhraseAsync();
        var aliceCalls = new ClientCallService(CreateCallService(callUrl, alicePhrase));
        var bobCalls = new ClientCallService(CreateCallService(callUrl, bobPhrase));

        var bobNotifications = new NotificationRegistrationViewModel(new PushRegistrationCoordinator(
            bobRuntime,
            new LocalPushNotificationService("fcm", $"client-live-fcm-{Guid.NewGuid():N}"),
            new HttpPushSubscriptionTransport(new HttpClient(), new HttpPushSubscriptionTransportOptions(pushUrl)),
            bobRuntime.Clock));
        await bobNotifications.RegisterAsync();

        Assert.Null(bobNotifications.ErrorMessage);
        Assert.Equal("fcm", bobNotifications.Provider);
        Assert.False(string.IsNullOrWhiteSpace(bobNotifications.Token));

        var aliceConversations = new ConversationsViewModel(aliceRuntime)
        {
            NewSessionId = bobOnboarding.Account.SessionId.Value,
            NewDisplayName = "Bob"
        };
        var conversation = await aliceConversations.StartConversationFromComposerAsync();

        Assert.Null(aliceConversations.ErrorMessage);
        Assert.NotNull(conversation);
        Assert.Single(aliceConversations.Conversations);
        Assert.Equal(conversation!.Id, aliceConversations.SelectedConversation?.Id);

        var aliceChat = new ChatViewModel(
            aliceRuntime,
            aliceCalls,
            new LiveAttachmentPicker(attachmentFiles, attachmentBytes));
        var bobChat = new ChatViewModel(bobRuntime, bobCalls);
        await aliceChat.OpenOneToOneAsync(aliceOnboarding.Account, bobOnboarding.Account.SessionId, "Bob");
        await bobChat.OpenOneToOneAsync(bobOnboarding.Account, aliceOnboarding.Account.SessionId, "Alice");
        await aliceChat.PickAttachmentsAsync();

        Assert.Null(aliceChat.ErrorMessage);
        Assert.True(aliceChat.HasStagedAttachments);
        Assert.True(aliceChat.StagedAttachments.Single().HasEncryptedPointer);

        var messageBody = $"client-live-message-{Guid.NewGuid():N}";
        aliceChat.Draft = messageBody;
        await aliceChat.SendAsync();
        await WaitForAsync(() => aliceChat.Messages.Any(item => item.Body == messageBody && item.State == MessageDeliveryState.Sent));
        await bobChat.ReceiveAsync();

        Assert.Null(aliceChat.ErrorMessage);
        Assert.Null(bobChat.ErrorMessage);
        Assert.Contains(aliceChat.Messages, item => item.Body == messageBody && item.HasAttachments);
        Assert.Contains(bobChat.Messages, item => item.Body == messageBody && item.HasAttachments);

        var bobMessages = await bobRuntime.Messages.ListConversationMessagesAsync(ConversationId.ForOneToOne(aliceOnboarding.Account.SessionId));
        var received = Assert.Single(bobMessages, item => item.Body == messageBody);
        var attachment = Assert.Single(received.Attachments);
        var downloaded = await attachmentFiles.DownloadAsync(attachment);
        Assert.Equal(attachmentBytes, downloaded.Content);

        var aliceGroups = new GroupsViewModel(aliceRuntime)
        {
            GroupName = $"client-live-group-{Guid.NewGuid():N}"
        };
        var group = await aliceGroups.CreateGroupAsync(
            aliceOnboarding.Account.SessionId,
            [bobOnboarding.Account.SessionId]);

        Assert.Null(aliceGroups.ErrorMessage);
        Assert.NotNull(group);
        Assert.Single(aliceGroups.Groups);
        Assert.Equal(2, aliceGroups.Groups[0].MemberCount);

        var bobGroups = new GroupsViewModel(bobRuntime);
        var bobGroupItems = await bobGroups.RefreshAsync();

        Assert.Null(bobGroups.ErrorMessage);
        Assert.Contains(bobGroupItems, item => item.Id == group!.Id && item.MemberCount == 2);

        var aliceGroupChat = new GroupChatViewModel(aliceRuntime);
        var bobGroupChat = new GroupChatViewModel(bobRuntime);
        await aliceGroupChat.OpenFromRouteAsync(group!.Id.Value, group.Name);
        await bobGroupChat.OpenFromRouteAsync(group.Id.Value, group.Name);

        var groupBody = $"client-live-group-message-{Guid.NewGuid():N}";
        aliceGroupChat.Draft = groupBody;
        await aliceGroupChat.SendAsync();
        await WaitForAsync(() => aliceGroupChat.Messages.Any(item => item.Body == groupBody && item.State == MessageDeliveryState.Sent));
        await bobGroupChat.RefreshAsync();

        Assert.Null(aliceGroupChat.ErrorMessage);
        Assert.Null(bobGroupChat.ErrorMessage);
        Assert.Contains(aliceGroupChat.Messages, item => item.Body == groupBody && item.Direction == MessageDirection.Outgoing);
        Assert.Contains(bobGroupChat.Messages, item => item.Body == groupBody && item.Direction == MessageDirection.Incoming);

        var started = await aliceChat.StartCallAsync();
        var ringing = await bobChat.PollCallAsync();
        var accepted = await bobChat.AcceptIncomingCallAsync();
        var connected = await aliceChat.PollCallAsync();
        var ended = await aliceChat.EndCallAsync();
        var bobEnded = await bobChat.PollCallAsync();

        Assert.Null(aliceChat.ErrorMessage);
        Assert.Null(bobChat.ErrorMessage);
        Assert.NotNull(started);
        Assert.Equal(CallSessionState.Connecting, started!.State);
        Assert.NotNull(ringing);
        Assert.Equal(CallSessionState.Ringing, ringing!.State);
        Assert.NotNull(accepted);
        Assert.Equal(CallSessionState.Connected, accepted!.State);
        Assert.NotNull(connected);
        Assert.Equal(CallSessionState.Connected, connected!.State);
        Assert.NotNull(ended);
        Assert.Equal(CallSessionState.Ended, ended!.State);
        Assert.NotNull(bobEnded);
        Assert.Equal(CallSessionState.Ended, bobEnded!.State);
    }

    private static ClientRuntime CreateRuntime(string? storageUrl, string? routerUrls)
    {
        if (!string.IsNullOrWhiteSpace(routerUrls))
        {
            var router = new XNodeRpcClient(
                new HttpClient { Timeout = TimeSpan.FromSeconds(15) },
                new XNodeRpcClientOptions(ParseRouterUrls(routerUrls)));
            return new ClientRuntime(
                new InMemorySessionStore(),
                ClientFeatureFlags.ReleaseDefaults,
                new SystemClock(),
                new RoutedSessionStorageMessageTransport(router, new RoutedSessionStorageTransportOptions()),
                requireE2eeTransport: true);
        }

        return new ClientRuntime(
            new InMemorySessionStore(),
            ClientFeatureFlags.ReleaseDefaults,
            new SystemClock(),
            new SessionStorageMessageTransport(
                new HttpClient(),
                new SessionStorageMessageTransportOptions(storageUrl!)),
            requireE2eeTransport: true);
    }

    private static IReadOnlyList<PinnedRouterEndpoint> ParseRouterUrls(string routerUrls) =>
        routerUrls
            .Split([';', ',', '\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static value => value.Split('|', 2, StringSplitOptions.TrimEntries))
            .Select(static parts => parts.Length == 2
                ? new PinnedRouterEndpoint(parts[1], parts[0])
                : throw new InvalidOperationException("XNODE_URLS entries must use '<router-id>|<absolute-url>'."))
            .ToArray();

    private static RealtimeCallService CreateCallService(string callUrl, string? recoveryPhrase) =>
        new(new HttpCallSignalingTransport(
            new HttpClient(),
            new HttpCallSignalingTransportOptions(callUrl),
            _ => Task.FromResult(recoveryPhrase)));

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.True(condition(), "Timed out waiting for optimistic message dispatch.");
    }

    private sealed class LiveAttachmentPicker(
        IAttachmentFileTransport attachmentFiles,
        byte[] content) : IAttachmentPickerService
    {
        public async Task<IReadOnlyList<AttachmentMetadata>> PickAsync(CancellationToken cancellationToken = default)
        {
            await using var upload = new MemoryStream(content);
            return
            [
                await attachmentFiles.UploadAsync(
                    new AttachmentFileUpload("client-live.txt", "text/plain", upload),
                    cancellationToken)
                    .ConfigureAwait(false)
            ];
        }
    }

    private sealed class LocalPushNotificationService(string provider, string token) : IPushNotificationService
    {
        private PushRegistration? registration;

        public Task<PushRegistration?> RegisterAsync(CancellationToken cancellationToken = default)
        {
            registration = new PushRegistration(token, provider, DateTimeOffset.UtcNow);
            return Task.FromResult<PushRegistration?>(registration);
        }

        public Task<PushRegistration?> GetCachedRegistrationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(registration);

        public Task UnregisterAsync(CancellationToken cancellationToken = default)
        {
            registration = null;
            return Task.CompletedTask;
        }
    }

    private sealed class ClientCallService(RealtimeCallService realtime) : ICallService
    {
        public bool IsAvailable => true;

        public Task<CallSessionSnapshot> StartAsync(
            SessionId local,
            SessionId remote,
            string conversationId,
            CancellationToken cancellationToken = default) =>
            realtime.StartOutgoingAsync(local, remote, conversationId, cancellationToken);

        public Task<IReadOnlyList<CallSessionSnapshot>> PollAsync(
            SessionId local,
            CancellationToken cancellationToken = default) =>
            realtime.PollAsync(local, cancellationToken);

        public Task<CallSessionSnapshot?> AcceptAsync(
            string callId,
            SessionId local,
            CancellationToken cancellationToken = default) =>
            realtime.AcceptIncomingAsync(callId, local, cancellationToken);

        public Task<CallSessionSnapshot?> EndAsync(
            string callId,
            SessionId local,
            string reason = "local-hangup",
            CancellationToken cancellationToken = default) =>
            realtime.EndAsync(callId, local, reason, cancellationToken);

        public Task<CallSessionSnapshot?> ApplyNetworkSampleAsync(
            string callId,
            SessionId local,
            CallNetworkSample sample,
            CancellationToken cancellationToken = default) =>
            realtime.ApplyNetworkSampleAsync(callId, local, sample, cancellationToken);
    }
}
