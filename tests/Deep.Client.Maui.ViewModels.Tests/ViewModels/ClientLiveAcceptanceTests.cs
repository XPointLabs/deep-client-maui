using System.Text;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.ViewModels.Tests.Services;
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
    [StrictLiveFact]
    public async Task ClientViewModels_RunLaunchCriticalFlowThroughLiveLocalInfrastructure_WhenConfigured()
    {
        var routerUrls = Environment.GetEnvironmentVariable("XNODE_URLS");
        var storageUrl = Environment.GetEnvironmentVariable("DEEP_STORAGE_URL");
        var fileUrl = Environment.GetEnvironmentVariable("DEEP_FILE_URL");
        var pushUrl = Environment.GetEnvironmentVariable("DEEP_PUSH_URL");
        var callUrl = Environment.GetEnvironmentVariable("DEEP_CALL_SIGNALING_BASE_URL");
        var endpointPolicy = StrictLiveEndpointPolicy.Resolve();
        var serviceEndpointPolicy = StrictLiveEndpointPolicy.ResolveHttpServicePolicy();
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(routerUrls))
        {
            missing.Add("XNODE_URLS (at least three pinned routers; direct storage is a separate contract lane)");
        }
        if (string.IsNullOrWhiteSpace(fileUrl))
        {
            missing.Add("DEEP_FILE_URL");
        }
        if (string.IsNullOrWhiteSpace(pushUrl))
        {
            missing.Add("DEEP_PUSH_URL");
        }
        if (string.IsNullOrWhiteSpace(callUrl))
        {
            missing.Add("DEEP_CALL_SIGNALING_BASE_URL");
        }
        Assert.True(missing.Count == 0, $"Strict live acceptance configuration is incomplete: {string.Join(", ", missing)}.");
        Assert.True(
            string.IsNullOrWhiteSpace(storageUrl),
            "DEEP_STORAGE_URL must be absent; direct storage cannot satisfy routed release evidence.");

        var endpoints = RoutedRuntimeConfiguration.ParseAtLeastThree(routerUrls!, endpointPolicy);
        _ = RoutedRuntimeConfiguration.RequireLiveServiceUrl("DEEP_FILE_URL", fileUrl, endpointPolicy);
        _ = RoutedRuntimeConfiguration.RequireLiveServiceUrl("DEEP_PUSH_URL", pushUrl, endpointPolicy);
        _ = RoutedRuntimeConfiguration.RequireLiveServiceUrl("DEEP_CALL_SIGNALING_BASE_URL", callUrl, endpointPolicy);
        var aliceFixture = CreateRoutedRuntime(endpoints, endpointPolicy);
        var bobFixture = CreateRoutedRuntime(endpoints, endpointPolicy);
        var aliceRuntime = aliceFixture.Runtime;
        var bobRuntime = bobFixture.Runtime;
        var attachmentFiles = new HttpAttachmentFileTransport(
            new HttpClient(),
            new HttpAttachmentFileTransportOptions(fileUrl!),
            serviceEndpointPolicy);
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
        var aliceCalls = new ClientCallService(CreateCallService(callUrl!, alicePhrase, serviceEndpointPolicy));
        var bobCalls = new ClientCallService(CreateCallService(callUrl!, bobPhrase, serviceEndpointPolicy));

        var bobNotifications = new NotificationRegistrationViewModel(new PushRegistrationCoordinator(
            bobRuntime,
            new LocalPushNotificationService("fcm", $"client-live-fcm-{Guid.NewGuid():N}"),
            new HttpPushSubscriptionTransport(
                new HttpClient(),
                new HttpPushSubscriptionTransportOptions(pushUrl!),
                serviceEndpointPolicy),
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
        AssertRoutedStorageEvidence(aliceFixture, endpoints);
        AssertRoutedStorageEvidence(bobFixture, endpoints);

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

        aliceFixture.RouterHandler.MakeRouterApisUnavailable();
        var routedFailure = await Record.ExceptionAsync(() =>
            aliceRuntime.Messages.SendOneToOneAsync(
                aliceOnboarding.Account.SessionId,
                bobOnboarding.Account.SessionId,
                $"router-outage-must-fail-{Guid.NewGuid():N}"));

        Assert.NotNull(routedFailure);
        Assert.IsAssignableFrom<HttpRequestException>(routedFailure.GetBaseException());
        Assert.True(aliceFixture.RouterHandler.BlockedRouterRequests > 0);
        Assert.Equal(0, aliceFixture.RouterHandler.UnexpectedDestinationRequests);
    }

    private static RoutedRuntimeFixture CreateRoutedRuntime(
        IReadOnlyList<PinnedRouterEndpoint> endpoints,
        RoutedRuntimeEndpointPolicy endpointPolicy)
    {
        var handler = new RouterAvailabilityHandler(endpoints);
        var composition = RoutedProductionCompositionFactory.Create(
            endpoints,
            directStorageUrl: null,
            new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) },
            new RoutedSessionStorageTransportOptions(
                MetadataMode: SessionStorageMetadataMode.OpaqueP03),
            OpaqueStorageTestDependencies.Create(),
            TimeProvider.System,
            endpointPolicy);
        var runtime = new ClientRuntime(
            new InMemorySessionStore(),
            ClientFeatureFlags.ReleaseDefaults with { MetadataPrivateTransportRequired = false },
            new SystemClock(),
            composition.SessionMessageTransport,
            requireE2eeTransport: true);
        return new RoutedRuntimeFixture(runtime, composition.Router, handler);
    }

    private static void AssertRoutedStorageEvidence(
        RoutedRuntimeFixture fixture,
        IReadOnlyList<PinnedRouterEndpoint> endpoints)
    {
        var route = Assert.IsType<TransportRouteSnapshot>(fixture.Router.CurrentRoute);
        Assert.Equal("onion-storage", route.Mode);
        Assert.Equal([0, 1, 2], route.Nodes.Select(static node => node.Index));
        Assert.Equal(3, route.Nodes.Count);
        var pinnedRouterIds = endpoints
            .Select(static endpoint => endpoint.ExpectedRouterId)
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(route.Nodes, node => Assert.Contains(node.RouterId, pinnedRouterIds));
        Assert.Equal(3, route.Nodes.Select(static node => node.RouterId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            3,
            route.Nodes
                .Select(static node => node.RpcEndpoint)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        Assert.All(
            route.Nodes,
            static node => Assert.True(Uri.TryCreate(node.RpcEndpoint, UriKind.Absolute, out _)));
        Assert.Contains(
            fixture.RouterHandler.ForwardedBodies,
            static body => body.Contains("\"storage_route\"", StringComparison.Ordinal));
        Assert.Contains(
            fixture.RouterHandler.ForwardedBodies,
            static body => body.Contains("\"onion_request\"", StringComparison.Ordinal));
        Assert.Equal(0, fixture.RouterHandler.UnexpectedDestinationRequests);
    }

    private static RealtimeCallService CreateCallService(
        string callUrl,
        string? recoveryPhrase,
        HttpServiceEndpointPolicy serviceEndpointPolicy) =>
        new(new HttpCallSignalingTransport(
            new HttpClient(),
            new HttpCallSignalingTransportOptions(callUrl),
            _ => Task.FromResult(recoveryPhrase),
            endpointPolicy: serviceEndpointPolicy));

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.True(condition(), "Timed out waiting for optimistic message dispatch.");
    }

    private sealed record RoutedRuntimeFixture(
        ClientRuntime Runtime,
        XNodeRpcClient Router,
        RouterAvailabilityHandler RouterHandler);

    private sealed class RouterAvailabilityHandler : DelegatingHandler
    {
        private readonly HashSet<string> allowedOrigins;
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> forwardedBodies = new();
        private int routerApisUnavailable;
        private int blockedRouterRequests;
        private int unexpectedDestinationRequests;

        public RouterAvailabilityHandler(IReadOnlyList<PinnedRouterEndpoint> endpoints)
        {
            allowedOrigins = endpoints
                .Select(static endpoint => new Uri(endpoint.BaseUrl).GetLeftPart(UriPartial.Authority))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            InnerHandler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(5)
            };
        }

        public IReadOnlyList<string> ForwardedBodies => forwardedBodies.ToArray();

        public int BlockedRouterRequests => Volatile.Read(ref blockedRouterRequests);

        public int UnexpectedDestinationRequests => Volatile.Read(ref unexpectedDestinationRequests);

        public void MakeRouterApisUnavailable() =>
            Interlocked.Exchange(ref routerApisUnavailable, 1);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var destination = request.RequestUri?.GetLeftPart(UriPartial.Authority);
            if (destination is null || !allowedOrigins.Contains(destination))
            {
                Interlocked.Increment(ref unexpectedDestinationRequests);
                throw new HttpRequestException(
                    "Routed acceptance attempted a destination outside the pinned router APIs.");
            }

            if (Volatile.Read(ref routerApisUnavailable) != 0)
            {
                Interlocked.Increment(ref blockedRouterRequests);
                throw new HttpRequestException("Pinned router APIs are unavailable.");
            }

            if (request.Content is not null)
            {
                forwardedBodies.Enqueue(
                    await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
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
