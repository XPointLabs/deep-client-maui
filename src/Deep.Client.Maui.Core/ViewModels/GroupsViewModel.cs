using System.Collections.ObjectModel;
using Deep.Client.Maui.Core.Commands;
using Deep.Client.Maui.Core.Services;

namespace Deep.Client.Maui.Core.ViewModels;

public sealed record GroupListItem(
    string Id,
    string Name,
    int MemberCount,
    int PendingInvitationCount,
    bool HasForkConflict);

public sealed record GroupDraftMemberItem(string CanonicalAddress, string DisplayName);

public sealed class GroupsViewModel : ViewModelBase
{
    private readonly IGroupV1Composer composer;
    private string groupName = string.Empty;
    private string memberAddress = string.Empty;
    private string composerStatus = "Проверяем готовность GroupV1…";
    private string? operationStatus;
    private bool composerReady;
    private GroupDraftMemberItem? selectedDraftMember;
    private GroupListItem? selectedGroup;

    public GroupsViewModel(IGroupV1Composer composer)
    {
        this.composer = composer ?? throw new ArgumentNullException(nameof(composer));
        Groups = [];
        DraftMembers = [];
        CreateCommand = new AsyncCommand(CreateFromUiAsync, () => CanCreateGroup);
        AddDraftMemberCommand = new AsyncCommand(AddDraftMemberAsync, CanAddDraftMember);
        RefreshCommand = new AsyncCommand(RefreshFromUiAsync);
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

    public string MemberAddress
    {
        get => memberAddress;
        set
        {
            if (SetProperty(ref memberAddress, value))
            {
                RaisePropertyChanged(nameof(CanAddMember));
                AddDraftMemberCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ComposerStatus
    {
        get => composerStatus;
        private set => SetProperty(ref composerStatus, value);
    }

    public string? OperationStatus
    {
        get => operationStatus;
        private set
        {
            if (SetProperty(ref operationStatus, value))
            {
                RaisePropertyChanged(nameof(HasOperationStatus));
            }
        }
    }

    public bool HasOperationStatus => !string.IsNullOrWhiteSpace(OperationStatus);
    public bool CanCreateGroups => composerReady;
    public bool CanCreateGroup => composerReady && !string.IsNullOrWhiteSpace(GroupName);
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

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await RunBusyAsync(async ct =>
        {
            var readiness = await composer.GetReadinessAsync(ct).ConfigureAwait(false);
            SetReadiness(readiness);
            await RefreshCoreAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GroupListItem?> CreateGroupFromComposerAsync(
        CancellationToken cancellationToken = default)
    {
        GroupListItem? created = null;
        await RunBusyAsync(async ct =>
        {
            var readiness = await composer.GetReadinessAsync(ct).ConfigureAwait(false);
            SetReadiness(readiness);
            if (!readiness.IsReady)
            {
                throw new GroupV1ComposerException("group-v1-not-ready", readiness.Message);
            }

            var members = DraftMembers
                .Select(static item => new GroupV1DraftMember(
                    item.CanonicalAddress,
                    item.DisplayName))
                .ToArray();
            var result = await composer.CreateAsync(GroupName.Trim(), members, ct)
                .ConfigureAwait(false);
            created = ToListItem(result);
            Upsert(created);
            GroupName = string.Empty;
            DraftMembers.Clear();
            RaiseDraftMemberPropertiesChanged();
            OperationStatus = result.PendingInvitationCount == 0
                ? "GroupV1 создана локально. Подключение GroupChat будет выполнено следующим срезом."
                : $"GroupV1 создана локально; проверено участников: {result.PendingInvitationCount}. Авторинг, отправка и активация приглашений ожидают GroupV1 fanout.";
        }, cancellationToken).ConfigureAwait(false);
        return created;
    }

    public async Task AddDraftMemberAsync(CancellationToken cancellationToken = default)
    {
        var input = MemberAddress.Trim();
        if (input.Length == 0)
        {
            ErrorMessage = "Введите canonical deep1… или deepinvite:DIA1.";
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var readiness = await composer.GetReadinessAsync(ct).ConfigureAwait(false);
            SetReadiness(readiness);
            if (!readiness.IsReady)
            {
                throw new GroupV1ComposerException("group-v1-not-ready", readiness.Message);
            }

            var verified = await composer.VerifyMemberAsync(input, ct).ConfigureAwait(false);
            if (DraftMembers.Any(item => string.Equals(
                    item.CanonicalAddress,
                    verified.CanonicalAddress,
                    StringComparison.Ordinal)))
            {
                MemberAddress = string.Empty;
                return;
            }

            DraftMembers.Add(new GroupDraftMemberItem(
                verified.CanonicalAddress,
                verified.DisplayName));
            MemberAddress = string.Empty;
            OperationStatus = "Участник проверен через ContactV1 и добавлен в черновик.";
            RaiseDraftMemberPropertiesChanged();
        }, cancellationToken).ConfigureAwait(false);
    }

    public void RemoveDraftMember(GroupDraftMemberItem member)
    {
        ArgumentNullException.ThrowIfNull(member);
        DraftMembers.Remove(member);
        if (SelectedDraftMember == member)
        {
            SelectedDraftMember = null;
        }
        RaiseDraftMemberPropertiesChanged();
    }

    public Task<IReadOnlyList<GroupListItem>> RefreshAsync(
        CancellationToken cancellationToken = default) =>
        RefreshWithBusyStateAsync(cancellationToken);

    public void ReportGroupChatHandoffPending(GroupListItem group)
    {
        ArgumentNullException.ThrowIfNull(group);
        OperationStatus =
            $"«{group.Name}» хранится как GroupV1. Открытие и отправка сообщений появятся после подключения GroupChat/fanout.";
        SelectedGroup = null;
    }

    private Task CreateFromUiAsync(CancellationToken cancellationToken) =>
        CreateGroupFromComposerAsync(cancellationToken);

    private Task RefreshFromUiAsync(CancellationToken cancellationToken) =>
        RefreshWithBusyStateAsync(cancellationToken);

    private async Task<IReadOnlyList<GroupListItem>> RefreshWithBusyStateAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<GroupListItem> items = [];
        await RunBusyAsync(async ct =>
        {
            var readiness = await composer.GetReadinessAsync(ct).ConfigureAwait(false);
            SetReadiness(readiness);
            items = await RefreshCoreAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return items;
    }

    private async Task<IReadOnlyList<GroupListItem>> RefreshCoreAsync(
        CancellationToken cancellationToken)
    {
        var items = (await composer.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            .Select(ToListItem)
            .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Sync(items);
        return items;
    }

    private void SetReadiness(GroupV1ComposerReadiness readiness)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        composerReady = readiness.IsReady;
        ComposerStatus = readiness.Message;
        RaisePropertyChanged(nameof(CanCreateGroups));
        RaisePropertyChanged(nameof(CanCreateGroup));
        RaisePropertyChanged(nameof(CanAddMember));
        CreateCommand.RaiseCanExecuteChanged();
        AddDraftMemberCommand.RaiseCanExecuteChanged();
    }

    private static GroupListItem ToListItem(GroupV1ComposerGroup group) => new(
        group.GroupId,
        group.Name,
        group.MemberCount,
        group.PendingInvitationCount,
        group.HasForkConflict);

    private void Upsert(GroupListItem item)
    {
        var index = -1;
        for (var position = 0; position < Groups.Count; position++)
        {
            if (string.Equals(Groups[position].Id, item.Id, StringComparison.Ordinal))
            {
                index = position;
                break;
            }
        }

        if (index < 0)
        {
            Groups.Add(item);
        }
        else
        {
            Groups[index] = item;
        }
    }

    private void Sync(IReadOnlyList<GroupListItem> items)
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

    private bool CanAddDraftMember() =>
        composerReady && !string.IsNullOrWhiteSpace(MemberAddress);

    private void RaiseDraftMemberPropertiesChanged()
    {
        RaisePropertyChanged(nameof(HasDraftMembers));
        RaisePropertyChanged(nameof(CanAddMember));
        AddDraftMemberCommand.RaiseCanExecuteChanged();
    }
}
