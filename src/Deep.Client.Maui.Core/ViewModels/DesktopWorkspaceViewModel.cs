using Deep.Client.Shared.Domain;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.Core.ViewModels;

public enum DesktopConversationDetailKind
{
    None,
    Direct,
    Group
}

public sealed class DesktopWorkspaceViewModel : ViewModelBase, IDisposable
{
    public const double SplitBreakpoint = 680;
    public const double DefaultConversationListWidth = 320;
    public const double MinimumConversationListWidth = 280;
    public const double MaximumConversationListWidth = 420;
    private const int MaximumCachedConversationViewModels = 16;

    private readonly ClientRuntime runtime;
    private readonly Func<ConversationsViewModel> conversationListFactory;
    private readonly Func<ChatViewModel> directChatFactory;
    private readonly Func<GroupChatViewModel> groupChatFactory;
    private readonly SemaphoreSlim activationGate = new(1, 1);
    private readonly Dictionary<ConversationId, ChatViewModel> directChats = [];
    private readonly Dictionary<ConversationId, GroupChatViewModel> groupChats = [];
    private CancellationTokenSource? activationCancellation;
    private ConversationsViewModel conversationList;
    private ChatViewModel directChat;
    private GroupChatViewModel groupChat;
    private ConversationListItem? selectedConversation;
    private ConversationId? activeConversationId;
    private DesktopConversationDetailKind detailKind;
    private bool isSplitView;
    private bool isNarrowDetailOpen;
    private int activationVersion;
    private SessionId? workspaceAccountId;

    public DesktopWorkspaceViewModel(
        ClientRuntime runtime,
        Func<ConversationsViewModel> conversationListFactory,
        Func<ChatViewModel> directChatFactory,
        Func<GroupChatViewModel> groupChatFactory)
    {
        this.runtime = runtime;
        this.conversationListFactory = conversationListFactory;
        this.directChatFactory = directChatFactory;
        this.groupChatFactory = groupChatFactory;
        conversationList = conversationListFactory();
        directChat = directChatFactory();
        groupChat = groupChatFactory();
    }

    public ConversationsViewModel ConversationList
    {
        get => conversationList;
        private set => SetProperty(ref conversationList, value);
    }

    public ChatViewModel DirectChat
    {
        get => directChat;
        private set => SetProperty(ref directChat, value);
    }

    public GroupChatViewModel GroupChat
    {
        get => groupChat;
        private set => SetProperty(ref groupChat, value);
    }

    public ConversationListItem? SelectedConversation
    {
        get => selectedConversation;
        private set => SetProperty(ref selectedConversation, value);
    }

    public DesktopConversationDetailKind DetailKind
    {
        get => detailKind;
        private set
        {
            if (SetProperty(ref detailKind, value))
            {
                RaisePropertyChanged(nameof(IsDirectDetail));
                RaisePropertyChanged(nameof(IsGroupDetail));
                RaisePropertyChanged(nameof(HasSelectedDetail));
                RaisePropertyChanged(nameof(IsEmptyDetail));
            }
        }
    }

    public bool IsSplitView
    {
        get => isSplitView;
        private set
        {
            if (SetProperty(ref isSplitView, value))
            {
                RaiseLayoutPropertiesChanged();
            }
        }
    }

    public bool IsDirectDetail => DetailKind == DesktopConversationDetailKind.Direct;

    public bool IsGroupDetail => DetailKind == DesktopConversationDetailKind.Group;

    public bool HasSelectedDetail => DetailKind != DesktopConversationDetailKind.None;

    public bool IsEmptyDetail => !HasSelectedDetail;

    public bool IsConversationListVisible => IsSplitView || !isNarrowDetailOpen;

    public bool IsDetailPaneVisible => IsSplitView || isNarrowDetailOpen;

    public bool IsBackButtonVisible => !IsSplitView && isNarrowDetailOpen;

    public void UpdateWindowWidth(double width)
    {
        if (double.IsNaN(width) || width <= 0)
        {
            return;
        }

        IsSplitView = width >= SplitBreakpoint;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var account = await runtime.Accounts.GetActiveAccountAsync(cancellationToken);
        if (account is null)
        {
            ResetSession();
            return;
        }

        if (workspaceAccountId != account.SessionId)
        {
            ResetUiState(account.SessionId);
        }

        await ConversationList.LoadCachedAsync(cancellationToken);
        RebindSelectedConversation();
    }

    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var synchronized = await ConversationList.SyncAsync(cancellationToken);
        RebindSelectedConversation();
        return synchronized;
    }

    public async Task RefreshLocalConversationListAsync(CancellationToken cancellationToken = default)
    {
        await ConversationList.LoadCachedAsync(cancellationToken);
        RebindSelectedConversation();
    }

    public async Task<bool> ActivateConversationAsync(
        ConversationId conversationId,
        CancellationToken cancellationToken = default)
    {
        var item = FindConversation(conversationId);
        if (item is null)
        {
            await ConversationList.LoadAsync(cancellationToken);
            item = FindConversation(conversationId);
        }

        return item is not null && await SelectConversationAsync(item, cancellationToken);
    }

    public async Task<bool> SelectConversationAsync(
        ConversationListItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        var requestedKind = item.Kind switch
        {
            ConversationKind.OneToOne => DesktopConversationDetailKind.Direct,
            ConversationKind.GroupV2 => DesktopConversationDetailKind.Group,
            _ => DesktopConversationDetailKind.None
        };
        if (requestedKind == DesktopConversationDetailKind.None)
        {
            ErrorMessage = "Этот тип диалога пока не поддерживается.";
            return false;
        }

        if (activeConversationId == item.Id && DetailKind == requestedKind)
        {
            SelectedConversation = item;
            ConversationList.SelectedConversation = item;
            return true;
        }

        var version = Interlocked.Increment(ref activationVersion);
        var nextCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previousCancellation = Interlocked.Exchange(ref activationCancellation, nextCancellation);
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();

        var gateAcquired = false;
        try
        {
            IsBusy = true;
            ErrorMessage = null;
            await activationGate.WaitAsync(nextCancellation.Token);
            gateAcquired = true;
            if (version != Volatile.Read(ref activationVersion))
            {
                return false;
            }

            ChatViewModel? candidateDirect = null;
            GroupChatViewModel? candidateGroup = null;
            switch (requestedKind)
            {
                case DesktopConversationDetailKind.Direct:
                    candidateDirect = GetOrCreateDirectChat(item.Id);
                    await candidateDirect.OpenFromRouteAsync(item.Id.Value, item.Title, nextCancellation.Token);
                    break;
                case DesktopConversationDetailKind.Group:
                    candidateGroup = GetOrCreateGroupChat(item.Id);
                    await candidateGroup.OpenFromRouteAsync(item.Id.Value, item.Title, nextCancellation.Token);
                    break;
            }

            if (version != Volatile.Read(ref activationVersion))
            {
                return false;
            }

            SelectedConversation = item;
            ConversationList.SelectedConversation = item;
            if (candidateDirect is not null)
            {
                DirectChat = candidateDirect;
            }

            if (candidateGroup is not null)
            {
                GroupChat = candidateGroup;
            }

            DetailKind = requestedKind;
            isNarrowDetailOpen = true;
            activeConversationId = item.Id;
            TrimViewModelCaches(item.Id);
            RaiseLayoutPropertiesChanged();
            return true;
        }
        catch (OperationCanceledException) when (nextCancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return false;
        }
        finally
        {
            if (gateAcquired)
            {
                activationGate.Release();
            }

            if (ReferenceEquals(activationCancellation, nextCancellation))
            {
                activationCancellation = null;
                nextCancellation.Dispose();
                IsBusy = false;
            }
        }
    }

    public void ShowConversationList()
    {
        if (IsSplitView)
        {
            return;
        }

        isNarrowDetailOpen = false;
        RaiseLayoutPropertiesChanged();
    }

    public void ResetSession() => ResetUiState(accountId: null);

    public void Dispose()
    {
        activationCancellation?.Cancel();
        activationCancellation?.Dispose();
        activationGate.Dispose();
    }

    private ConversationListItem? FindConversation(ConversationId id) =>
        ConversationList.Conversations.FirstOrDefault(item => item.Id == id);

    private void RebindSelectedConversation()
    {
        if (SelectedConversation is not { } selected)
        {
            return;
        }

        var current = FindConversation(selected.Id);
        if (current is not null)
        {
            SelectedConversation = current;
            ConversationList.SelectedConversation = current;
            return;
        }

        SelectedConversation = null;
        ConversationList.SelectedConversation = null;
        activeConversationId = null;
        DetailKind = DesktopConversationDetailKind.None;
        isNarrowDetailOpen = false;
        RaiseLayoutPropertiesChanged();
    }

    private ChatViewModel GetOrCreateDirectChat(ConversationId id)
    {
        if (!directChats.TryGetValue(id, out var chat))
        {
            chat = directChatFactory();
            directChats[id] = chat;
        }

        return chat;
    }

    private GroupChatViewModel GetOrCreateGroupChat(ConversationId id)
    {
        if (!groupChats.TryGetValue(id, out var chat))
        {
            chat = groupChatFactory();
            groupChats[id] = chat;
        }

        return chat;
    }

    private void TrimViewModelCaches(ConversationId activeId)
    {
        TrimCache(directChats, activeId);
        TrimCache(groupChats, activeId);
    }

    private static void TrimCache<TViewModel>(Dictionary<ConversationId, TViewModel> cache, ConversationId activeId)
    {
        while (cache.Count > MaximumCachedConversationViewModels)
        {
            var removable = cache.Keys.FirstOrDefault(id => id != activeId);
            if (removable == default)
            {
                return;
            }

            cache.Remove(removable);
        }
    }

    private void ResetUiState(SessionId? accountId)
    {
        Interlocked.Increment(ref activationVersion);
        var cancellation = Interlocked.Exchange(ref activationCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
        directChats.Clear();
        groupChats.Clear();
        workspaceAccountId = accountId;
        ConversationList = conversationListFactory();
        DirectChat = directChatFactory();
        GroupChat = groupChatFactory();
        SelectedConversation = null;
        activeConversationId = null;
        DetailKind = DesktopConversationDetailKind.None;
        isNarrowDetailOpen = false;
        IsBusy = false;
        ErrorMessage = null;
        RaiseLayoutPropertiesChanged();
    }

    private void RaiseLayoutPropertiesChanged()
    {
        RaisePropertyChanged(nameof(IsConversationListVisible));
        RaisePropertyChanged(nameof(IsDetailPaneVisible));
        RaisePropertyChanged(nameof(IsBackButtonVisible));
    }
}
