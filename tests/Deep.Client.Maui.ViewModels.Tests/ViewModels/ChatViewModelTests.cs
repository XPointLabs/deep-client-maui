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
    public async Task ReceiveReconcilesMessageAlreadyPersistedByBackgroundSync()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-07-13T00:00:00Z")));
        var account = await runtime.Accounts.RegisterAsync("Owner");
        var remote = SessionId.CreateNew();
        var conversation = await runtime.Conversations.GetOrCreateOneToOneAsync(remote, "Remote");
        var chat = new ChatViewModel(runtime);
        await chat.OpenOneToOneAsync(account, remote, "Remote");
        var incoming = new Message(
            MessageId.NewId(),
            conversation.Id,
            remote,
            account.SessionId,
            "background persisted",
            MessageDirection.Incoming,
            MessageDeliveryState.Delivered,
            runtime.Clock.UtcNow,
            []);
        await ((IMessageRepository)runtime.Store).AppendAsync(incoming);

        await chat.ReceiveAsync();

        var visible = Assert.Single(chat.Messages);
        Assert.Equal(incoming.Id, visible.Id);
        Assert.Equal(MessageDeliveryState.Read, visible.State);
    }

    [Fact]
    public async Task ReceiveKeepsOlderPersistedMessagesReachableAfterEmptyOpen()
    {
        var start = DateTimeOffset.Parse("2026-07-13T00:00:00Z");
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(start.AddHours(1)));
        var account = await runtime.Accounts.RegisterAsync("Owner");
        var remote = SessionId.CreateNew();
        var conversation = await runtime.Conversations.GetOrCreateOneToOneAsync(remote, "Remote");
        var chat = new ChatViewModel(runtime);
        await chat.OpenOneToOneAsync(account, remote, "Remote");
        for (var index = 0; index < 21; index++)
        {
            await ((IMessageRepository)runtime.Store).AppendAsync(new Message(
                MessageId.NewId(),
                conversation.Id,
                remote,
                account.SessionId,
                $"persisted {index}",
                MessageDirection.Incoming,
                MessageDeliveryState.Delivered,
                start.AddMinutes(index),
                []));
        }

        await chat.ReceiveAsync();
        Assert.Equal(20, chat.Messages.Count);

        await chat.LoadOlderMessagesAsync();

        Assert.Equal(21, chat.Messages.Count);
        Assert.Contains(chat.Messages, message => message.Body == "persisted 0");
    }

    [Fact]
    public async Task ReceiveRebuildsDisjointRecentWindowWithoutLosingHistory()
    {
        var start = DateTimeOffset.Parse("2026-07-13T00:00:00Z");
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(start.AddHours(2)));
        var account = await runtime.Accounts.RegisterAsync("Owner");
        var remote = SessionId.CreateNew();
        var conversation = await runtime.Conversations.GetOrCreateOneToOneAsync(remote, "Remote");
        await ((IMessageRepository)runtime.Store).AppendAsync(new Message(
            MessageId.NewId(), conversation.Id, remote, account.SessionId, "loaded old",
            MessageDirection.Incoming, MessageDeliveryState.Delivered, start, []));
        var chat = new ChatViewModel(runtime);
        await chat.OpenOneToOneAsync(account, remote, "Remote");
        for (var index = 1; index <= 21; index++)
        {
            await ((IMessageRepository)runtime.Store).AppendAsync(new Message(
                MessageId.NewId(), conversation.Id, remote, account.SessionId, $"burst {index}",
                MessageDirection.Incoming, MessageDeliveryState.Delivered, start.AddMinutes(index), []));
        }

        await chat.ReceiveAsync();
        Assert.Equal(20, chat.Messages.Count);

        await chat.LoadOlderMessagesAsync();

        Assert.Equal(22, chat.Messages.Count);
        Assert.Contains(chat.Messages, message => message.Body == "loaded old");
        Assert.Contains(chat.Messages, message => message.Body == "burst 1");
    }

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
    public async Task OpenFromRouteHydratesRecentMessagesFromLocalStore()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var account = await runtime.Accounts.RegisterAsync("Alice");
        var recipient = SessionId.CreateNew();

        var chat = new ChatViewModel(runtime);
        await chat.OpenOneToOneAsync(account, recipient, "Bob");
        chat.Draft = "cached open";
        await chat.SendAsync();

        var reopened = new ChatViewModel(runtime);
        await reopened.OpenFromRouteAsync(recipient.Value, "Bob");

        Assert.Equal("Bob", reopened.ConversationTitle);
        var item = Assert.Single(reopened.Messages);
        Assert.Equal("cached open", item.Body);
        Assert.Equal(recipient, reopened.Counterpart);
    }

    [Fact]
    public async Task PrepareRouteHydratesRecentMessagesFromUiCacheBeforeDatabaseOpen()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var account = await runtime.Accounts.RegisterAsync("Alice");
        var recipient = SessionId.CreateNew();
        var cache = new ChatOpenUiCache();

        var chat = new ChatViewModel(runtime, openCache: cache);
        await chat.OpenOneToOneAsync(account, recipient, "Bob");
        chat.Draft = "instant cache";
        await chat.SendAsync();

        var reopened = new ChatViewModel(runtime, openCache: cache);
        reopened.PrepareRoute(recipient, "Bob");

        var item = Assert.Single(reopened.Messages);
        Assert.Equal("instant cache", item.Body);
        Assert.Equal("Bob", reopened.ConversationTitle);
        Assert.Equal(recipient, reopened.Counterpart);
    }

    [Fact]
    public void ChatOpenCacheRejectsStaleEntriesAndNeverReturnsExpiredMessages()
    {
        var now = DateTimeOffset.Parse("2026-05-28T00:00:00Z");
        var account = new SessionAccount(SessionId.CreateNew(), "Owner", now);
        var remote = SessionId.CreateNew();
        var conversation = new Conversation(
            ConversationId.ForOneToOne(remote),
            ConversationKind.OneToOne,
            "Remote",
            ConversationSettings.Default(ConversationKind.OneToOne),
            now,
            now);
        var live = new ChatMessageItem(
            MessageId.NewId(), "live", MessageDirection.Incoming, MessageDeliveryState.Delivered,
            now, [], null, [], now.AddMinutes(1));
        var expired = new ChatMessageItem(
            MessageId.NewId(), "expired", MessageDirection.Incoming, MessageDeliveryState.Delivered,
            now, [], null, [], now.AddSeconds(1));
        var cache = new ChatOpenUiCache();
        cache.Store(new ChatOpenUiSnapshot(
            account, remote, conversation, false, false, [live, expired], now, live.Id, false, now));

        Assert.True(cache.TryGet(remote, now.AddSeconds(2), out var filtered));
        Assert.Equal([live.Id], filtered.Messages.Select(static message => message.Id));
        Assert.False(cache.TryGet(remote, now.AddMinutes(3), out _));
    }

    [Fact]
    public async Task OpenFromRouteClearsCachedMessagesWhenLocalSnapshotIsEmpty()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var account = await runtime.Accounts.RegisterAsync("Alice");
        var recipient = SessionId.CreateNew();
        var conversation = await runtime.Conversations.GetOrCreateOneToOneAsync(recipient, "Bob");
        var message = new Message(
            MessageId.NewId(),
            conversation.Id,
            account.SessionId,
            recipient,
            "stale cache",
            MessageDirection.Outgoing,
            MessageDeliveryState.Sent,
            DateTimeOffset.Parse("2026-05-28T00:00:01Z"),
            []);
        await ((IMessageRepository)runtime.Store).AppendAsync(message);

        var cache = new ChatOpenUiCache();
        var cached = new ChatViewModel(runtime, openCache: cache);
        await cached.OpenFromRouteAsync(recipient.Value, "Bob");
        Assert.Single(cached.Messages);

        await ((IMessageRepository)runtime.Store).DeleteAsync(message.Id);

        var reopened = new ChatViewModel(runtime, openCache: cache);
        reopened.PrepareRoute(recipient, "Bob");
        Assert.Single(reopened.Messages);

        await reopened.OpenFromRouteAsync(recipient.Value, "Bob");

        Assert.Empty(reopened.Messages);
    }

    [Fact]
    public void ImagePresentationSeparatesPhotosFromImageDocuments()
    {
        var photo = new ChatMessageItem(
            MessageId.NewId(),
            "[Вложение]",
            MessageDirection.Outgoing,
            MessageDeliveryState.Sent,
            DateTimeOffset.UnixEpoch,
            [AttachmentMetadata.Local("photo.jpg", "image/jpeg", 1024, width: 1280, height: 720)],
            replyTo: null,
            reactions: []);
        var imageDocument = new ChatMessageItem(
            MessageId.NewId(),
            "[Вложение]",
            MessageDirection.Outgoing,
            MessageDeliveryState.Sent,
            DateTimeOffset.UnixEpoch,
            [AttachmentMetadata.Local("diagram.png", "image/png", 2048, isDocument: true)],
            replyTo: null,
            reactions: []);

        Assert.True(photo.IsImageMessage);
        Assert.False(photo.HasGenericAttachments);
        Assert.Equal("Фото", photo.AttachmentTitle);
        Assert.True(photo.ImagePreviewWidthRequest > photo.ImagePreviewHeightRequest);

        Assert.False(imageDocument.IsImageMessage);
        Assert.True(imageDocument.HasGenericAttachments);
        Assert.Equal("diagram.png", imageDocument.AttachmentTitle);
        Assert.StartsWith("Файл ·", imageDocument.AttachmentSubtitle);
    }

    [Fact]
    public void PrepareRouteShowsTitleAndClearsStaleChatState()
    {
        var runtime = ClientRuntime.CreateStubbed();
        var chat = new ChatViewModel(runtime);
        chat.Messages.Add(new ChatMessageItem(
            MessageId.NewId(),
            "stale",
            MessageDirection.Incoming,
            MessageDeliveryState.Delivered,
            DateTimeOffset.UnixEpoch,
            [],
            replyTo: null,
            reactions: []));
        chat.StageAttachment(AttachmentMetadata.Local("old.txt", "text/plain", 12));

        chat.PrepareRoute(SessionId.CreateNew(), "Мария");

        Assert.Equal("Мария", chat.ConversationTitle);
        Assert.Empty(chat.Messages);
        Assert.Empty(chat.StagedAttachments);
        Assert.False(chat.SendCommand.CanExecute(null));
        Assert.False(chat.ReceiveCommand.CanExecute(null));
    }

    [Fact]
    public async Task UnknownSenderCanBeAcceptedOrBlockedFromChat()
    {
        var backend = new StubSessionBackend();
        var aliceRuntime = ClientRuntime.CreateStubbed(backend: backend);
        var bobRuntime = ClientRuntime.CreateStubbed(backend: backend);
        var alice = await aliceRuntime.Accounts.RegisterAsync("Alice");
        var bob = await bobRuntime.Accounts.RegisterAsync("Bob");
        await aliceRuntime.Messages.SendOneToOneAsync(alice.SessionId, bob.SessionId, "request");
        await bobRuntime.Inbox.SynchronizeAsync();
        var chat = new ChatViewModel(bobRuntime);

        await chat.OpenOneToOneAsync(bob, alice.SessionId, "Alice");
        Assert.True(chat.IsMessageRequest);

        await chat.AcceptMessageRequestAsync();
        Assert.False(chat.IsMessageRequest);
        Assert.True(chat.IsComposerEnabled);

        await chat.BlockContactAsync();
        Assert.True(chat.IsBlocked);
        Assert.False(chat.IsComposerEnabled);
        Assert.False(chat.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task ReplyAndReactionUpdateChatPresentation()
    {
        var runtime = ClientRuntime.CreateStubbed();
        var account = await runtime.Accounts.RegisterAsync("Alice");
        var chat = new ChatViewModel(runtime);
        await chat.OpenOneToOneAsync(account, account.SessionId, "Notes");
        chat.Draft = "original";
        await chat.SendAsync();
        var original = Assert.Single(chat.Messages);

        chat.BeginReply(original);
        chat.Draft = "reply";
        await chat.SendAsync();
        var reply = Assert.Single(chat.Messages, message => message.Body == "reply");
        await chat.ToggleReactionAsync(reply, "👍");

        Assert.Equal(original.Id, chat.Messages.Single(message => message.Body == "reply").ReplyTo?.MessageId);
        var reaction = Assert.Single(chat.Messages.Single(message => message.Body == "reply").ReactionChips);
        Assert.Equal(reply.Id, reaction.MessageId);
        Assert.Equal("👍", reaction.Emoji);
        Assert.Equal(1, reaction.Count);
        await chat.ToggleReactionAsync(reply, "👍");
        Assert.Empty(chat.Messages.Single(message => message.Body == "reply").ReactionChips);
        Assert.False(chat.IsReplying);
    }

    [Fact]
    public async Task ClearAndDeleteConversationRemoveHistoryAndHideChat()
    {
        var runtime = ClientRuntime.CreateStubbed();
        var account = await runtime.Accounts.RegisterAsync("Alice");
        var remote = SessionId.CreateNew();
        var chat = new ChatViewModel(runtime);
        await chat.OpenOneToOneAsync(account, remote, "Bob");
        chat.Draft = "temporary";
        await chat.SendAsync();

        await chat.ClearConversationAsync();

        Assert.Empty(chat.Messages);
        Assert.Empty(await runtime.Messages.ListConversationMessagesAsync(chat.Conversation!.Id));

        chat.Draft = "delete me";
        await chat.SendAsync();
        await chat.DeleteConversationAsync();

        var stored = await ((IConversationRepository)runtime.Store).GetAsync(chat.Conversation!.Id);
        Assert.NotNull(stored);
        Assert.True(stored!.IsHidden);
        Assert.Empty(await runtime.Messages.ListConversationMessagesAsync(chat.Conversation.Id));
    }

    [Fact]
    public async Task SendAddsOptimisticMessageBeforeTransportCompletes()
    {
        var transport = new BlockingMessageTransport();
        var runtime = new ClientRuntime(
            new InMemorySessionStore(),
            Deep.Client.Shared.Features.ClientFeatureFlags.ReleaseDefaults,
            new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")),
            transport);
        var account = await runtime.Accounts.RegisterAsync("Alice");
        var chat = new ChatViewModel(runtime);
        await chat.OpenOneToOneAsync(account, SessionId.CreateNew(), "Bob");
        chat.Draft = "instant";

        await chat.SendAsync();

        var pending = Assert.Single(chat.Messages);
        Assert.Equal(MessageDeliveryState.Sending, pending.State);
        Assert.Equal("◷", pending.StatusGlyph);
        Assert.Empty(chat.Draft);

        transport.Release();
        for (var attempt = 0; attempt < 50 && chat.Messages[0].State != MessageDeliveryState.Sent; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(MessageDeliveryState.Sent, chat.Messages[0].State);
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
    public async Task VoiceRecordingQueuesAudioAttachmentOptimistically()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var account = await runtime.Accounts.RegisterAsync("Alice");
        var remote = SessionId.CreateNew();
        var recorder = new FakeVoiceMessageRecorder();

        var chat = new ChatViewModel(runtime, attachmentPicker: null, voiceRecorder: recorder);
        await chat.OpenOneToOneAsync(account, remote, "Bob");

        await chat.StartVoiceRecordingAsync();
        Assert.True(chat.IsRecordingVoice);

        await chat.StopVoiceRecordingAndSendAsync();

        var message = Assert.Single(chat.Messages);
        Assert.True(message.IsVoiceMessage);
        Assert.False(message.HasVisibleBody);
        Assert.Equal("Голосовое сообщение", message.AttachmentTitle);
        Assert.Equal("00:02", message.VoiceDurationLabel);
    }

    [Fact]
    public async Task CancelVoiceRecordingStopsRecorderWithoutQueueingMessage()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var account = await runtime.Accounts.RegisterAsync("Alice");
        var recorder = new FakeVoiceMessageRecorder();
        var chat = new ChatViewModel(runtime, attachmentPicker: null, voiceRecorder: recorder);
        await chat.OpenOneToOneAsync(account, SessionId.CreateNew(), "Bob");

        await chat.StartVoiceRecordingAsync();
        await chat.CancelVoiceRecordingAsync();

        Assert.False(chat.IsRecordingVoice);
        Assert.False(recorder.IsRecording);
        Assert.Empty(chat.Messages);
    }

    [Fact]
    public async Task VoiceRecordingClearsRecordingStateBeforeAttachmentUploadCompletes()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var account = await runtime.Accounts.RegisterAsync("Alice");
        var remote = SessionId.CreateNew();
        var recorder = new DelayedVoiceMessageRecorder(new AttachmentMetadata(
            Guid.NewGuid().ToString("n"),
            "voice-delayed.m4a",
            "audio/mp4",
            4096,
            Duration: TimeSpan.FromSeconds(2)));

        var chat = new ChatViewModel(runtime, attachmentPicker: null, voiceRecorder: recorder);
        await chat.OpenOneToOneAsync(account, remote, "Bob");

        await chat.StartVoiceRecordingAsync();
        Assert.True(chat.IsRecordingVoice);

        var stopTask = chat.StopVoiceRecordingAndSendAsync();
        await recorder.StopStarted.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(chat.IsRecordingVoice);
        Assert.Empty(chat.Messages);

        recorder.CompleteStop();
        await stopTask;

        var message = Assert.Single(chat.Messages);
        Assert.True(message.IsVoiceMessage);
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
    public async Task ReceiveMarksFutureDatedIncomingMessageReadWithoutTrustingSenderClock()
    {
        var backend = new StubSessionBackend();
        var senderRuntime = ClientRuntime.CreateStubbed(
            clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T01:00:00Z")),
            backend: backend);
        var recipientClock = new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z"));
        var recipientRuntime = ClientRuntime.CreateStubbed(clock: recipientClock, backend: backend);
        var sender = await senderRuntime.Accounts.RegisterAsync("Sender");
        var recipient = await recipientRuntime.Accounts.RegisterAsync("Recipient");

        var senderChat = new ChatViewModel(senderRuntime);
        await senderChat.OpenOneToOneAsync(sender, recipient.SessionId, "Recipient");
        senderChat.Draft = "future sender clock";
        await senderChat.SendAsync();

        var recipientChat = new ChatViewModel(recipientRuntime);
        await recipientChat.OpenOneToOneAsync(recipient, sender.SessionId, "Sender");
        await recipientChat.ReceiveAsync();

        var item = Assert.Single(recipientChat.Messages);
        Assert.Equal(MessageDeliveryState.Read, item.State);
        Assert.Equal(
            recipientClock.UtcNow,
            await recipientRuntime.Messages.GetReadCursorAsync(ConversationId.ForOneToOne(sender.SessionId)));
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

        Assert.Equal(20, chat.Messages.Count);
        Assert.Equal("message-085", chat.Messages[0].Body);

        await chat.LoadOlderMessagesAsync();
        await chat.LoadOlderMessagesAsync();
        await chat.LoadOlderMessagesAsync();
        await chat.LoadOlderMessagesAsync();
        await chat.LoadOlderMessagesAsync();

        Assert.Equal(105, chat.Messages.Count);
        Assert.Equal("message-000", chat.Messages[0].Body);
        Assert.Equal("message-104", chat.Messages[^1].Body);
    }

    [Fact]
    public async Task UiCacheKeepsOnlyTheLatestMessagePage()
    {
        var clock = new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z"));
        var runtime = ClientRuntime.CreateStubbed(clock: clock);
        var account = await runtime.Accounts.RegisterAsync("Owner");
        var remote = SessionId.CreateNew();
        var conversation = await runtime.Conversations.GetOrCreateOneToOneAsync(remote, "Remote");
        var cache = new ChatOpenUiCache();

        for (var index = 0; index < 75; index++)
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

        var chat = new ChatViewModel(runtime, openCache: cache);
        await chat.OpenOneToOneAsync(account, remote, "Remote");
        await chat.LoadOlderMessagesAsync();
        await chat.LoadOlderMessagesAsync();
        await chat.LoadOlderMessagesAsync();
        Assert.Equal(75, chat.Messages.Count);

        var reopened = new ChatViewModel(runtime, openCache: cache);
        Assert.True(reopened.PrepareRoute(remote, "Remote"));
        Assert.Equal(20, reopened.Messages.Count);
        Assert.Equal("message-055", reopened.Messages[0].Body);
        Assert.Equal("message-074", reopened.Messages[^1].Body);
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

    private sealed class FakeVoiceMessageRecorder : IVoiceMessageRecorder
    {
        public bool IsSupported => true;

        public bool IsRecording { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            IsRecording = true;
            return Task.CompletedTask;
        }

        public Task<AttachmentMetadata?> StopAsync(CancellationToken cancellationToken = default)
        {
            IsRecording = false;
            return Task.FromResult<AttachmentMetadata?>(new AttachmentMetadata(
                Guid.NewGuid().ToString("n"),
                "voice.m4a",
                "audio/mp4",
                4096,
                Duration: TimeSpan.FromSeconds(2)));
        }

        public Task CancelAsync(CancellationToken cancellationToken = default)
        {
            IsRecording = false;
            return Task.CompletedTask;
        }
    }

    private sealed class DelayedVoiceMessageRecorder : IVoiceMessageRecorder
    {
        private readonly AttachmentMetadata attachment;
        private readonly TaskCompletionSource stopStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<AttachmentMetadata?> stopCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DelayedVoiceMessageRecorder(AttachmentMetadata attachment)
        {
            this.attachment = attachment;
        }

        public bool IsSupported => true;

        public bool IsRecording { get; private set; }

        public Task StopStarted => stopStarted.Task;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            IsRecording = true;
            return Task.CompletedTask;
        }

        public Task<AttachmentMetadata?> StopAsync(CancellationToken cancellationToken = default)
        {
            IsRecording = false;
            stopStarted.TrySetResult();
            return stopCompleted.Task;
        }

        public Task CancelAsync(CancellationToken cancellationToken = default)
        {
            IsRecording = false;
            stopCompleted.TrySetCanceled(cancellationToken);
            return Task.CompletedTask;
        }

        public void CompleteStop() => stopCompleted.TrySetResult(attachment);
    }

    private sealed class BlockingMessageTransport : ISessionMessageTransport, IAuthenticatedInboxTransport
    {
        private readonly TaskCompletionSource sendGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task SendAsync(OutboundMessageEnvelope envelope, CancellationToken cancellationToken = default) =>
            await sendGate.Task.WaitAsync(cancellationToken);

        public Task<IReadOnlyList<InboundMessageEnvelope>> ReceiveAsync(
            SessionId recipient,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InboundMessageEnvelope>>([]);

        public Task<IReadOnlyList<InboundMessageEnvelope>> ReceiveAuthenticatedAsync(
            SessionIdentityProvider identity,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InboundMessageEnvelope>>([]);

        public void Release() => sendGate.TrySetResult();
    }
}
