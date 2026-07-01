using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Platform;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.ViewModels.Tests.ViewModels;

public sealed class ChatViewModelTests
{
    [Fact]
    public async Task SendReceiveFlowWorksThroughSharedStubBackend()
    {
        var backend = new StubSessionBackend();
        var aliceRuntime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")), backend: backend);
        var bobRuntime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:01Z")), backend: backend);
        var alice = await aliceRuntime.Accounts.RegisterAsync("Alice");
        var bob = await bobRuntime.Accounts.RegisterAsync("Bob");

        var aliceChat = new ChatViewModel(aliceRuntime);
        await aliceChat.OpenOneToOneAsync(alice, bob.SessionId, "Bob");
        aliceChat.Draft = "hello from maui core";
        aliceChat.StageAttachment(AttachmentMetadata.Local("photo.jpg", "image/jpeg", 128));
        await aliceChat.SendAsync();

        var bobChat = new ChatViewModel(bobRuntime);
        await bobChat.OpenOneToOneAsync(bob, alice.SessionId, "Alice");
        await bobChat.ReceiveAsync();

        Assert.Single(aliceChat.Messages);
        Assert.Single(bobChat.Messages);
        Assert.True(aliceChat.Messages[0].HasAttachments);
        Assert.Equal("hello from maui core", bobChat.Messages[0].Body);
    }

    [Fact]
    public async Task SelfChatKeepsOneOutgoingMessageAndUsesIconStatus()
    {
        var backend = new StubSessionBackend();
        var runtime = ClientRuntime.CreateStubbed(
            clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")),
            backend: backend);
        var account = await runtime.Accounts.RegisterAsync("Notes");
        var chat = new ChatViewModel(runtime);
        await chat.OpenOneToOneAsync(account, account.SessionId, "Заметки для себя");
        chat.Draft = "one copy";

        await chat.SendAsync();
        await chat.ReceiveAsync();

        var message = Assert.Single(chat.Messages);
        Assert.Equal(MessageDirection.Outgoing, message.Direction);
        Assert.Equal("✓", message.StatusGlyph);
        Assert.Equal("Отправлено", message.StatusDescription);
        Assert.True(message.IsStatusVisible);
    }

    [Fact]
    public async Task PickAttachmentsCommandStagesFilesAndAllowsAttachmentOnlySend()
    {
        var backend = new StubSessionBackend();
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")), backend: backend);
        var account = await runtime.Accounts.RegisterAsync("Alice");
        var remote = SessionId.CreateNew();
        var picker = new FakeAttachmentPicker();

        var chat = new ChatViewModel(runtime, attachmentPicker: picker);
        await chat.OpenOneToOneAsync(account, remote, "Bob");

        await chat.PickAttachmentsAsync();

        Assert.True(chat.HasStagedAttachments);
        Assert.Equal("receipt.pdf", chat.StagedAttachmentSummary);
        Assert.True(chat.SendCommand.CanExecute(null));

        await chat.SendAsync();

        Assert.Single(chat.Messages);
        Assert.True(chat.Messages[0].HasAttachments);
        Assert.Equal("[Вложение]", chat.Messages[0].Body);
        Assert.False(chat.HasStagedAttachments);
    }

    [Fact]
    public async Task ReceiveWithoutNewMessagesDoesNotResetCollection()
    {
        var backend = new StubSessionBackend();
        var aliceRuntime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")), backend: backend);
        var bobRuntime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:01Z")), backend: backend);
        var alice = await aliceRuntime.Accounts.RegisterAsync("Alice");
        var bob = await bobRuntime.Accounts.RegisterAsync("Bob");

        var aliceChat = new ChatViewModel(aliceRuntime);
        await aliceChat.OpenOneToOneAsync(alice, bob.SessionId, "Bob");
        aliceChat.Draft = "stable scroll";
        await aliceChat.SendAsync();

        var bobChat = new ChatViewModel(bobRuntime);
        await bobChat.OpenOneToOneAsync(bob, alice.SessionId, "Alice");
        await bobChat.ReceiveAsync();

        var changeCount = 0;
        bobChat.Messages.CollectionChanged += (_, _) => changeCount++;

        await bobChat.ReceiveAsync();

        Assert.Equal(0, changeCount);
        Assert.Single(bobChat.Messages);
        Assert.Equal("stable scroll", bobChat.Messages[0].Body);
    }

    [Fact]
    public async Task ReceiveMarksVisibleIncomingMessagesReadByConversationCursor()
    {
        var backend = new StubSessionBackend();
        var aliceRuntime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")), backend: backend);
        var bobRuntime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:01Z")), backend: backend);
        var alice = await aliceRuntime.Accounts.RegisterAsync("Alice");
        var bob = await bobRuntime.Accounts.RegisterAsync("Bob");

        var aliceChat = new ChatViewModel(aliceRuntime);
        await aliceChat.OpenOneToOneAsync(alice, bob.SessionId, "Bob");
        aliceChat.Draft = "read by cursor";
        await aliceChat.SendAsync();

        var bobChat = new ChatViewModel(bobRuntime);
        await bobChat.OpenOneToOneAsync(bob, alice.SessionId, "Alice");
        await bobChat.ReceiveAsync();
        var cursor = await bobRuntime.Messages.GetReadCursorAsync(ConversationId.ForOneToOne(alice.SessionId));

        Assert.NotNull(cursor);
        Assert.Equal(MessageDeliveryState.Read, bobChat.Messages[0].State);
    }

    [Fact]
    public async Task LoadOlderMessagesPrependsPreviousPage()
    {
        var clock = new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z"));
        var runtime = ClientRuntime.CreateStubbed(clock: clock);
        var account = await runtime.Accounts.RegisterAsync("Owner");
        var remote = SessionId.CreateNew();
        var conversation = await runtime.Conversations.GetOrCreateOneToOneAsync(remote, "Remote");

        for (var index = 0; index < 105; index++)
        {
            await ((IMessageRepository)runtime.Store).AppendAsync(new Message(
                MessageId.NewId(),
                conversation.Id,
                account.SessionId,
                remote,
                $"message-{index:000}",
                MessageDirection.Outgoing,
                MessageDeliveryState.Sent,
                clock.UtcNow.AddSeconds(index),
                []));
        }

        var chat = new ChatViewModel(runtime);
        await chat.OpenOneToOneAsync(account, remote, "Remote");

        Assert.Equal(100, chat.Messages.Count);
        Assert.Equal("message-005", chat.Messages[0].Body);

        await chat.LoadOlderMessagesAsync();

        Assert.Equal(105, chat.Messages.Count);
        Assert.Equal("message-000", chat.Messages[0].Body);
        Assert.Equal("message-104", chat.Messages[^1].Body);
    }

    [Fact]
    public async Task StartCall_UsesInjectedCallServiceAndUpdatesState()
    {
        var backend = new StubSessionBackend();
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")), backend: backend);
        var account = await runtime.Accounts.RegisterAsync("Alice");
        var remote = SessionId.CreateNew();
        var calls = new FakeCallService();

        var chat = new ChatViewModel(runtime, calls);
        await chat.OpenOneToOneAsync(account, remote, "Bob");

        var started = await chat.StartCallAsync();

        Assert.NotNull(started);
        Assert.Equal(CallSessionState.Connecting, started!.State);
        Assert.Equal("call-1", chat.ActiveCallId);
        Assert.Equal(CallSessionState.Connecting, chat.ActiveCallState);
        Assert.Equal(1, calls.StartCount);
    }

    [Fact]
    public async Task EndCall_UsesInjectedCallServiceAndUpdatesState()
    {
        var backend = new StubSessionBackend();
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")), backend: backend);
        var account = await runtime.Accounts.RegisterAsync("Alice");
        var remote = SessionId.CreateNew();
        var calls = new FakeCallService();

        var chat = new ChatViewModel(runtime, calls);
        await chat.OpenOneToOneAsync(account, remote, "Bob");
        await chat.StartCallAsync();

        var ended = await chat.EndCallAsync();

        Assert.NotNull(ended);
        Assert.Equal(CallSessionState.Ended, ended!.State);
        Assert.Equal(CallSessionState.Ended, chat.ActiveCallState);
        Assert.Equal(1, calls.EndCount);
    }

    [Fact]
    public async Task PollCall_UpdatesStateForIncomingRingingCall()
    {
        var backend = new StubSessionBackend();
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")), backend: backend);
        var account = await runtime.Accounts.RegisterAsync("Alice");
        var remote = SessionId.CreateNew();
        var calls = new FakeCallService();

        var chat = new ChatViewModel(runtime, calls);
        await chat.OpenOneToOneAsync(account, remote, "Bob");

        calls.NextPollSnapshots =
        [
            new CallSessionSnapshot(
                "call-incoming",
                chat.Conversation!.Id.Value,
                account.SessionId,
                remote,
                CallSessionState.Ringing,
                new CallQualityMetrics(100, 0, 0, 0, 0, DateTimeOffset.UtcNow),
                0,
                null,
                [])
        ];

        var polled = await chat.PollCallAsync();

        Assert.NotNull(polled);
        Assert.Equal("call-incoming", polled!.CallId);
        Assert.Equal(CallSessionState.Ringing, chat.ActiveCallState);
        Assert.Equal("call-incoming", chat.ActiveCallId);
    }

    [Fact]
    public async Task AcceptIncomingCall_TransitionsToConnected()
    {
        var backend = new StubSessionBackend();
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")), backend: backend);
        var account = await runtime.Accounts.RegisterAsync("Alice");
        var remote = SessionId.CreateNew();
        var calls = new FakeCallService();

        var chat = new ChatViewModel(runtime, calls);
        await chat.OpenOneToOneAsync(account, remote, "Bob");

        calls.NextPollSnapshots =
        [
            new CallSessionSnapshot(
                "call-incoming",
                chat.Conversation!.Id.Value,
                account.SessionId,
                remote,
                CallSessionState.Ringing,
                new CallQualityMetrics(100, 0, 0, 0, 0, DateTimeOffset.UtcNow),
                0,
                null,
                [])
        ];

        await chat.PollCallAsync();
        var accepted = await chat.AcceptIncomingCallAsync();

        Assert.NotNull(accepted);
        Assert.Equal(CallSessionState.Connected, accepted!.State);
        Assert.Equal(CallSessionState.Connected, chat.ActiveCallState);
        Assert.Equal(1, calls.AcceptCount);
    }

    [Fact]
    public async Task DeclineIncomingCall_TransitionsToEndedWithDeclineReason()
    {
        var backend = new StubSessionBackend();
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")), backend: backend);
        var account = await runtime.Accounts.RegisterAsync("Alice");
        var remote = SessionId.CreateNew();
        var calls = new FakeCallService();

        var chat = new ChatViewModel(runtime, calls);
        await chat.OpenOneToOneAsync(account, remote, "Bob");

        calls.NextPollSnapshots =
        [
            new CallSessionSnapshot(
                "call-incoming",
                chat.Conversation!.Id.Value,
                account.SessionId,
                remote,
                CallSessionState.Ringing,
                new CallQualityMetrics(100, 0, 0, 0, 0, DateTimeOffset.UtcNow),
                0,
                null,
                [])
        ];

        await chat.PollCallAsync();
        Assert.True(chat.IsIncomingCall);
        Assert.Equal("Входящий звонок", chat.CallStatusText);

        var declined = await chat.DeclineIncomingCallAsync();

        Assert.NotNull(declined);
        Assert.Equal(CallSessionState.Ended, declined!.State);
        Assert.Equal("local-decline", declined.FailureReason);
        Assert.Equal(CallSessionState.Ended, chat.ActiveCallState);
        Assert.Equal(1, calls.EndCount);
    }

    [Fact]
    public async Task ApplyCallNetworkSample_UsesCallServiceAndUpdatesState()
    {
        var backend = new StubSessionBackend();
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")), backend: backend);
        var account = await runtime.Accounts.RegisterAsync("Alice");
        var remote = SessionId.CreateNew();
        var calls = new FakeCallService();

        var chat = new ChatViewModel(runtime, calls);
        await chat.OpenOneToOneAsync(account, remote, "Bob");
        await chat.StartCallAsync();

        var updated = await chat.ApplyCallNetworkSampleAsync(new CallNetworkSample(
            RttMs: 650,
            JitterMs: 140,
            PacketLossRatio: 0.15,
            AvailableBitrateKbps: 20));

        Assert.NotNull(updated);
        Assert.Equal(CallSessionState.Reconnecting, updated!.State);
        Assert.Equal(CallSessionState.Reconnecting, chat.ActiveCallState);
        Assert.Equal(1, calls.ApplySampleCount);
    }

    private sealed class FakeCallService : ICallService
    {
        public bool IsAvailable => true;

        public int StartCount { get; private set; }

        public int EndCount { get; private set; }

        public int AcceptCount { get; private set; }

        public int ApplySampleCount { get; private set; }

        public IReadOnlyList<CallSessionSnapshot> NextPollSnapshots { get; set; } = [];

        public Task<CallSessionSnapshot> StartAsync(SessionId local, SessionId remote, string conversationId, CancellationToken cancellationToken = default)
        {
            StartCount++;
            return Task.FromResult(new CallSessionSnapshot(
                "call-1",
                conversationId,
                local,
                remote,
                CallSessionState.Connecting,
                new CallQualityMetrics(100, 0, 0, 0, 0, DateTimeOffset.UtcNow),
                0,
                null,
                []));
        }

        public Task<IReadOnlyList<CallSessionSnapshot>> PollAsync(SessionId local, CancellationToken cancellationToken = default)
        {
            var next = NextPollSnapshots;
            NextPollSnapshots = [];
            return Task.FromResult(next);
        }

        public Task<CallSessionSnapshot?> AcceptAsync(string callId, SessionId local, CancellationToken cancellationToken = default)
        {
            AcceptCount++;
            return Task.FromResult<CallSessionSnapshot?>(new CallSessionSnapshot(
                callId,
                "03conversation",
                local,
                SessionId.CreateNew(),
                CallSessionState.Connected,
                new CallQualityMetrics(100, 0, 0, 0, 0, DateTimeOffset.UtcNow),
                0,
                null,
                []));
        }

        public Task<CallSessionSnapshot?> EndAsync(string callId, SessionId local, string reason = "local-hangup", CancellationToken cancellationToken = default)
        {
            EndCount++;
            return Task.FromResult<CallSessionSnapshot?>(new CallSessionSnapshot(
                callId,
                "03conversation",
                local,
                SessionId.CreateNew(),
                CallSessionState.Ended,
                new CallQualityMetrics(100, 0, 0, 0, 0, DateTimeOffset.UtcNow),
                0,
                reason,
                []));
        }

        public Task<CallSessionSnapshot?> ApplyNetworkSampleAsync(string callId, SessionId local, CallNetworkSample sample, CancellationToken cancellationToken = default) =>
            Task.FromResult<CallSessionSnapshot?>(OnApplyNetworkSample(callId, local, sample));

        private CallSessionSnapshot OnApplyNetworkSample(string callId, SessionId local, CallNetworkSample sample)
        {
            ApplySampleCount++;

            var state = sample.PacketLossRatio > 0.08 || sample.RttMs > 350
                ? CallSessionState.Reconnecting
                : CallSessionState.Connected;

            return new CallSessionSnapshot(
                callId,
                "03conversation",
                local,
                SessionId.CreateNew(),
                state,
                new CallQualityMetrics(100, sample.RttMs, sample.JitterMs, sample.PacketLossRatio, sample.AvailableBitrateKbps, DateTimeOffset.UtcNow),
                state == CallSessionState.Reconnecting ? 1 : 0,
                null,
                []);
        }
    }

    private sealed class FakeAttachmentPicker : IAttachmentPickerService
    {
        public Task<IReadOnlyList<AttachmentMetadata>> PickAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AttachmentMetadata>>(
            [
                AttachmentMetadata.Local("receipt.pdf", "application/pdf", 1024)
            ]);
    }
}
