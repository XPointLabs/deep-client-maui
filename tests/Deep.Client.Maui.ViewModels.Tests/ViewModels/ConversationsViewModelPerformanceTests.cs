using System.Collections.Specialized;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Features;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.ViewModels.Tests.ViewModels;

public sealed class ConversationsViewModelPerformanceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-05-28T00:00:00Z");

    [Fact]
    public async Task SyncDoesNotFallbackForNullFieldsInExistingSummary()
    {
        var (runtime, store) = CreateRuntime();
        using (runtime)
        {
            await SeedFallbackDataAsync(runtime);
            store.SnapshotTransform = snapshot => snapshot with
            {
                Summaries = snapshot.Summaries.ToDictionary(
                    static pair => pair.Key,
                    static pair => new ConversationListSummary(
                        pair.Key,
                        ReadCursor: null,
                        LastMessage: null,
                        UnreadCount: 7,
                        Contact: pair.Value.Contact))
            };
            store.ResetFallbackCounts();
            var viewModel = new ConversationsViewModel(runtime);

            await viewModel.LoadCachedAsync();

            Assert.True(Assert.Single(viewModel.Conversations).IsMessageRequest);
            store.SnapshotTransform = snapshot => snapshot with
            {
                Summaries = snapshot.Summaries.ToDictionary(
                    static pair => pair.Key,
                    static pair => new ConversationListSummary(
                        pair.Key,
                        ReadCursor: null,
                        LastMessage: null,
                        UnreadCount: 7,
                        Contact: null))
            };
            await viewModel.SyncAsync();

            var item = Assert.Single(viewModel.Conversations);
            Assert.Null(item.LastMessageId);
            Assert.DoesNotContain("fallback message", item.LastMessagePreview, StringComparison.Ordinal);
            Assert.Equal(7, item.UnreadCount);
            Assert.True(item.IsUnread);
            Assert.False(item.IsMessageRequest);
            Assert.Equal(0, store.ReadCursorFallbackCount);
            Assert.Equal(0, store.RecentMessagesFallbackCount);
            Assert.Equal(0, store.UnreadCountFallbackCount);
            Assert.Equal(0, store.ContactFallbackCount);
        }
    }

    [Fact]
    public async Task LoadCachedAsyncFallsBackWhenConversationSummaryIsMissing()
    {
        var (runtime, store) = CreateRuntime();
        using (runtime)
        {
            var (_, message) = await SeedFallbackDataAsync(runtime);
            store.SnapshotTransform = snapshot => snapshot with
            {
                Summaries = new Dictionary<ConversationId, ConversationListSummary>()
            };
            store.ResetFallbackCounts();
            var viewModel = new ConversationsViewModel(runtime);

            await viewModel.LoadCachedAsync();

            var item = Assert.Single(viewModel.Conversations);
            Assert.Equal(message.Id.Value, item.LastMessageId);
            Assert.Equal("fallback message", item.LastMessagePreview);
            Assert.Equal(0, item.UnreadCount);
            Assert.False(item.IsUnread);
            Assert.True(item.IsMessageRequest);
            Assert.Equal(1, store.ReadCursorFallbackCount);
            Assert.Equal(1, store.RecentMessagesFallbackCount);
            Assert.Equal(1, store.UnreadCountFallbackCount);
            Assert.Equal(1, store.ContactFallbackCount);
        }
    }

    [Fact]
    public async Task LoadCachedAsyncReplacesOnlyChangedItemWhenOrderIsStable()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(Now));
        using (runtime)
        {
            var (_, second) = await SeedOrderedConversationsAsync(runtime);
            var viewModel = new ConversationsViewModel(runtime);
            await viewModel.LoadCachedAsync();
            var selected = viewModel.Conversations.Single(item => item.Id == second.Id);
            viewModel.SelectedConversation = selected;
            var selectedIndex = viewModel.Conversations.IndexOf(
                viewModel.Conversations.Single(item => item.Id == second.Id));
            var changes = new List<NotifyCollectionChangedEventArgs>();
            viewModel.Conversations.CollectionChanged += (_, args) => changes.Add(args);

            await ((IConversationRepository)runtime.Store).UpsertAsync(
                second with { DisplayName = "Bob renamed" });
            await viewModel.LoadCachedAsync();

            var change = Assert.Single(changes);
            Assert.Equal(NotifyCollectionChangedAction.Replace, change.Action);
            Assert.Equal(selectedIndex, change.NewStartingIndex);
            var renamed = viewModel.Conversations.Single(item => item.Id == second.Id);
            Assert.Equal("Bob renamed", renamed.Title);
            Assert.True(renamed.IsSelected);
            Assert.Equal(second.Id, viewModel.SelectedConversation?.Id);
        }
    }

    [Fact]
    public async Task SelectionRemainsCorrectAcrossFilterChanges()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(Now));
        using (runtime)
        {
            var (first, second) = await SeedOrderedConversationsAsync(runtime);
            var viewModel = new ConversationsViewModel(runtime);
            await viewModel.LoadCachedAsync();
            viewModel.SelectedConversation = viewModel.Conversations.Single(item => item.Id == second.Id);

            viewModel.SearchQuery = "Bob";

            Assert.True(Assert.Single(viewModel.Conversations).IsSelected);

            viewModel.SearchQuery = "Alice";

            var filtered = Assert.Single(viewModel.Conversations);
            Assert.Equal(first.Id, filtered.Id);
            Assert.False(filtered.IsSelected);
            Assert.Equal(second.Id, viewModel.SelectedConversation?.Id);

            viewModel.SearchQuery = string.Empty;

            Assert.Equal(2, viewModel.Conversations.Count);
            Assert.True(viewModel.Conversations.Single(item => item.Id == second.Id).IsSelected);
            Assert.False(viewModel.Conversations.Single(item => item.Id == first.Id).IsSelected);
        }
    }

    private static (ClientRuntime Runtime, ConversationSummaryStoreProxy Store) CreateRuntime()
    {
        var inner = new InMemorySessionStore();
        var (store, proxy) = ConversationSummaryStoreProxy.Create(inner);
        var runtime = new ClientRuntime(
            store,
            ClientFeatureFlags.Defaults,
            new FrozenClock(Now),
            new StubSessionBackend());
        return (runtime, proxy);
    }

    private static async Task<(Conversation Conversation, Message Message)> SeedFallbackDataAsync(ClientRuntime runtime)
    {
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var remote = SessionId.CreateNew();
        var conversation = await runtime.Conversations.GetOrCreateOneToOneAsync(remote, "Remote");
        var message = new Message(
            MessageId.NewId(),
            conversation.Id,
            remote,
            owner.SessionId,
            "fallback message",
            MessageDirection.Incoming,
            MessageDeliveryState.Delivered,
            Now,
            []);
        await ((IMessageRepository)runtime.Store).AppendAsync(message);
        await runtime.Messages.MarkConversationAsReadAsync(conversation.Id, Now.AddMinutes(1));
        return (conversation, message);
    }

    private static async Task<(Conversation First, Conversation Second)> SeedOrderedConversationsAsync(ClientRuntime runtime)
    {
        await runtime.Accounts.RegisterAsync("Owner");
        var first = await runtime.Conversations.GetOrCreateOneToOneAsync(SessionId.CreateNew(), "Alice");
        var second = await runtime.Conversations.GetOrCreateOneToOneAsync(SessionId.CreateNew(), "Bob");
        first = first with { UpdatedAt = Now.AddMinutes(2) };
        second = second with { UpdatedAt = Now.AddMinutes(1) };
        await ((IConversationRepository)runtime.Store).UpsertAsync(first);
        await ((IConversationRepository)runtime.Store).UpsertAsync(second);
        return (first, second);
    }
}

public class ConversationSummaryStoreProxy : DispatchProxy
{
    private InMemorySessionStore inner = null!;

    public Func<ConversationListOpenSnapshot, ConversationListOpenSnapshot> SnapshotTransform { get; set; } =
        static snapshot => snapshot;

    public int ReadCursorFallbackCount { get; private set; }

    public int RecentMessagesFallbackCount { get; private set; }

    public int UnreadCountFallbackCount { get; private set; }

    public int ContactFallbackCount { get; private set; }

    public static (ILocalSessionStore Store, ConversationSummaryStoreProxy Proxy) Create(InMemorySessionStore inner)
    {
        var store = DispatchProxy.Create<ILocalSessionStore, ConversationSummaryStoreProxy>();
        var proxy = (ConversationSummaryStoreProxy)(object)store;
        proxy.inner = inner;
        return (store, proxy);
    }

    public void ResetFallbackCounts()
    {
        ReadCursorFallbackCount = 0;
        RecentMessagesFallbackCount = 0;
        UnreadCountFallbackCount = 0;
        ContactFallbackCount = 0;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        args ??= [];

        if (targetMethod.Name == nameof(IConversationListOpenRepository.OpenConversationListAsync))
        {
            return OpenConversationListAsync((DateTimeOffset)args[0]!, (CancellationToken)args[1]!);
        }

        TrackFallback(targetMethod, args);
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

    private async Task<ConversationListOpenSnapshot> OpenConversationListAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var snapshot = await inner.OpenConversationListAsync(now, cancellationToken);
        return SnapshotTransform(snapshot);
    }

    private void TrackFallback(MethodInfo method, object?[] args)
    {
        if (method.Name == nameof(ISettingsRepository.GetAsync)
            && method.IsGenericMethod
            && args[0] is string key
            && key.StartsWith("sync.read-cursor.", StringComparison.Ordinal))
        {
            ReadCursorFallbackCount++;
        }
        else if (method.Name == nameof(IMessageRepository.ListRecentForConversationAsync))
        {
            RecentMessagesFallbackCount++;
        }
        else if (method.Name == nameof(IMessageRepository.CountUnreadForConversationAsync))
        {
            UnreadCountFallbackCount++;
        }
        else if (method.Name == nameof(IContactRepository.GetAsync)
            && method.GetParameters()[0].ParameterType == typeof(SessionId))
        {
            ContactFallbackCount++;
        }
    }
}
