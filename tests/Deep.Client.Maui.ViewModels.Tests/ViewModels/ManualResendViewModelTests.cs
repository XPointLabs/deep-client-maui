using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Features;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Deep.Client.Maui.ViewModels.Tests.ViewModels;

public sealed class ManualResendViewModelTests
{
    [Fact]
    public async Task Direct_retry_sends_same_message_and_replaces_failed_bubble()
    {
        var backend = new StubSessionBackend();
        var runtime = ClientRuntime.CreateStubbed(backend: backend);
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var remote = SessionId.CreateNew();
        var message = await AppendFailedDirectAsync(runtime, owner.SessionId, remote, "retry direct");
        var viewModel = new ChatViewModel(runtime);
        await viewModel.OpenOneToOneAsync(owner, remote, "Remote");
        var failed = Assert.Single(viewModel.Messages);

        Assert.True(failed.IsRetryAvailable);
        Assert.True(viewModel.RetryMessageCommand.CanExecute(failed));
        await viewModel.RetryMessageCommand.ExecuteAsync(failed);

        var sent = Assert.Single(viewModel.Messages);
        Assert.Equal(message.Id, sent.Id);
        Assert.Equal(MessageDeliveryState.Sent, sent.State);
        Assert.False(sent.IsRetryAvailable);
        Assert.False(viewModel.RetryMessageCommand.CanExecute(sent));
    }

    [Fact]
    public async Task Direct_retry_failure_restores_authoritative_failed_state_without_sensitive_error()
    {
        const string sensitiveFailure = "transport-secret-capability";
        var transport = new ControlledMessageTransport(new IOException(sensitiveFailure));
        var runtime = CreateRuntime(transport);
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var remote = SessionId.CreateNew();
        await AppendFailedDirectAsync(runtime, owner.SessionId, remote, "retry failure");
        var viewModel = new ChatViewModel(runtime);
        await viewModel.OpenOneToOneAsync(owner, remote, "Remote");

        await viewModel.RetryMessageCommand.ExecuteAsync(Assert.Single(viewModel.Messages));

        var failed = Assert.Single(viewModel.Messages);
        Assert.Equal(MessageDeliveryState.Failed, failed.State);
        Assert.True(failed.IsRetryAvailable);
        Assert.Equal("Не удалось повторно отправить сообщение.", viewModel.ErrorMessage);
        Assert.DoesNotContain(sensitiveFailure, viewModel.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Direct_retry_command_is_single_flight_and_never_duplicates_the_bubble()
    {
        var transport = new ControlledMessageTransport();
        var runtime = CreateRuntime(transport);
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var remote = SessionId.CreateNew();
        var message = await AppendFailedDirectAsync(runtime, owner.SessionId, remote, "single flight");
        var viewModel = new ChatViewModel(runtime);
        await viewModel.OpenOneToOneAsync(owner, remote, "Remote");
        var failed = Assert.Single(viewModel.Messages);

        var first = viewModel.RetryMessageCommand.ExecuteAsync(failed);
        await transport.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MessageDeliveryState.Sending, Assert.Single(viewModel.Messages).State);
        Assert.False(viewModel.RetryMessageCommand.CanExecute(failed));

        await viewModel.RetryMessageCommand.ExecuteAsync(failed);
        transport.Release();
        await first;

        Assert.Equal(1, transport.SendCount);
        var sent = Assert.Single(viewModel.Messages);
        Assert.Equal(message.Id, sent.Id);
        Assert.Equal(MessageDeliveryState.Sent, sent.State);
    }

    [Fact]
    public async Task Direct_retry_completion_cannot_pollute_a_new_conversation_generation()
    {
        var transport = new ControlledMessageTransport();
        var runtime = CreateRuntime(transport);
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var firstRemote = SessionId.CreateNew();
        var secondRemote = SessionId.CreateNew();
        await AppendFailedDirectAsync(runtime, owner.SessionId, firstRemote, "old route");
        var viewModel = new ChatViewModel(runtime);
        await viewModel.OpenOneToOneAsync(owner, firstRemote, "First");

        var retry = viewModel.RetryMessageCommand.ExecuteAsync(Assert.Single(viewModel.Messages));
        await transport.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await viewModel.OpenOneToOneAsync(owner, secondRemote, "Second");
        transport.Release();
        await retry;

        Assert.Equal(secondRemote, viewModel.Counterpart);
        Assert.Empty(viewModel.Messages);
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Cancelled_retry_with_store_read_failure_stays_sending_until_terminal_reconciliation()
    {
        var innerStore = new InMemorySessionStore();
        var (store, proxy) = FailingMessageReadStoreProxy.Create(innerStore);
        var transport = new ControlledMessageTransport();
        var runtime = CreateRuntime(transport, store);
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var remote = SessionId.CreateNew();
        var message = await AppendFailedDirectAsync(runtime, owner.SessionId, remote, "cancelled waiter");
        var viewModel = new ChatViewModel(runtime);
        await viewModel.OpenOneToOneAsync(owner, remote, "Remote");
        using var cancellation = new CancellationTokenSource();

        var retry = viewModel.RetryMessageCommand.ExecuteAsync(
            Assert.Single(viewModel.Messages),
            cancellation.Token);
        await transport.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        proxy.FailMessageReads = true;
        cancellation.Cancel();
        await retry;

        var pending = Assert.Single(viewModel.Messages);
        Assert.Equal(message.Id, pending.Id);
        Assert.Equal(MessageDeliveryState.Sending, pending.State);
        Assert.False(pending.IsRetryAvailable);
        Assert.Equal("Статус повторной отправки уточняется.", viewModel.ErrorMessage);

        proxy.FailMessageReads = false;
        transport.Release();
        for (var attempt = 0; attempt < 40 && viewModel.Messages[0].State != MessageDeliveryState.Sent; attempt++)
        {
            await Task.Delay(50);
        }

        Assert.Equal(MessageDeliveryState.Sent, Assert.Single(viewModel.Messages).State);
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Direct_reload_exposes_only_owned_failed_outgoing_message()
    {
        var runtime = ClientRuntime.CreateStubbed();
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var remote = SessionId.CreateNew();
        var conversation = await runtime.Conversations.GetOrCreateOneToOneAsync(remote, "Remote");
        var ownFailed = await AppendFailedDirectAsync(runtime, owner.SessionId, remote, "reload");
        await ((IMessageRepository)runtime.Store).AppendAsync(new Message(
            MessageId.NewId(), conversation.Id, owner.SessionId, remote, "terminal",
            MessageDirection.Outgoing, MessageDeliveryState.Sent, runtime.Clock.UtcNow.AddSeconds(1), []));
        await ((IMessageRepository)runtime.Store).AppendAsync(new Message(
            MessageId.NewId(), conversation.Id, remote, owner.SessionId, "incoming",
            MessageDirection.Incoming, MessageDeliveryState.Failed, runtime.Clock.UtcNow.AddSeconds(2), []));
        await ((IMessageRepository)runtime.Store).AppendAsync(new Message(
            MessageId.NewId(), conversation.Id, remote, owner.SessionId, "wrong owner",
            MessageDirection.Outgoing, MessageDeliveryState.Failed, runtime.Clock.UtcNow.AddSeconds(3), []));

        var reloaded = new ChatViewModel(runtime);
        await reloaded.OpenOneToOneAsync(owner, remote, "Remote");

        var retryable = Assert.Single(reloaded.Messages, static item => item.IsRetryAvailable);
        Assert.Equal(ownFailed.Id, retryable.Id);
        Assert.All(
            reloaded.Messages.Where(item => item.Id != ownFailed.Id),
            item => Assert.False(reloaded.RetryMessageCommand.CanExecute(item)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Group_retry_uses_same_message_and_keeps_repeat_failure_retryable(bool fail)
    {
        var groupTransport = new ControlledGroupTransport { FailSend = fail };
        var runtime = ClientRuntime.CreateStubbed(groupSyncTransport: groupTransport);
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var member = SessionId.CreateNew();
        var group = await runtime.Conversations.CreateGroupScaffoldAsync(
            owner.SessionId,
            "Group",
            [member]);
        var message = new Message(
            MessageId.NewId(), group.Id, owner.SessionId, Recipient: null, "group retry",
            MessageDirection.Outgoing, MessageDeliveryState.Failed, runtime.Clock.UtcNow, [],
            NotifyRecipients: [member]);
        await ((IMessageRepository)runtime.Store).AppendAsync(message);
        var viewModel = new GroupChatViewModel(runtime);
        await viewModel.OpenFromRouteAsync(group.Id.Value, group.Name);
        var failed = Assert.Single(viewModel.Messages);

        Assert.True(failed.IsRetryAvailable);
        await viewModel.RetryMessageCommand.ExecuteAsync(failed);

        var result = Assert.Single(viewModel.Messages);
        Assert.Equal(message.Id, result.Id);
        Assert.Equal(fail ? MessageDeliveryState.Failed : MessageDeliveryState.Sent, result.State);
        Assert.Equal(fail, result.IsRetryAvailable);
        Assert.Equal(1, groupTransport.SendCount);
        if (fail)
        {
            Assert.Equal("Не удалось повторно отправить сообщение.", viewModel.ErrorMessage);
        }
    }

    [Fact]
    public async Task Group_retry_completion_cannot_pollute_a_new_group_generation()
    {
        var groupTransport = new ControlledGroupTransport { BlockSend = true };
        var runtime = ClientRuntime.CreateStubbed(groupSyncTransport: groupTransport);
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var member = SessionId.CreateNew();
        var first = await runtime.Conversations.CreateGroupScaffoldAsync(owner.SessionId, "First", [member]);
        var second = await runtime.Conversations.CreateGroupScaffoldAsync(owner.SessionId, "Second", [member]);
        var message = new Message(
            MessageId.NewId(), first.Id, owner.SessionId, Recipient: null, "old group",
            MessageDirection.Outgoing, MessageDeliveryState.Failed, runtime.Clock.UtcNow, [],
            NotifyRecipients: [member]);
        await ((IMessageRepository)runtime.Store).AppendAsync(message);
        var viewModel = new GroupChatViewModel(runtime);
        await viewModel.OpenFromRouteAsync(first.Id.Value, first.Name);

        var retry = viewModel.RetryMessageCommand.ExecuteAsync(Assert.Single(viewModel.Messages));
        await groupTransport.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await viewModel.OpenFromRouteAsync(second.Id.Value, second.Name);
        groupTransport.Release();
        await retry;

        Assert.Equal(second.Name, viewModel.GroupTitle);
        Assert.Empty(viewModel.Messages);
        Assert.Equal("Группа загружена.", viewModel.StatusMessage);
        Assert.Null(viewModel.ErrorMessage);
    }

    private static ClientRuntime CreateRuntime(ISessionMessageTransport transport) =>
        CreateRuntime(transport, new InMemorySessionStore());

    private static ClientRuntime CreateRuntime(
        ISessionMessageTransport transport,
        ILocalSessionStore store) =>
        new(
            store,
            ClientFeatureFlags.Defaults,
            new FrozenClock(DateTimeOffset.Parse("2026-08-02T00:00:00Z")),
            transport);

    private static async Task<Message> AppendFailedDirectAsync(
        ClientRuntime runtime,
        SessionId owner,
        SessionId remote,
        string body)
    {
        var conversation = await runtime.Conversations.GetOrCreateOneToOneAsync(remote, "Remote");
        var message = new Message(
            MessageId.NewId(), conversation.Id, owner, remote, body,
            MessageDirection.Outgoing, MessageDeliveryState.Failed, runtime.Clock.UtcNow, []);
        await ((IMessageRepository)runtime.Store).AppendAsync(message);
        return message;
    }

    private sealed class ControlledMessageTransport(Exception? failure = null) : ISessionMessageTransport
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SendStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SendCount { get; private set; }

        public async Task SendAsync(
            OutboundMessageEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            SendCount++;
            SendStarted.TrySetResult();
            if (failure is not null)
            {
                throw failure;
            }

            await release.Task.WaitAsync(cancellationToken);
        }

        public Task<IReadOnlyList<InboundMessageEnvelope>> ReceiveAsync(
            SessionId recipient,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InboundMessageEnvelope>>([]);

        public void Release() => release.TrySetResult();
    }

    private sealed class ControlledGroupTransport : IGroupSyncTransport
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool FailSend { get; init; }

        public bool BlockSend { get; init; }

        public TaskCompletionSource SendStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SendCount { get; private set; }

        public Task PublishGroupStateAsync(
            Group group,
            DateTimeOffset updatedAt,
            IEnumerable<SessionId>? recipients = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<InboundGroupStateEnvelope>> ReceiveGroupStatesAsync(
            SessionId member,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InboundGroupStateEnvelope>>([]);

        public async Task SendGroupMessageAsync(
            OutboundGroupMessageEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            SendCount++;
            SendStarted.TrySetResult();
            if (FailSend)
            {
                throw new IOException("group-transport-secret");
            }

            if (BlockSend)
            {
                await release.Task.WaitAsync(cancellationToken);
            }
        }

        public Task<IReadOnlyList<InboundGroupMessageEnvelope>> ReceiveGroupMessagesAsync(
            ConversationId groupId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InboundGroupMessageEnvelope>>([]);

        public void Release() => release.TrySetResult();
    }
}

public class FailingMessageReadStoreProxy : DispatchProxy
{
    private ILocalSessionStore inner = null!;

    public bool FailMessageReads { get; set; }

    public static (ILocalSessionStore Store, FailingMessageReadStoreProxy Proxy) Create(
        ILocalSessionStore inner)
    {
        var store = DispatchProxy.Create<ILocalSessionStore, FailingMessageReadStoreProxy>();
        var proxy = (FailingMessageReadStoreProxy)(object)store;
        proxy.inner = inner;
        return (store, proxy);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        args ??= [];
        if (FailMessageReads
            && targetMethod.Name == nameof(IMessageRepository.GetAsync)
            && targetMethod.GetParameters().FirstOrDefault()?.ParameterType == typeof(MessageId))
        {
            return Task.FromException<Message?>(new IOException("store-read-secret"));
        }

        try
        {
            return targetMethod.Invoke(inner, args);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            return null;
        }
    }
}
