using System.Collections.ObjectModel;
using Deep.Client.Maui.Core.Commands;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.Core.ViewModels;

public sealed record ConversationListItem(
    ConversationId Id,
    string Title,
    ConversationKind Kind,
    DateTimeOffset UpdatedAt,
    bool IsMuted,
    string LastMessagePreview,
    int UnreadCount,
    bool IsUnread,
    bool IsMessageRequest,
    bool IsSelected);

public sealed class ConversationsViewModel : ViewModelBase
{
    private readonly ClientRuntime runtime;
    private readonly List<ConversationListItem> allConversations = [];
    private readonly Dictionary<ConversationId, DateTimeOffset?> readCursors = [];
    private string searchQuery = string.Empty;
    private string newSessionId = string.Empty;
    private string newDisplayName = string.Empty;
    private ConversationListItem? selectedConversation;
    private bool isManualRefreshing;

    public ConversationsViewModel(ClientRuntime runtime)
    {
        this.runtime = runtime;
        Conversations = [];
        LoadCommand = new AsyncCommand(LoadAsync);
        ManualRefreshCommand = new AsyncCommand(ManualRefreshAsync);
        StartConversationCommand = new AsyncCommand(StartConversationCommandAsync, CanStartConversationFromComposer);
    }

    public ObservableCollection<ConversationListItem> Conversations { get; }

    public AsyncCommand LoadCommand { get; }

    public AsyncCommand ManualRefreshCommand { get; }

    public AsyncCommand StartConversationCommand { get; }

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
        var conversation = await runtime.Conversations
            .GetOrCreateOneToOneAsync(
                SessionId.Parse(sessionId),
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

        try
        {
            SessionId.Parse(NewSessionId);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<Conversation?> StartConversationFromComposerAsync(CancellationToken cancellationToken = default)
    {
        if (!CanStartConversationFromComposer())
        {
            return null;
        }

        var conversation = await StartOneToOneAsync(NewSessionId.Trim(), NewDisplayName.Trim(), cancellationToken);
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

    public Task LoadAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(forceMessageSummaries: true, cancellationToken);

    public Task SyncAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(forceMessageSummaries: false, cancellationToken);

    private async Task ManualRefreshAsync(CancellationToken cancellationToken)
    {
        if (IsManualRefreshing)
        {
            return;
        }

        try
        {
            IsManualRefreshing = true;
            await LoadAsync(forceMessageSummaries: true, cancellationToken);
        }
        finally
        {
            IsManualRefreshing = false;
        }
    }

    private Task LoadAsync(bool forceMessageSummaries, CancellationToken cancellationToken) =>
        RunBusyAsync(async ct =>
        {
            var activeAccount = await runtime.Accounts.GetActiveAccountAsync(ct);
            await RefreshLocalAsync(activeAccount, forceMessageSummaries, ct);

            try
            {
                await runtime.Inbox.SynchronizeAsync(ct);
            }
            catch when (!ct.IsCancellationRequested)
            {
                // Keep the cached conversation list usable while the network is unavailable.
            }

            await RefreshLocalAsync(activeAccount, forceMessageSummaries: false, ct);
        }, cancellationToken);

    private async Task RefreshLocalAsync(
        SessionAccount? activeAccount,
        bool forceMessageSummaries,
        CancellationToken cancellationToken)
    {
            var conversations = await runtime.Conversations.ListAsync(cancellationToken);
            var previousById = allConversations.ToDictionary(item => item.Id);
            var nextConversations = new List<ConversationListItem>(conversations.Count);
            foreach (var conversation in conversations.Where(static item => !item.IsHidden))
            {
                if (!forceMessageSummaries
                    && previousById.TryGetValue(conversation.Id, out var previous)
                    && previous.UpdatedAt == conversation.UpdatedAt
                    && string.Equals(previous.Title, conversation.DisplayName, StringComparison.Ordinal)
                    && previous.IsMuted == conversation.Settings.IsMuted
                    && previous.Kind == conversation.Kind)
                {
                    var readCursor = await runtime.Messages.GetReadCursorAsync(conversation.Id, cancellationToken);
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

                nextConversations.Add(await BuildListItemAsync(conversation, activeAccount?.SessionId, cancellationToken));
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
        CancellationToken cancellationToken)
    {
        var messages = await runtime.Messages.ListConversationMessagesAsync(conversation.Id, cancellationToken);
        var readCursor = await runtime.Messages.GetReadCursorAsync(conversation.Id, cancellationToken);
        readCursors[conversation.Id] = readCursor;
        var lastMessage = messages.LastOrDefault();
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

        var unreadCount = messages.Count(message =>
            message.Direction == MessageDirection.Incoming
            && (readCursor is null || message.CreatedAt > readCursor.Value)
            && message.DeliveryState != MessageDeliveryState.Read);

        var isMessageRequest = false;
        if (conversation.Kind == ConversationKind.OneToOne
            && activeSessionId is not null
            && !string.Equals(conversation.Id.Value, activeSessionId.Value.Value, StringComparison.Ordinal))
        {
            var contact = await ((IContactRepository)runtime.Store)
                .GetAsync(SessionId.Parse(conversation.Id.Value), cancellationToken);
            isMessageRequest = contact is { IsApproved: false, IsBlocked: false };
        }

        return new ConversationListItem(
            conversation.Id,
            conversation.DisplayName,
            conversation.Kind,
            conversation.UpdatedAt,
            conversation.Settings.IsMuted,
            preview,
            unreadCount,
            IsUnread: unreadCount > 0,
            IsMessageRequest: isMessageRequest,
            IsSelected: false);
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
        if (Conversations.Count == nextItems.Count && Conversations.SequenceEqual(nextItems))
        {
            return;
        }

        for (var targetIndex = 0; targetIndex < nextItems.Count; targetIndex++)
        {
            var next = nextItems[targetIndex];
            var existingIndex = -1;
            for (var index = targetIndex; index < Conversations.Count; index++)
            {
                if (Conversations[index].Id == next.Id)
                {
                    existingIndex = index;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                Conversations.Insert(targetIndex, next);
                continue;
            }

            if (existingIndex != targetIndex)
            {
                Conversations.Move(existingIndex, targetIndex);
            }

            if (Conversations[targetIndex] != next)
            {
                Conversations[targetIndex] = next;
            }
        }

        while (Conversations.Count > nextItems.Count)
        {
            Conversations.RemoveAt(Conversations.Count - 1);
        }
    }

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
