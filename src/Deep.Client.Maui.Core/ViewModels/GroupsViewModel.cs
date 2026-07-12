using System.Collections.ObjectModel;
using Deep.Client.Maui.Core.Commands;
using Deep.Client.Maui.Core.Presentation;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Features;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.Core.ViewModels;

public sealed record GroupListItem(ConversationId Id, string Name, int MemberCount, int AdminCount, bool IsDestroyed);

public sealed record GroupDraftMemberItem(SessionId SessionId, string DisplayName);

public sealed class GroupsViewModel : ViewModelBase
{
    private readonly ClientRuntime runtime;
    private readonly IContactRepository contacts;
    private IReadOnlyDictionary<string, string> contactDisplayNames = new Dictionary<string, string>(StringComparer.Ordinal);
    private bool contactDisplayNamesLoaded;
    private string groupName = string.Empty;
    private string memberSessionId = string.Empty;
    private GroupDraftMemberItem? selectedDraftMember;
    private GroupListItem? selectedGroup;

    public GroupsViewModel(ClientRuntime runtime)
    {
        this.runtime = runtime;
        contacts = (IContactRepository)runtime.Store;
        Groups = [];
        DraftMembers = [];
        CreateCommand = new AsyncCommand(CreateFromUiAsync, () => runtime.FeatureFlags.GroupsV2Enabled && !string.IsNullOrWhiteSpace(GroupName));
        AddDraftMemberCommand = new AsyncCommand(AddDraftMemberAsync, CanAddDraftMember);
        RefreshCommand = new AsyncCommand(RefreshFromUiAsync, () => runtime.FeatureFlags.GroupsV2Enabled);
    }

    public ObservableCollection<GroupListItem> Groups { get; }

    public ObservableCollection<GroupDraftMemberItem> DraftMembers { get; }

    public string GroupName
    {
        get => groupName;
        set
        {
            if (SetProperty(ref groupName, value))
            {
                RaisePropertyChanged(nameof(CanCreateGroup));
                CreateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string MemberSessionId
    {
        get => memberSessionId;
        set
        {
            if (SetProperty(ref memberSessionId, value))
            {
                RaisePropertyChanged(nameof(CanAddMember));
                AddDraftMemberCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanCreateGroups => runtime.FeatureFlags.GroupsV2Enabled;

    public bool CanCreateGroup => runtime.FeatureFlags.GroupsV2Enabled && !string.IsNullOrWhiteSpace(GroupName);

    public bool CanAddMember => CanAddDraftMember();

    public bool HasDraftMembers => DraftMembers.Count > 0;

    public GroupListItem? SelectedGroup
    {
        get => selectedGroup;
        set => SetProperty(ref selectedGroup, value);
    }

    public GroupDraftMemberItem? SelectedDraftMember
    {
        get => selectedDraftMember;
        set => SetProperty(ref selectedDraftMember, value);
    }

    public AsyncCommand CreateCommand { get; }

    public AsyncCommand AddDraftMemberCommand { get; }

    public AsyncCommand RefreshCommand { get; }

    public Task<Group?> CreateGroupAsync(SessionId owner, IEnumerable<SessionId> members, CancellationToken cancellationToken = default) =>
        RunCreateAsync(owner, members, cancellationToken);

    public Task<IReadOnlyList<GroupListItem>> RefreshAsync(CancellationToken cancellationToken = default) =>
        RunRefreshAsync(cancellationToken);

    public Task RefreshContactDisplayNamesAsync(CancellationToken cancellationToken = default) =>
        RunBusyAsync(ReloadContactDisplayNamesAsync, cancellationToken);

    public async Task<Group?> CreateGroupFromComposerAsync(CancellationToken cancellationToken = default)
    {
        var account = await runtime.Accounts.GetActiveAccountAsync(cancellationToken)
            ?? throw new InvalidOperationException("Войдите в аккаунт перед созданием группы.");

        var members = DraftMembers.Select(item => item.SessionId).ToArray();
        var created = await RunCreateAsync(account.SessionId, members, cancellationToken);
        if (created is not null)
        {
            GroupName = string.Empty;
            DraftMembers.Clear();
            RaiseDraftMemberPropertiesChanged();
        }

        return created;
    }

    public async Task AddDraftMemberAsync(CancellationToken cancellationToken = default)
    {
        if (!TryParseMemberSessionId(out var member))
        {
            ErrorMessage = "Введите корректный ID аккаунта.";
            return;
        }

        if (DraftMembers.Any(item => item.SessionId == member))
        {
            MemberSessionId = string.Empty;
            ErrorMessage = null;
            return;
        }

        await EnsureContactDisplayNamesLoadedAsync(cancellationToken);
        DraftMembers.Add(ToDraftMemberItem(member));
        MemberSessionId = string.Empty;
        ErrorMessage = null;
        RaiseDraftMemberPropertiesChanged();
    }

    public void RemoveDraftMember(GroupDraftMemberItem member)
    {
        DraftMembers.Remove(member);
        if (SelectedDraftMember == member)
        {
            SelectedDraftMember = null;
        }

        RaiseDraftMemberPropertiesChanged();
    }

    public Task<Group?> UpdateGroupNameAsync(
        ConversationId groupId,
        SessionId requestor,
        string newName,
        CancellationToken cancellationToken = default) =>
        RunGroupMutationAsync(() => runtime.Conversations.UpdateGroupNameAsync(groupId, requestor, newName, cancellationToken), cancellationToken);

    public Task<Group?> PromoteMemberAsync(
        ConversationId groupId,
        SessionId requestor,
        SessionId memberId,
        CancellationToken cancellationToken = default) =>
        RunGroupMutationAsync(() => runtime.Conversations.PromoteMemberAsync(groupId, requestor, memberId, cancellationToken), cancellationToken);

    public Task<Group?> AddMemberAsync(
        ConversationId groupId,
        SessionId requestor,
        SessionId memberId,
        CancellationToken cancellationToken = default) =>
        RunGroupMutationAsync(() => runtime.Conversations.AddMemberAsync(groupId, requestor, memberId, cancellationToken), cancellationToken);

    public Task<Group?> DemoteMemberAsync(
        ConversationId groupId,
        SessionId requestor,
        SessionId memberId,
        CancellationToken cancellationToken = default) =>
        RunGroupMutationAsync(() => runtime.Conversations.DemoteMemberAsync(groupId, requestor, memberId, cancellationToken), cancellationToken);

    public Task<Group?> MarkMemberPendingRemovalAsync(
        ConversationId groupId,
        SessionId requestor,
        SessionId memberId,
        bool isPending,
        CancellationToken cancellationToken = default) =>
        RunGroupMutationAsync(() => runtime.Conversations.MarkMemberPendingRemovalAsync(groupId, requestor, memberId, isPending, cancellationToken), cancellationToken);

    public Task<Group?> RemoveMemberAsync(
        ConversationId groupId,
        SessionId requestor,
        SessionId memberId,
        CancellationToken cancellationToken = default) =>
        RunGroupMutationAsync(() => runtime.Conversations.RemoveMemberAsync(groupId, requestor, memberId, cancellationToken), cancellationToken);

    public Task<Group?> LeaveGroupAsync(
        ConversationId groupId,
        SessionId memberId,
        CancellationToken cancellationToken = default) =>
        RunGroupMutationAsync(() => runtime.Conversations.LeaveGroupAsync(groupId, memberId, cancellationToken), cancellationToken);

    public Task<Group?> DestroyGroupAsync(
        ConversationId groupId,
        SessionId requestor,
        CancellationToken cancellationToken = default) =>
        RunGroupMutationAsync(() => runtime.Conversations.DestroyGroupAsync(groupId, requestor, cancellationToken), cancellationToken);

    private async Task CreateFromUiAsync(CancellationToken cancellationToken)
    {
        await CreateGroupFromComposerAsync(cancellationToken);
    }

    private Task RefreshFromUiAsync(CancellationToken cancellationToken) =>
        RunRefreshAsync(cancellationToken);

    private async Task<Group?> RunCreateAsync(SessionId owner, IEnumerable<SessionId> members, CancellationToken cancellationToken)
    {
        Group? created = null;
        await RunBusyAsync(async ct =>
        {
            if (!runtime.FeatureFlags.GroupsV2Enabled)
            {
                throw new FeatureDisabledException(nameof(runtime.FeatureFlags.GroupsV2Enabled));
            }

            created = await runtime.Conversations.CreateGroupScaffoldAsync(owner, GroupName, members, ct);
            UpsertGroupItem(created);
        }, cancellationToken);

        return created;
    }

    private async Task<IReadOnlyList<GroupListItem>> RunRefreshAsync(CancellationToken cancellationToken)
    {
        var items = Array.Empty<GroupListItem>();
        await RunBusyAsync(async ct =>
        {
            if (!runtime.FeatureFlags.GroupsV2Enabled)
            {
                throw new FeatureDisabledException(nameof(runtime.FeatureFlags.GroupsV2Enabled));
            }

            var account = await runtime.Accounts.GetActiveAccountAsync(ct);
            if (account is not null)
            {
                await runtime.Conversations.ReceiveGroupUpdatesAsync(account.SessionId, ct);
            }

            var groups = await runtime.Conversations.ListGroupsAsync(ct);
            items = groups
                .Select(ToListItem)
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            await ReloadContactDisplayNamesAsync(ct);
            SyncGroupItems(items);
        }, cancellationToken);

        return items;
    }

    private async Task<Group?> RunGroupMutationAsync(Func<Task<Group?>> operation, CancellationToken cancellationToken)
    {
        Group? group = null;
        await RunBusyAsync(async _ =>
        {
            group = await operation();
            if (group is not null)
            {
                UpsertGroupItem(group);
            }
        }, cancellationToken);

        return group;
    }

    private static GroupListItem ToListItem(Group group) =>
        new(
            group.Id,
            group.Name,
            group.Members.Count,
            group.Members.Count(member => member.Role == GroupMemberRole.Admin),
            group.IsDestroyed);

    private void UpsertGroupItem(Group group)
    {
        var item = ToListItem(group);
        var index = Groups
            .Select((entry, idx) => new { entry, idx })
            .FirstOrDefault(x => x.entry.Id == group.Id)
            ?.idx;

        if (index is null)
        {
            Groups.Add(item);
            return;
        }

        Groups[index.Value] = item;
    }

    private void SyncGroupItems(IReadOnlyList<GroupListItem> items)
    {
        if (Groups.Count == items.Count && Groups.SequenceEqual(items))
        {
            return;
        }

        Groups.Clear();
        foreach (var item in items)
        {
            Groups.Add(item);
        }
    }

    private async Task EnsureContactDisplayNamesLoadedAsync(CancellationToken cancellationToken)
    {
        if (!contactDisplayNamesLoaded)
        {
            await ReloadContactDisplayNamesAsync(cancellationToken);
        }
    }

    private async Task ReloadContactDisplayNamesAsync(CancellationToken cancellationToken)
    {
        contactDisplayNames = await LoadContactDisplayNamesAsync(cancellationToken);
        contactDisplayNamesLoaded = true;
        RefreshDraftMemberDisplayNames();
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadContactDisplayNamesAsync(CancellationToken cancellationToken)
    {
        var displayNames = new Dictionary<string, string>(StringComparer.Ordinal);
        await foreach (var contact in contacts.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            var displayName = DeepDisplayName.ContactTitle(contact.Id, contact.DisplayName);
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                displayNames[contact.Id.Value] = displayName;
            }
        }

        return displayNames;
    }

    private GroupDraftMemberItem ToDraftMemberItem(SessionId sessionId) =>
        new(sessionId, ResolveContactDisplayName(sessionId));

    private string ResolveContactDisplayName(SessionId sessionId) =>
        contactDisplayNames.TryGetValue(sessionId.Value, out var displayName)
            ? displayName
            : DeepDisplayName.ShortId(sessionId.Value);

    private void RefreshDraftMemberDisplayNames()
    {
        for (var index = 0; index < DraftMembers.Count; index++)
        {
            var current = DraftMembers[index];
            var updated = ToDraftMemberItem(current.SessionId);
            if (current != updated)
            {
                DraftMembers[index] = updated;
            }
        }
    }

    private bool CanAddDraftMember() => TryParseMemberSessionId(out var member)
        && DraftMembers.All(item => item.SessionId != member);

    private bool TryParseMemberSessionId(out SessionId member)
    {
        member = default;
        if (string.IsNullOrWhiteSpace(MemberSessionId))
        {
            return false;
        }

        try
        {
            member = SessionId.Parse(MemberSessionId);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void RaiseDraftMemberPropertiesChanged()
    {
        RaisePropertyChanged(nameof(HasDraftMembers));
        RaisePropertyChanged(nameof(CanAddMember));
        AddDraftMemberCommand.RaiseCanExecuteChanged();
    }
}
