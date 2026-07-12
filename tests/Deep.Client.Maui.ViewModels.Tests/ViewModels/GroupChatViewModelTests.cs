using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.ViewModels.Tests.ViewModels;

public sealed class GroupChatViewModelTests
{
    [Fact]
    public async Task RefreshReconcilesGroupMessageAlreadyPersistedByBackgroundSync()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-07-13T00:00:00Z")));
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var member = SessionId.CreateNew();
        var group = await runtime.Conversations.CreateGroupScaffoldAsync(owner.SessionId, "Background", [member]);
        var chat = new GroupChatViewModel(runtime);
        await chat.OpenFromRouteAsync(group.Id.Value, group.Name);
        var incoming = new Message(
            MessageId.NewId(),
            group.Id,
            member,
            Recipient: null,
            "background group persisted",
            MessageDirection.Incoming,
            MessageDeliveryState.Delivered,
            runtime.Clock.UtcNow,
            []);
        await ((IMessageRepository)runtime.Store).AppendAsync(incoming);

        await chat.RefreshAsync();

        var visible = Assert.Single(chat.Messages);
        Assert.Equal(incoming.Id, visible.Id);
        Assert.Equal(MessageDeliveryState.Read, visible.State);
    }

    [Fact]
    public async Task RefreshKeepsOlderPersistedGroupMessagesReachableAfterEmptyOpen()
    {
        var start = DateTimeOffset.Parse("2026-07-13T00:00:00Z");
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(start.AddHours(1)));
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var member = SessionId.CreateNew();
        var group = await runtime.Conversations.CreateGroupScaffoldAsync(owner.SessionId, "Background", [member]);
        var chat = new GroupChatViewModel(runtime);
        await chat.OpenFromRouteAsync(group.Id.Value, group.Name);
        for (var index = 0; index < 21; index++)
        {
            await ((IMessageRepository)runtime.Store).AppendAsync(new Message(
                MessageId.NewId(),
                group.Id,
                member,
                Recipient: null,
                $"persisted group {index}",
                MessageDirection.Incoming,
                MessageDeliveryState.Delivered,
                start.AddMinutes(index),
                []));
        }

        await chat.RefreshAsync();
        Assert.Equal(20, chat.Messages.Count);

        await chat.LoadOlderMessagesAsync();

        Assert.Equal(21, chat.Messages.Count);
        Assert.Contains(chat.Messages, message => message.Body == "persisted group 0");
    }

    [Fact]
    public async Task RefreshRebuildsDisjointRecentGroupWindowWithoutLosingHistory()
    {
        var start = DateTimeOffset.Parse("2026-07-13T00:00:00Z");
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(start.AddHours(2)));
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var member = SessionId.CreateNew();
        var group = await runtime.Conversations.CreateGroupScaffoldAsync(owner.SessionId, "Background", [member]);
        await ((IMessageRepository)runtime.Store).AppendAsync(new Message(
            MessageId.NewId(), group.Id, member, Recipient: null, "loaded group old",
            MessageDirection.Incoming, MessageDeliveryState.Delivered, start, []));
        var chat = new GroupChatViewModel(runtime);
        await chat.OpenFromRouteAsync(group.Id.Value, group.Name);
        for (var index = 1; index <= 21; index++)
        {
            await ((IMessageRepository)runtime.Store).AppendAsync(new Message(
                MessageId.NewId(), group.Id, member, Recipient: null, $"group burst {index}",
                MessageDirection.Incoming, MessageDeliveryState.Delivered, start.AddMinutes(index), []));
        }

        await chat.RefreshAsync();
        Assert.Equal(20, chat.Messages.Count);

        await chat.LoadOlderMessagesAsync();

        Assert.Equal(22, chat.Messages.Count);
        Assert.Contains(chat.Messages, message => message.Body == "loaded group old");
        Assert.Contains(chat.Messages, message => message.Body == "group burst 1");
    }

    [Fact]
    public async Task SendCommandAppendsGroupMessage()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var group = await runtime.Conversations.CreateGroupScaffoldAsync(owner.SessionId, "Test Group", [], CancellationToken.None);

        var viewModel = new GroupChatViewModel(runtime);
        await viewModel.OpenFromRouteAsync(group.Id.Value, group.Name);
        viewModel.Draft = "hello group";

        await viewModel.SendCommand.ExecuteAsync();

        Assert.Single(viewModel.Messages);
        Assert.Equal("hello group", viewModel.Messages[0].Body);
        Assert.True(string.IsNullOrWhiteSpace(viewModel.Draft));
    }

    [Fact]
    public async Task PickAttachmentsCommandAllowsAttachmentOnlyGroupMessage()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var group = await runtime.Conversations.CreateGroupScaffoldAsync(owner.SessionId, "Files", [], CancellationToken.None);
        var picker = new FakeAttachmentPicker();

        var viewModel = new GroupChatViewModel(runtime, picker);
        await viewModel.OpenFromRouteAsync(group.Id.Value, group.Name);

        await viewModel.PickAttachmentsAsync();

        Assert.True(viewModel.HasStagedAttachments);
        Assert.Equal("group-note.txt", viewModel.StagedAttachmentSummary);
        Assert.True(viewModel.SendCommand.CanExecute(null));

        await viewModel.SendAsync();

        Assert.Single(viewModel.Messages);
        Assert.Equal("[Вложение]", viewModel.Messages[0].Body);
        Assert.True(viewModel.Messages[0].HasAttachments);
        Assert.False(viewModel.HasStagedAttachments);
    }

    [Fact]
    public async Task VoiceRecordingQueuesGroupAudioAttachment()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var group = await runtime.Conversations.CreateGroupScaffoldAsync(owner.SessionId, "Voice", [], CancellationToken.None);
        var recorder = new FakeVoiceMessageRecorder();

        var viewModel = new GroupChatViewModel(runtime, voiceRecorder: recorder);
        await viewModel.OpenFromRouteAsync(group.Id.Value, group.Name);

        await viewModel.StartVoiceRecordingAsync();
        Assert.True(viewModel.IsRecordingVoice);

        await viewModel.StopVoiceRecordingAndSendAsync();

        var message = Assert.Single(viewModel.Messages);
        Assert.True(message.IsVoiceMessage);
        Assert.False(message.HasVisibleBody);
        Assert.Equal("Голосовое сообщение", message.AttachmentTitle);
        Assert.Equal("00:03", message.VoiceDurationLabel);
    }

    [Fact]
    public async Task CancelVoiceRecordingStopsRecorderWithoutQueueingGroupMessage()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var group = await runtime.Conversations.CreateGroupScaffoldAsync(owner.SessionId, "Voice", [], CancellationToken.None);
        var recorder = new FakeVoiceMessageRecorder();
        var viewModel = new GroupChatViewModel(runtime, voiceRecorder: recorder);
        await viewModel.OpenFromRouteAsync(group.Id.Value, group.Name);

        await viewModel.StartVoiceRecordingAsync();
        await viewModel.CancelVoiceRecordingAsync();

        Assert.False(viewModel.IsRecordingVoice);
        Assert.False(recorder.IsRecording);
        Assert.Empty(viewModel.Messages);
    }


    [Fact]
    public async Task AddPromoteAndRemoveMemberUpdatesGroupMembers()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var group = await runtime.Conversations.CreateGroupScaffoldAsync(owner.SessionId, "Admins", [], CancellationToken.None);
        var member = "05" + new string('b', 64);

        var viewModel = new GroupChatViewModel(runtime);
        await viewModel.OpenFromRouteAsync(group.Id.Value, group.Name);

        viewModel.MemberSessionId = member;
        await viewModel.AddMemberCommand.ExecuteAsync();
        Assert.Contains(viewModel.Members, item => item.SessionId.Value == member && item.Role == Deep.Client.Shared.Domain.GroupMemberRole.Standard);

        viewModel.MemberSessionId = member;
        await viewModel.PromoteMemberCommand.ExecuteAsync();
        Assert.Contains(viewModel.Members, item => item.SessionId.Value == member && item.Role == Deep.Client.Shared.Domain.GroupMemberRole.Admin);

        viewModel.MemberSessionId = member;
        await viewModel.RemoveMemberCommand.ExecuteAsync();
        Assert.DoesNotContain(viewModel.Members, item => item.SessionId.Value == member);
    }

    [Fact]
    public async Task MembersUseKnownDisplayNameAndShortUnknownFallback()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var knownMember = SessionId.Parse("05" + new string('4', 64));
        var unknownMember = SessionId.Parse("05" + new string('5', 64));
        await runtime.Conversations.GetOrCreateOneToOneAsync(knownMember, "Bob", approve: true);
        var group = await runtime.Conversations.CreateGroupScaffoldAsync(
            owner.SessionId,
            "Named members",
            [knownMember, unknownMember],
            CancellationToken.None);
        var viewModel = new GroupChatViewModel(runtime);

        await viewModel.OpenFromRouteAsync(group.Id.Value, group.Name);

        Assert.Equal("Bob", viewModel.Members.Single(item => item.SessionId == knownMember).DisplayName);
        Assert.Equal("055555...5555", viewModel.Members.Single(item => item.SessionId == unknownMember).DisplayName);
    }

    [Fact]
    public async Task MemberDisplayNameRefreshAppliesRepeatedRenames()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var member = SessionId.Parse("05" + new string('6', 64));
        await runtime.Conversations.GetOrCreateOneToOneAsync(member, "Initial", approve: true);
        var group = await runtime.Conversations.CreateGroupScaffoldAsync(owner.SessionId, "Renames", [member], CancellationToken.None);
        var viewModel = new GroupChatViewModel(runtime);
        await viewModel.OpenFromRouteAsync(group.Id.Value, group.Name);

        await runtime.Conversations.UpdateContactDisplayNameAsync(member, "Renamed Once");
        await viewModel.RefreshContactDisplayNamesAsync();
        Assert.Equal("Renamed Once", viewModel.Members.Single(item => item.SessionId == member).DisplayName);

        await runtime.Conversations.UpdateContactDisplayNameAsync(member, "Renamed Twice");
        await viewModel.RefreshAsync();
        Assert.Equal("Renamed Twice", viewModel.Members.Single(item => item.SessionId == member).DisplayName);
    }

    [Fact]
    public async Task VisibleIncomingSenderLabelRefreshesAfterContactRename()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var member = SessionId.Parse("05" + new string('7', 64));
        await runtime.Conversations.GetOrCreateOneToOneAsync(member, "Initial", approve: true);
        var group = await runtime.Conversations.CreateGroupScaffoldAsync(owner.SessionId, "Sender labels", [member]);
        await ((IMessageRepository)runtime.Store).AppendAsync(new Message(
            MessageId.NewId(),
            group.Id,
            member,
            Recipient: null,
            "hello",
            MessageDirection.Incoming,
            MessageDeliveryState.Delivered,
            runtime.Clock.UtcNow,
            []));
        var viewModel = new GroupChatViewModel(runtime);
        await viewModel.OpenFromRouteAsync(group.Id.Value, group.Name);
        var visibleMessage = Assert.Single(viewModel.Messages);
        Assert.Equal("Initial", visibleMessage.SenderLabel);

        await runtime.Conversations.UpdateContactDisplayNameAsync(member, "Renamed");
        await viewModel.RefreshContactDisplayNamesAsync();

        Assert.Same(visibleMessage, Assert.Single(viewModel.Messages));
        Assert.Equal("Renamed", visibleMessage.SenderLabel);
    }

    [Fact]
    public async Task DemoteLastAdminShowsStatusError()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var group = await runtime.Conversations.CreateGroupScaffoldAsync(owner.SessionId, "Admins", [], CancellationToken.None);

        var viewModel = new GroupChatViewModel(runtime);
        await viewModel.OpenFromRouteAsync(group.Id.Value, group.Name);

        viewModel.MemberSessionId = owner.SessionId.Value;
        await viewModel.DemoteMemberCommand.ExecuteAsync();

        Assert.Equal("Нельзя понизить последнего администратора.", viewModel.StatusMessage);
        Assert.True(viewModel.IsStatusError);
    }

    [Fact]
    public async Task RefreshWithoutGroupChangesDoesNotResetMessagesOrMembers()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var group = await runtime.Conversations.CreateGroupScaffoldAsync(owner.SessionId, "Stable", [], CancellationToken.None);

        var viewModel = new GroupChatViewModel(runtime);
        await viewModel.OpenFromRouteAsync(group.Id.Value, group.Name);
        var messageChanges = 0;
        var memberChanges = 0;
        viewModel.Messages.CollectionChanged += (_, _) => messageChanges++;
        viewModel.Members.CollectionChanged += (_, _) => memberChanges++;

        await viewModel.RefreshAsync();

        Assert.Equal(0, messageChanges);
        Assert.Equal(0, memberChanges);
        Assert.Single(viewModel.Members);
    }

    [Fact]
    public async Task ReplyAndReactionUpdateGroupChatPresentation()
    {
        var runtime = ClientRuntime.CreateStubbed();
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var group = await runtime.Conversations.CreateGroupScaffoldAsync(owner.SessionId, "Replies", []);
        var viewModel = new GroupChatViewModel(runtime);
        await viewModel.OpenFromRouteAsync(group.Id.Value, group.Name);
        viewModel.Draft = "original";
        await viewModel.SendAsync();
        var original = Assert.Single(viewModel.Messages);

        viewModel.BeginReply(original);
        viewModel.Draft = "reply";
        await viewModel.SendAsync();
        var reply = viewModel.Messages.Single(message => message.Body == "reply");
        await viewModel.ToggleReactionAsync(reply, "❤️");
        var updatedReply = viewModel.Messages.Single(message => message.Body == "reply");

        Assert.Equal(original.Id, updatedReply.ReplyTo?.MessageId);
        var reaction = Assert.Single(updatedReply.ReactionChips);
        Assert.Equal(reply.Id, reaction.MessageId);
        Assert.Equal("❤️", reaction.Emoji);
        Assert.Equal(1, reaction.Count);
        await viewModel.ToggleReactionAsync(reply, "❤️");
        Assert.Empty(viewModel.Messages.Single(message => message.Body == "reply").ReactionChips);
        Assert.False(viewModel.IsReplying);
    }

    private sealed class FakeAttachmentPicker : IAttachmentPickerService
    {
        public Task<IReadOnlyList<AttachmentMetadata>> PickAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AttachmentMetadata>>(
            [
                AttachmentMetadata.Local("group-note.txt", "text/plain", 256)
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
                "group-voice.m4a",
                "audio/mp4",
                6144,
                Duration: TimeSpan.FromSeconds(3)));
        }

        public Task CancelAsync(CancellationToken cancellationToken = default)
        {
            IsRecording = false;
            return Task.CompletedTask;
        }
    }
}
