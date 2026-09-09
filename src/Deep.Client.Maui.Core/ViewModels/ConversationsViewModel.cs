using System.Collections.ObjectModel;
using Deep.Client.Maui.Core.Commands;
using Deep.Client.Maui.Core.Presentation;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;
using DeepDisplayName = Deep.Client.Maui.Core.Presentation.DeepDisplayName;

namespace Deep.Client.Maui.Core.ViewModels;

public sealed record ConversationListItem(
    ConversationId Id,
    string Title,
    string AvatarInitial,
    ConversationKind Kind,
    DateTimeOffset UpdatedAt,
    bool IsMuted,
    string LastMessagePreview,
    int UnreadCount,
    bool IsUnread,
    bool IsMessageRequest,
    bool IsSelected,
    string? LastMessageId = null)
{
    public DateTimeOffset LocalUpdatedAt => UpdatedAt.ToLocalTime();
}

public sealed class ConversationsViewModel : ViewModelBase
{
    private readonly ClientRuntime runtime;
    private readonly IContactMailboxOnboarding contactOnboarding;
    private readonly SemaphoreSlim loadGate = new(1, 1);
    private readonly List<ConversationListItem> allConversations = [];
    private readonly Dictionary<ConversationId, DateTimeOffset?> readCursors = [];
    private string searchQuery = string.Empty;
    private string newSessionId = string.Empty;
    private string newDisplayName = string.Empty;
    private string accountInitial = "D";
    private ConversationListItem? selectedConversation;
    private bool isManualRefreshing;

    internal string SyncFailureCode { get; private set; } = "none";

    public ConversationsViewModel(ClientRuntime runtime)
        : this(runtime, new SessionIdContactMailboxOnboarding())
    {
    }

    public ConversationsViewModel(
        ClientRuntime runtime,
        IContactMailboxOnboarding contactOnboarding)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.contactOnboarding = contactOnboarding ??
            throw new ArgumentNullException(nameof(contactOnboarding));
        Conversations = new ObservableRangeCollection<ConversationListItem>();
        LoadCommand = new AsyncCommand(LoadAsync);
        ManualRefreshCommand = new AsyncCommand(ManualRefreshAsync);
        StartConversationCommand = new AsyncCommand(StartConversationCommandAsync, CanStartConversationFromComposer);
    }

    public ObservableCollection<ConversationListItem> Conversations { get; }

    public AsyncCommand LoadCommand { get; }

    public AsyncCommand ManualRefreshCommand { get; }

    public AsyncCommand StartConversationCommand { get; }

    public string AccountInitial
    {
        get => accountInitial;
        private set => SetProperty(ref accountInitial, value);
    }

    public bool IsManualRefreshing
    {
        get => isManualRefreshing;
        private set => SetProperty(ref isManualRefreshing, value);
    }

    public bool CanStartNewConversation => CanStartConversationFromComposer();

    public string SearchQuery
    {
        get => searchQuery;
        set
        {
            if (SetProperty(ref searchQuery, value))
            {
                ApplyFilter();
            }
        }
    }

    public string NewSessionId
    {
        get => newSessionId;
        set
        {
            if (SetProperty(ref newSessionId, value))
            {
                RaisePropertyChanged(nameof(CanStartNewConversation));
                StartConversationCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string NewDisplayName
    {
        get => newDisplayName;
        set => SetProperty(ref newDisplayName, value);
    }

    public ConversationListItem? SelectedConversation
    {
        get => selectedConversation;
        set
        {
            if (SetProperty(ref selectedConversation, value))
            {
                UpdateSelection(value?.Id);
            }
        }
    }

    public async Task<Conversation> StartOneToOneAsync(string sessionId, string? displayName = null, CancellationToken cancellationToken = default)
    {
        var recipient = await contactOnboarding.PrepareAsync(
            sessionId,
            cancellationToken);
        var conversation = await runtime.Conversations
            .GetOrCreateOneToOneAsync(
                recipient,
                displayName,
                approve: true,
                cancellationToken: cancellationToken);

        await LoadAsync(cancellationToken);
        return conversation;
    }

    private bool CanStartConversationFromComposer()
    {
        if (string.IsNullOrWhiteSpace(NewSessionId))
        {
            return false;
        }

        return contactOnboarding.CanAccept(NewSessionId);
    }

    public async Task<Conversation?> StartConversationFromComposerAsync(CancellationToken cancellationToken = default)
    {
        if (!CanStartConversationFromComposer())
        {
            return null;
        }

        ErrorMessage = null;
        Conversation conversation;
        try
        {
            conversation = await StartOneToOneAsync(
                NewSessionId.Trim(),
                NewDisplayName.Trim(),
                cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            ErrorMessage = "Контакт не подтверждён. Используйте действующее защищённое приглашение.";
            return null;
        }
        var selected = Conversations.FirstOrDefault(item => item.Id == conversation.Id);
        if (selected is not null)
        {
            SelectedConversation = selected;
        }

        NewSessionId = string.Empty;
        NewDisplayName = string.Empty;
        return conversation;
    }

    private async Task StartConversationCommandAsync(CancellationToken cancellationToken) =>
        await StartConversationFromComposerAsync(cancellationToken);

    public async Task LoadAsync(CancellationToken cancellationToken = default) =>
        _ = await LoadCoreAsync(forceMessageSummaries: true, cancellationToken);

    public Task LoadCachedAsync(CancellationToken cancellationToken = default) =>
        LoadLocalSnapshotAsync(forceMessageSummaries: true, cancellationToken);

    public Task<bool> SyncAsync(CancellationToken cancellationToken = default) =>
        LoadCoreAsync(forceMessageSummaries: false, cancellationToken);

    private async Task ManualRefreshAsync(CancellationToken cancellationToken)
    {
        if (IsManualRefreshing)
        {
            return;
        }

        try
        {
            IsManualRefreshing = true;
            _ = await LoadCoreAsync(forceMessageSummaries: true, cancellationToken);
        }
        finally
        {
            IsManualRefreshing = false;
        }
    }

    private async Task<bool> LoadCoreAsync(bool forceMessageSummaries, CancellationToken cancellationToken)
    {
        await loadGate.WaitAsync(cancellationToken);

        try
        {
            var synchronized = true;
            SyncFailureCode = "none";
            IsBusy = true;
            ErrorMessage = null;
            await RefreshLocalAsync(forceMessageSummaries, cancellationToken);

            try
            {
                await runtime.Inbox.SynchronizeAsync(cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Keep the cached conversation list usable while the network is unavailable.
                synchronized = false;
                SyncFailureCode = SyncFailureCodeClassifier.Classify(ex);
                ErrorMessage = ex.Message;
            }

            await RefreshLocalAsync(forceMessageSummaries: false, cancellationToken);
            return synchronized;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            SyncFailureCode = SyncFailureCodeClassifier.Classify(ex);
            ErrorMessage = ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
            loadGate.Release();
        }
    }

    private async Task LoadLocalSnapshotAsync(bool forceMessageSummaries, CancellationToken cancellationToken)
    {
        await loadGate.WaitAsync(cancellationToken);
        try
        {
            ErrorMessage = null;
            await RefreshLocalAsync(forceMessageSummaries, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            loadGate.Release();
        }
    }

    private async Task RefreshLocalAsync(
        bool forceMessageSummaries,
        CancellationToken cancellationToken)
    {
        SessionAccount? activeAccount;
        IReadOnlyList<Conversation> conversations;
        IReadOnlyDictionary<ConversationId, ConversationListSummary> summaries;
        if (runtime.Store is IConversationListOpenRepository openRepository)
        {
            var snapshot = await openRepository.OpenConversationListAsync(runtime.Clock.UtcNow, cancellationToken);
            activeAccount = snapshot.ActiveAccount;
            conversations = snapshot.Conversations;
            summaries = snapshot.Summaries;
        }
        else
        {
            activeAccount = await runtime.Accounts.GetActiveAccountAsync(cancellationToken);
            conversations = await runtime.Conversations.ListAsync(cancellationToken);
            summaries = await runtime.Store.GetConversationSummariesAsync(
                conversations.Select(static conversation => conversation.Id).ToArray(),
                runtime.Clock.UtcNow,
                cancellationToken);
        }

        AccountInitial = DeepDisplayName.AvatarInitial(activeAccount?.DisplayName, activeAccount?.SessionId.Value);
        var visibleConversations = conversations
            .Where(static item => !item.IsHidden)
            .ToArray();
        var previousById = allConversations.ToDictionary(item => item.Id);
        var nextConversations = new List<ConversationListItem>(visibleConversations.Length);
        foreach (var conversation in visibleConversations)
        {
            var resolvedTitle = ResolveConversationTitle(conversation);
            summaries.TryGetValue(conversation.Id, out var summary);
            if (!forceMessageSummaries
                && summary is not null
                && previousById.TryGetValue(conversation.Id, out var previous)
                && previous.UpdatedAt == conversation.UpdatedAt
                && string.Equals(previous.Title, resolvedTitle, StringComparison.Ordinal)
                && previous.IsMuted == conversation.Settings.IsMuted
                && previous.Kind == conversation.Kind
                && string.Equals(previous.LastMessageId, summary.LastMessage?.Id.Value, StringComparison.Ordinal)
                && previous.UnreadCount == summary.UnreadCount
                && previous.IsMessageRequest == ResolveIsMessageRequest(
                    conversation,
                    activeAccount?.SessionId,
                    summary.Contact))
            {
                var readCursor = summary.ReadCursor;
                var cursorChanged = !readCursors.TryGetValue(conversation.Id, out var previousCursor)
                    || previousCursor != readCursor;
                readCursors[conversation.Id] = readCursor;

                if (cursorChanged && readCursor is not null)
                {
                    nextConversations.Add(previous with { UnreadCount = 0, IsUnread = false, IsSelected = false });
                    continue;
                }

                nextConversations.Add(previous with { IsSelected = false });
                continue;
            }

            nextConversations.Add(await BuildListItemAsync(
                conversation,
                activeAccount?.SessionId,
                summary,
                cancellationToken));
        }

        if (allConversations.SequenceEqual(nextConversations))
        {
            return;
        }

        allConversations.Clear();
        allConversations.AddRange(nextConversations);

        ApplyFilter();

        if (SelectedConversation is not null && !Conversations.Any(item => item.Id == SelectedConversation.Id))
        {
            SelectedConversation = null;
        }
    }

    private async Task<ConversationListItem> BuildListItemAsync(
        Conversation conversation,
        SessionId? activeSessionId,
        ConversationListSummary? summary,
        CancellationToken cancellationToken)
    {
        var readCursor = summary is not null
            ? summary.ReadCursor
            : await runtime.Messages.GetReadCursorAsync(conversation.Id, cancellationToken);
        readCursors[conversation.Id] = readCursor;
        var lastMessage = summary?.LastMessage;
        if (summary is null)
        {
            var recentMessages = await runtime.Messages.ListRecentConversationMessagesAsync(conversation.Id, 1, cancellationToken);
            lastMessage = recentMessages.LastOrDefault();
        }

        var preview = lastMessage?.Body;
        if (string.IsNullOrWhiteSpace(preview))
        {
            preview = "Пока нет сообщений";
        }

        if (preview.Length > 42)
        {
            preview = preview[..42] + "...";
        }

        if (lastMessage?.Direction == MessageDirection.Outgoing)
        {
            preview = $"Вы: {preview}";
        }

        if (lastMessage is { Attachments.Count: > 0 })
        {
            preview = lastMessage.Attachments.Count == 1
                ? $"Фото · {preview}"
                : $"{lastMessage.Attachments.Count} вложения · {preview}";
        }

        var unreadCount = summary is not null
            ? summary.UnreadCount
            : await runtime.Messages.CountUnreadConversationMessagesAsync(
                conversation.Id,
                readCursor,
                cancellationToken);

        var isMessageRequest = false;
        if (conversation.Kind == ConversationKind.OneToOne
            && activeSessionId is not null
            && !string.Equals(conversation.Id.Value, activeSessionId.Value.Value, StringComparison.Ordinal))
        {
            var contact = summary is not null
                ? summary.Contact
                : await ((IContactRepository)runtime.Store)
                    .GetAsync(SessionId.Parse(conversation.Id.Value), cancellationToken);
            isMessageRequest = contact is { IsApproved: false, IsBlocked: false };
        }

        var title = ResolveConversationTitle(conversation);

        return new ConversationListItem(
            conversation.Id,
            title,
            DeepDisplayName.AvatarInitial(title, conversation.Id.Value),
            conversation.Kind,
            conversation.UpdatedAt,
            conversation.Settings.IsMuted,
            preview,
            unreadCount,
            IsUnread: unreadCount > 0,
            IsMessageRequest: isMessageRequest,
            IsSelected: false,
            LastMessageId: lastMessage?.Id.Value);
    }

    private static bool ResolveIsMessageRequest(
        Conversation conversation,
        SessionId? activeSessionId,
        Contact? contact) =>
        conversation.Kind == ConversationKind.OneToOne
        && activeSessionId is not null
        && !string.Equals(conversation.Id.Value, activeSessionId.Value.Value, StringComparison.Ordinal)
        && contact is { IsApproved: false, IsBlocked: false };

    private static string ResolveConversationTitle(Conversation conversation)
    {
        if (conversation.Kind != ConversationKind.OneToOne)
        {
            return conversation.DisplayName;
        }

        try
        {
            var sessionId = SessionId.Parse(conversation.Id.Value);
            return DeepDisplayName.ContactTitleOrFallback(sessionId, conversation.DisplayName);
        }
        catch (FormatException)
        {
            return conversation.DisplayName;
        }
    }

    private void ApplyFilter()
    {
        var filter = SearchQuery.Trim();
        var filtered = string.IsNullOrWhiteSpace(filter)
            ? allConversations
            : allConversations
                .Where(item => item.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || item.Id.Value.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();

        filtered = filtered
            .OrderByDescending(item => item.UpdatedAt)
            .ToList();

        var selectedId = SelectedConversation?.Id;
        var nextItems = filtered
            .Select(conversation => conversation with { IsSelected = selectedId is not null && conversation.Id == selectedId.Value })
            .ToArray();

        SyncConversationItems(nextItems);
    }

    private void SyncConversationItems(IReadOnlyList<ConversationListItem> nextItems)
    {
        if (HasSameConversationOrder(nextItems))
        {
            for (var index = 0; index < nextItems.Count; index++)
            {
                if (Conversations[index] != nextItems[index])
                {
                    Conversations[index] = nextItems[index];
                }
            }

            return;
        }

        ConversationItems.ReplaceRange(nextItems);
    }

    private bool HasSameConversationOrder(IReadOnlyList<ConversationListItem> nextItems)
    {
        if (Conversations.Count != nextItems.Count)
        {
            return false;
        }

        for (var index = 0; index < nextItems.Count; index++)
        {
            if (Conversations[index].Id != nextItems[index].Id)
            {
                return false;
            }
        }

        return true;
    }

    private ObservableRangeCollection<ConversationListItem> ConversationItems =>
        (ObservableRangeCollection<ConversationListItem>)Conversations;

    private void UpdateSelection(ConversationId? selectedId)
    {
        for (var index = 0; index < Conversations.Count; index++)
        {
            var item = Conversations[index];
            var isSelected = selectedId is not null && item.Id == selectedId.Value;
            if (item.IsSelected == isSelected)
            {
                continue;
            }

            Conversations[index] = item with { IsSelected = isSelected };
        }
    }
}
