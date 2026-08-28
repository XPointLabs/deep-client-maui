using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Features;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.ViewModels.Tests.ViewModels;

public sealed class ConversationsViewModelTests
{
    [Fact]
    public async Task StartConversationCommandCreatesOneToOneConversation()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        await runtime.Accounts.RegisterAsync("Owner");
        var recipient = "05" + new string('a', 64);

        var viewModel = new ConversationsViewModel(runtime)
        {
            NewSessionId = recipient,
            NewDisplayName = "Alice"
        };

        await viewModel.StartConversationCommand.ExecuteAsync();

        Assert.Single(viewModel.Conversations);
        Assert.Equal("Alice", viewModel.Conversations[0].Title);
        Assert.True(string.IsNullOrWhiteSpace(viewModel.NewSessionId));
    }

    [Fact]
    public void StartConversationCommandRejectsInvalidSessionId()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var viewModel = new ConversationsViewModel(runtime)
        {
            NewSessionId = "invalid"
        };

        Assert.False(viewModel.StartConversationCommand.CanExecute(null));
    }

    [Fact]
    public async Task ProtectedInvitationIsPreparedBeforeLocalContactIsCreated()
    {
        var runtime = ClientRuntime.CreateStubbed(
            clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        await runtime.Accounts.RegisterAsync("Owner");
        var recipient = SessionId.CreateNew();
        var onboarding = new RecordingContactOnboarding("protected-invite", recipient);
        var viewModel = new ConversationsViewModel(runtime, onboarding)
        {
            NewSessionId = "protected-invite",
            NewDisplayName = "Verified"
        };

        var conversation = await viewModel.StartConversationFromComposerAsync();

        Assert.NotNull(conversation);
        Assert.Equal(recipient.Value, conversation.Id.Value);
        Assert.Equal(1, onboarding.PrepareCalls);
        Assert.Single(await runtime.Conversations.ListAsync());
    }

    [Fact]
    public async Task FailedInvitationDoesNotCreateLocalContactOrConversation()
    {
        var runtime = ClientRuntime.CreateStubbed(
            clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        await runtime.Accounts.RegisterAsync("Owner");
        var onboarding = new RecordingContactOnboarding(
            "protected-invite",
            SessionId.CreateNew(),
            fail: true);
        var viewModel = new ConversationsViewModel(runtime, onboarding)
        {
            NewSessionId = "protected-invite"
        };

        var conversation = await viewModel.StartConversationFromComposerAsync();

        Assert.Null(conversation);
        Assert.NotNull(viewModel.ErrorMessage);
        Assert.Empty(await runtime.Conversations.ListAsync());
        var contacts = new List<Contact>();
        await foreach (var contact in ((IContactRepository)runtime.Store).ListAsync())
            contacts.Add(contact);
        Assert.DoesNotContain(contacts, contact => contact.Id == onboarding.Recipient);
    }

    [Fact]
    public async Task SyncWithoutConversationChangesDoesNotResetCollection()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        await runtime.Accounts.RegisterAsync("Owner");
        await runtime.Conversations.GetOrCreateOneToOneAsync(Deep.Client.Shared.Domain.SessionId.CreateNew(), "Alice");
        var viewModel = new ConversationsViewModel(runtime);

        await viewModel.LoadAsync();
        var changeCount = 0;
        viewModel.Conversations.CollectionChanged += (_, _) => changeCount++;

        await viewModel.SyncAsync();

        Assert.Equal(0, changeCount);
        Assert.Single(viewModel.Conversations);
    }

    [Fact]
    public async Task LoadCachedAsyncKeepsGlobalBusyStateOff()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        await runtime.Accounts.RegisterAsync("Owner");
        await runtime.Conversations.GetOrCreateOneToOneAsync(SessionId.CreateNew(), "Alice");
        var viewModel = new ConversationsViewModel(runtime);

        await viewModel.LoadCachedAsync();

        Assert.False(viewModel.IsBusy);
        Assert.Single(viewModel.Conversations);
    }

    [Fact]
    public async Task SyncUpdatesUnreadCountFromReadCursorWithoutConversationUpdate()
    {
        var clock = new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z"));
        var runtime = ClientRuntime.CreateStubbed(clock: clock);
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var remote = SessionId.CreateNew();
        var conversation = await runtime.Conversations.GetOrCreateOneToOneAsync(remote, "Remote");
        await ((IMessageRepository)runtime.Store).AppendAsync(new Message(
            MessageId.NewId(),
            conversation.Id,
            remote,
            owner.SessionId,
            "unread",
            MessageDirection.Incoming,
            MessageDeliveryState.Delivered,
            clock.UtcNow,
            []));
        var viewModel = new ConversationsViewModel(runtime);

        await viewModel.LoadAsync();
        await runtime.Messages.MarkConversationAsReadAsync(conversation.Id, clock.UtcNow.AddSeconds(1));
        await viewModel.SyncAsync();

        Assert.Single(viewModel.Conversations);
        Assert.Equal(0, viewModel.Conversations[0].UnreadCount);
        Assert.False(viewModel.Conversations[0].IsUnread);
    }

    [Fact]
    public async Task SyncShowsUnknownSenderAsMessageRequestInConversationList()
    {
        var backend = new StubSessionBackend();
        var senderRuntime = ClientRuntime.CreateStubbed(backend: backend);
        var recipientRuntime = ClientRuntime.CreateStubbed(backend: backend);
        var sender = await senderRuntime.Accounts.RegisterAsync("Unknown sender");
        var recipient = await recipientRuntime.Accounts.RegisterAsync("Recipient");
        await senderRuntime.Messages.SendOneToOneAsync(sender.SessionId, recipient.SessionId, "hello");
        var viewModel = new ConversationsViewModel(recipientRuntime);

        await viewModel.SyncAsync();

        var conversation = Assert.Single(viewModel.Conversations);
        Assert.Equal(sender.SessionId.Value, conversation.Id.Value);
        Assert.Equal("hello", conversation.LastMessagePreview);
        Assert.Equal(1, conversation.UnreadCount);
        Assert.True(conversation.IsMessageRequest);
    }

    [Fact]
    public async Task SyncReturnsFalseWhenInboxTransportFails()
    {
        var runtime = new ClientRuntime(
            new InMemorySessionStore(),
            ClientFeatureFlags.Defaults,
            new FrozenClock(DateTimeOffset.Parse("2026-07-11T00:00:00Z")),
            new FailingInboxTransport());
        await runtime.Accounts.RegisterAsync("Owner");
        var viewModel = new ConversationsViewModel(runtime);

        var synchronized = await viewModel.SyncAsync();

        Assert.False(synchronized);
        Assert.Equal("offline", viewModel.ErrorMessage);
        Assert.Equal("invalid-state", viewModel.SyncFailureCode);
    }

    [Theory]
    [InlineData(401, "http-401")]
    [InlineData(503, "http-503")]
    public void SyncFailureClassifierReturnsClosedSanitizedCodes(int status, string expected)
    {
        var http = new HttpRequestException("sensitive endpoint", null, (System.Net.HttpStatusCode)status);
        Assert.Equal(expected, SyncFailureCodeClassifier.Classify(http));
        Assert.Equal("tls", SyncFailureCodeClassifier.Classify(
            new HttpRequestException("outer", new System.Security.Authentication.AuthenticationException("secret"))));
        Assert.Equal("runtime-policy", SyncFailureCodeClassifier.Classify(
            new InvalidDataException("secret policy detail")));
        Assert.Equal("local-access", SyncFailureCodeClassifier.Classify(
            new UnauthorizedAccessException("secret path")));
        Assert.Equal("invalid-state", SyncFailureCodeClassifier.Classify(
            new InvalidOperationException("secret")));
        Assert.Equal("mailbox-8", SyncFailureCodeClassifier.Classify(
            new ClientMailboxTransportException(
                ClientMailboxTransportFailure.DependencyUnavailable,
                retryable: true,
                "secret upstream")));
    }

    [Fact]
    public void SyncFailureClassifierNeverIncludesExceptionDetails()
    {
        const string sensitive = "https://private.example/users/alice?message=secret";
        var exceptions = new Exception[]
        {
            new HttpRequestException(sensitive),
            new InvalidDataException(sensitive),
            new IOException(sensitive),
            new Exception(sensitive)
        };

        foreach (var exception in exceptions)
        {
            var code = SyncFailureCodeClassifier.Classify(exception);
            Assert.DoesNotContain("private", code, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("alice", code, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", code, StringComparison.OrdinalIgnoreCase);
            Assert.Matches("^[a-z]+(?:-[a-z]+|-[0-9]{1,3})?$", code);
        }
    }

    private sealed class FailingInboxTransport : ISessionMessageTransport
    {
        public Task SendAsync(OutboundMessageEnvelope envelope, CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("offline"));

        public Task<IReadOnlyList<InboundMessageEnvelope>> ReceiveAsync(
            SessionId recipient,
            CancellationToken cancellationToken = default) =>
            Task.FromException<IReadOnlyList<InboundMessageEnvelope>>(new InvalidOperationException("offline"));
    }

    private sealed class RecordingContactOnboarding(
        string acceptedInput,
        SessionId recipient,
        bool fail = false) : IContactMailboxOnboarding
    {
        public SessionId Recipient => recipient;
        public int PrepareCalls { get; private set; }

        public bool CanAccept(string contactInput) =>
            string.Equals(contactInput, acceptedInput, StringComparison.Ordinal);

        public Task<SessionId> PrepareAsync(
            string contactInput,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrepareCalls++;
            if (!CanAccept(contactInput) || fail)
                throw new InvalidDataException("unverified contact");
            return Task.FromResult(recipient);
        }
    }
}
