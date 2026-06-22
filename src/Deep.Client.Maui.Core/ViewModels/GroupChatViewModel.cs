using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Deep.Client.Maui.Core.Commands;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.Core.ViewModels;

public sealed record GroupChatMessageItem(
    MessageId Id,
    string Body,
    MessageDirection Direction,
    MessageDeliveryState State,
    DateTimeOffset CreatedAt,
    IReadOnlyList<AttachmentMetadata> Attachments)
{
    public bool HasAttachments => Attachments.Count > 0;

    public string AttachmentSummary => Attachments.Count switch
    {
        0 => string.Empty,
        1 => Attachments[0].FileName,
        _ => $"{Attachments.Count} влож."
    };
}

public sealed record GroupMemberItem(SessionId SessionId, GroupMemberRole Role, bool IsPendingRemoval);

public sealed class GroupChatViewModel : ViewModelBase
{
    private const int InitialMessagePageSize = 100;
    private readonly ClientRuntime runtime;
    private readonly IAttachmentPickerService? attachmentPicker;
    private SessionAccount? account;
    private Group? group;
    private string groupTitle = string.Empty;
    private string groupSubtitle = string.Empty;
    private string draft = string.Empty;
    private string memberSessionId = string.Empty;
    private GroupMemberItem? selectedMember;
    private bool canManageMembers;
    private string? statusMessage;
    private bool isStatusError;
    private bool messagesLoaded;
    private DateTimeOffset? oldestLoadedMessageAt;
    private bool hasOlderMessages;

    public GroupChatViewModel(ClientRuntime runtime, IAttachmentPickerService? attachmentPicker = null)
    {
        this.runtime = runtime;
        this.attachmentPicker = attachmentPicker;
        Messages = new ObservableRangeCollection<GroupChatMessageItem>();
        StagedAttachments = [];
        Members = [];
        PendingRemovalMembers = [];
        StagedAttachments.CollectionChanged += OnStagedAttachmentsChanged;
        RefreshCommand = new AsyncCommand(RefreshAsync, () => group is not null);
        SendCommand = new AsyncCommand(SendAsync, CanSend);
        PickAttachmentsCommand = new AsyncCommand(PickAttachmentsAsync, () => attachmentPicker is not null);
        ClearAttachmentsCommand = new AsyncCommand(ClearAttachmentsAsync, () => StagedAttachments.Count > 0);
        AddMemberCommand = new AsyncCommand(AddMemberAsync, () => CanManageMembers && CanParseMemberSessionId());
        PromoteMemberCommand = new AsyncCommand(PromoteMemberAsync, () => CanManageMembers && TryGetTargetMember(out _));
        DemoteMemberCommand = new AsyncCommand(DemoteMemberAsync, () => CanManageMembers && TryGetTargetMember(out _));
        RemoveMemberCommand = new AsyncCommand(RemoveMemberAsync, () => CanManageMembers && TryGetTargetMember(out _));
        MarkPendingRemovalCommand = new AsyncCommand(MarkPendingRemovalAsync, () => CanManageMembers && TryGetTargetMember(out _));
        UndoPendingRemovalCommand = new AsyncCommand(UndoPendingRemovalAsync, () => CanManageMembers && TryGetTargetMember(out _));
    }

    public string GroupTitle
    {
        get => groupTitle;
        private set => SetProperty(ref groupTitle, value);
    }

    public string GroupSubtitle
    {
        get => groupSubtitle;
        private set => SetProperty(ref groupSubtitle, value);
    }

    public ObservableCollection<GroupChatMessageItem> Messages { get; }

    public ObservableCollection<AttachmentMetadata> StagedAttachments { get; }

    public ObservableCollection<GroupMemberItem> Members { get; }

    public ObservableCollection<GroupMemberItem> PendingRemovalMembers { get; }

    public AsyncCommand RefreshCommand { get; }

    public AsyncCommand SendCommand { get; }

    public AsyncCommand PickAttachmentsCommand { get; }

    public AsyncCommand ClearAttachmentsCommand { get; }

    public AsyncCommand AddMemberCommand { get; }

    public AsyncCommand PromoteMemberCommand { get; }

    public AsyncCommand DemoteMemberCommand { get; }

    public AsyncCommand RemoveMemberCommand { get; }

    public AsyncCommand MarkPendingRemovalCommand { get; }

    public AsyncCommand UndoPendingRemovalCommand { get; }

    public string Draft
    {
        get => draft;
        set
        {
            if (SetProperty(ref draft, value))
            {
                SendCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasStagedAttachments => StagedAttachments.Count > 0;

    public string StagedAttachmentSummary => StagedAttachments.Count switch
    {
        0 => "Нет вложений",
        1 => StagedAttachments[0].FileName,
        _ => $"{StagedAttachments.Count} влож."
    };

    public string MemberSessionId
    {
        get => memberSessionId;
        set
        {
            if (SetProperty(ref memberSessionId, value))
            {
                RaiseMemberCommandCanExecuteChanged();
            }
        }
    }

    public GroupMemberItem? SelectedMember
    {
        get => selectedMember;
        set
        {
            if (SetProperty(ref selectedMember, value))
            {
                RaiseMemberCommandCanExecuteChanged();
            }
        }
    }

    public bool CanManageMembers
    {
        get => canManageMembers;
        private set => SetProperty(ref canManageMembers, value);
    }

    public string? StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public bool IsStatusError
    {
        get => isStatusError;
        private set => SetProperty(ref isStatusError, value);
    }

    public async Task OpenFromRouteAsync(string groupId, string? displayName = null, CancellationToken cancellationToken = default)
    {
        account = await runtime.Accounts.GetActiveAccountAsync(cancellationToken)
            ?? throw new InvalidOperationException("Войдите в аккаунт перед открытием групп.");

        var id = ConversationId.Parse(groupId);
        group = await runtime.Conversations.GetGroupAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("Группа не найдена.");

        GroupTitle = string.IsNullOrWhiteSpace(displayName) ? group.Name : displayName!;
        UpdateGroupSummary();
        SyncMembers();
        UpdateMemberPermissions();
        SetStatus("Группа загружена.");
        messagesLoaded = false;
        oldestLoadedMessageAt = null;
        hasOlderMessages = false;

        RefreshCommand.RaiseCanExecuteChanged();
        SendCommand.RaiseCanExecuteChanged();
        RaiseMemberCommandCanExecuteChanged();
        await LoadMessagesAsync(cancellationToken);
        await RefreshAsync(cancellationToken);
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            if (group is null)
            {
                throw new InvalidOperationException("Откройте группу перед обновлением.");
            }

            if (account is not null)
            {
                await runtime.Conversations.ReceiveGroupUpdatesAsync(account.SessionId, ct);
            }

            var latest = await runtime.Conversations.GetGroupAsync(group.Id, ct);
            if (latest is not null)
            {
                group = latest;
                UpdateGroupSummary();
                SyncMembers();
                UpdateMemberPermissions();
                RaiseMemberCommandCanExecuteChanged();
            }

            var activeGroup = group ?? throw new InvalidOperationException("Откройте группу перед обновлением.");
            var received = account is not null
                ? await runtime.Messages.ReceiveGroupAsync(account.SessionId, activeGroup.Id, ct)
                : [];

            if (!messagesLoaded || received.Count > 0)
            {
                await LoadMessagesAsync(ct);
            }

            SetStatus("Группа обновлена.");
        }, cancellationToken);

    public Task SendAsync(CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            if (group is null || account is null)
            {
                throw new InvalidOperationException("Откройте группу перед отправкой сообщений.");
            }

            var body = string.IsNullOrWhiteSpace(Draft)
                ? "[Вложение]"
                : Draft;

            var sent = await runtime.Messages.SendGroupAsync(account.SessionId, group.Id, body, StagedAttachments, cancellationToken: ct);
            Messages.Add(ToItem(sent));
            Draft = string.Empty;
            StagedAttachments.Clear();
            SetStatus("Сообщение отправлено.");
        }, cancellationToken);

    public Task PickAttachmentsAsync(CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            if (attachmentPicker is null)
            {
                throw new InvalidOperationException("Выбор вложений недоступен.");
            }

            var picked = await attachmentPicker.PickAsync(ct);
            foreach (var attachment in picked)
            {
                StagedAttachments.Add(attachment);
            }
        }, cancellationToken);

    public Task ClearAttachmentsAsync(CancellationToken cancellationToken = default)
    {
        StagedAttachments.Clear();
        return Task.CompletedTask;
    }

    public Task LoadOlderMessagesAsync(CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            if (group is null || !hasOlderMessages || oldestLoadedMessageAt is null)
            {
                return;
            }

            var messages = await runtime.Messages.ListConversationMessagesBeforeAsync(
                group.Id,
                oldestLoadedMessageAt.Value,
                InitialMessagePageSize,
                ct);
            if (messages.Count == 0)
            {
                hasOlderMessages = false;
                return;
            }

            var readCursor = await runtime.Messages.GetReadCursorAsync(group.Id, ct);
            var items = new List<GroupChatMessageItem>(messages.Count);
            foreach (var message in messages)
            {
                items.Add(ToItem(ApplyReadCursor(message, readCursor)));
            }

            PrependMessageItems(items);
            oldestLoadedMessageAt = messages[0].CreatedAt;
            hasOlderMessages = messages.Count == InitialMessagePageSize;
        }, cancellationToken);

    public Task AddMemberAsync(CancellationToken cancellationToken = default) =>
        RunMemberMutationAsync((groupId, requestor, member, ct) => runtime.Conversations.AddMemberAsync(groupId, requestor, member, ct), MemberAction.Add, cancellationToken);

    public Task PromoteMemberAsync(CancellationToken cancellationToken = default) =>
        RunMemberMutationAsync((groupId, requestor, member, ct) => runtime.Conversations.PromoteMemberAsync(groupId, requestor, member, ct), MemberAction.Promote, cancellationToken);

    public Task DemoteMemberAsync(CancellationToken cancellationToken = default) =>
        RunMemberMutationAsync((groupId, requestor, member, ct) => runtime.Conversations.DemoteMemberAsync(groupId, requestor, member, ct), MemberAction.Demote, cancellationToken);

    public Task RemoveMemberAsync(CancellationToken cancellationToken = default) =>
        RunMemberMutationAsync((groupId, requestor, member, ct) => runtime.Conversations.RemoveMemberAsync(groupId, requestor, member, ct), MemberAction.Remove, cancellationToken);

    public Task MarkPendingRemovalAsync(CancellationToken cancellationToken = default) =>
        RunMemberMutationAsync((groupId, requestor, member, ct) => runtime.Conversations.MarkMemberPendingRemovalAsync(groupId, requestor, member, true, ct), MemberAction.MarkPending, cancellationToken);

    public Task UndoPendingRemovalAsync(CancellationToken cancellationToken = default) =>
        RunMemberMutationAsync((groupId, requestor, member, ct) => runtime.Conversations.MarkMemberPendingRemovalAsync(groupId, requestor, member, false, ct), MemberAction.UndoPending, cancellationToken);

    private async Task RunMemberMutationAsync(
        Func<ConversationId, SessionId, SessionId, CancellationToken, Task<Group?>> operation,
        MemberAction action,
        CancellationToken cancellationToken)
    {
        await RunBusyAsync(async ct =>
        {
            if (group is null || account is null)
            {
                throw new InvalidOperationException("Откройте группу перед управлением участниками.");
            }

            if (!CanManageMembers)
            {
                SetStatus("Управлять участниками могут только администраторы.", isError: true);
                return;
            }

            if (!TryGetTargetMember(out var member))
            {
                SetStatus("Выберите участника или введите корректный ID аккаунта.", isError: true);
                return;
            }

            if (!ValidateMemberAction(action, member))
            {
                return;
            }

            var updated = await operation(group.Id, account.SessionId, member, ct);
            if (updated is null)
            {
                SetStatus("Обновление участника было отклонено.", isError: true);
                return;
            }

            group = updated;
            MemberSessionId = string.Empty;
            UpdateGroupSummary();
            SyncMembers();
            UpdateMemberPermissions();
            RaiseMemberCommandCanExecuteChanged();
            SetStatus("Состав группы обновлен.");
        }, cancellationToken);
    }

    private bool CanParseMemberSessionId()
    {
        if (string.IsNullOrWhiteSpace(MemberSessionId))
        {
            return false;
        }

        try
        {
            SessionId.Parse(MemberSessionId);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryGetTargetMember(out SessionId member)
    {
        if (SelectedMember is not null)
        {
            member = SelectedMember.SessionId;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(MemberSessionId))
        {
            try
            {
                member = SessionId.Parse(MemberSessionId);
                return true;
            }
            catch
            {
            }
        }

        member = default;
        return false;
    }

    private bool ValidateMemberAction(MemberAction action, SessionId member)
    {
        if (group is null)
        {
            SetStatus("Откройте группу перед управлением участниками.", isError: true);
            return false;
        }

        var existing = group.Members.FirstOrDefault(item => item.SessionId == member);
        var exists = existing is not null;
        var adminCount = group.Members.Count(item => item.Role == GroupMemberRole.Admin);

        switch (action)
        {
            case MemberAction.Add:
                if (exists)
                {
                    SetStatus("Участник уже состоит в группе.", isError: true);
                    return false;
                }

                return true;

            case MemberAction.Promote:
                if (!exists)
                {
                    SetStatus("Участника нет в группе.", isError: true);
                    return false;
                }

                if (existing!.Role == GroupMemberRole.Admin)
                {
                    SetStatus("Участник уже администратор.", isError: true);
                    return false;
                }

                return true;

            case MemberAction.Demote:
                if (!exists)
                {
                    SetStatus("Участника нет в группе.", isError: true);
                    return false;
                }

                if (existing!.Role != GroupMemberRole.Admin)
                {
                    SetStatus("Понизить можно только администратора.", isError: true);
                    return false;
                }

                if (adminCount <= 1)
                {
                    SetStatus("Нельзя понизить последнего администратора.", isError: true);
                    return false;
                }

                return true;

            case MemberAction.Remove:
                if (!exists)
                {
                    SetStatus("Участника нет в группе.", isError: true);
                    return false;
                }

                if (existing!.Role == GroupMemberRole.Admin && adminCount <= 1)
                {
                    SetStatus("Нельзя удалить последнего администратора.", isError: true);
                    return false;
                }

                return true;

            case MemberAction.MarkPending:
                if (!exists)
                {
                    SetStatus("Участника нет в группе.", isError: true);
                    return false;
                }

                if (existing!.IsPendingRemoval)
                {
                    SetStatus("Участник уже ожидает удаления.", isError: true);
                    return false;
                }

                return true;

            case MemberAction.UndoPending:
                if (!exists)
                {
                    SetStatus("Участника нет в группе.", isError: true);
                    return false;
                }

                if (!existing!.IsPendingRemoval)
                {
                    SetStatus("Участник не ожидает удаления.", isError: true);
                    return false;
                }

                return true;

            default:
                return true;
        }
    }

    private void UpdateGroupSummary()
    {
        if (group is null)
        {
            GroupSubtitle = string.Empty;
            return;
        }

        GroupSubtitle = $"{group.Members.Count} участн. | {group.Members.Count(member => member.Role == GroupMemberRole.Admin)} адм.";
    }

    private void SyncMembers()
    {
        if (group is null)
        {
            Members.Clear();
            PendingRemovalMembers.Clear();
            return;
        }

        var members = group.Members
            .OrderByDescending(item => item.Role)
            .ThenBy(item => item.SessionId.Value, StringComparer.Ordinal)
            .Select(member => new GroupMemberItem(member.SessionId, member.Role, member.IsPendingRemoval))
            .ToArray();

        SyncMemberItems(Members, members);
        SyncMemberItems(PendingRemovalMembers, members.Where(member => member.IsPendingRemoval).ToArray());
    }

    private void UpdateMemberPermissions()
    {
        CanManageMembers = account is not null && group is not null && group.HasAdmin(account.SessionId);
    }

    private bool CanSend() =>
        group is not null
        && account is not null
        && (!string.IsNullOrWhiteSpace(Draft) || StagedAttachments.Count > 0);

    private static GroupChatMessageItem ToItem(Message message) =>
        new(message.Id, message.Body, message.Direction, message.DeliveryState, message.CreatedAt, message.Attachments);

    private async Task LoadMessagesAsync(CancellationToken cancellationToken)
    {
        if (group is null)
        {
            return;
        }

        var messages = await runtime.Messages.ListRecentConversationMessagesAsync(
            group.Id,
            InitialMessagePageSize,
            cancellationToken);
        var readAt = await runtime.Messages.MarkConversationAsReadAsync(
            group.Id,
            LatestIncomingOrNow(messages),
            cancellationToken);
        var items = new List<GroupChatMessageItem>(messages.Count);
        foreach (var message in messages)
        {
            items.Add(ToItem(ApplyReadCursor(message, readAt)));
        }

        SyncMessageItems(items);
        messagesLoaded = true;
        oldestLoadedMessageAt = messages.Count == 0 ? null : messages[0].CreatedAt;
        hasOlderMessages = messages.Count == InitialMessagePageSize;
    }

    private DateTimeOffset LatestIncomingOrNow(IReadOnlyList<Message> messages)
    {
        var readAt = runtime.Clock.UtcNow;
        foreach (var message in messages)
        {
            if (message.Direction == MessageDirection.Incoming && message.CreatedAt > readAt)
            {
                readAt = message.CreatedAt;
            }
        }

        return readAt;
    }

    private static Message ApplyReadCursor(Message message, DateTimeOffset? readAt) =>
        readAt is not null
        && message.Direction == MessageDirection.Incoming
        && message.CreatedAt <= readAt.Value
            ? message.Mark(MessageDeliveryState.Read, readAt)
            : message;

    private void SyncMessageItems(IReadOnlyList<GroupChatMessageItem> items)
    {
        if (Messages.Count <= items.Count && HasSamePrefix(items))
        {
            for (var index = 0; index < Messages.Count; index++)
            {
                if (!SameMessageItem(Messages[index], items[index]))
                {
                    Messages[index] = items[index];
                }
            }

            var missingItems = items.Skip(Messages.Count).ToArray();
            if (missingItems.Length == 1)
            {
                Messages.Add(missingItems[0]);
            }
            else
            {
                MessageItems.AddRange(missingItems);
            }

            return;
        }

        MessageItems.ReplaceRange(items);
    }

    private ObservableRangeCollection<GroupChatMessageItem> MessageItems => (ObservableRangeCollection<GroupChatMessageItem>)Messages;

    private void PrependMessageItems(IReadOnlyList<GroupChatMessageItem> items)
    {
        for (var index = items.Count - 1; index >= 0; index--)
        {
            Messages.Insert(0, items[index]);
        }
    }

    private bool HasSamePrefix(IReadOnlyList<GroupChatMessageItem> items)
    {
        for (var index = 0; index < Messages.Count; index++)
        {
            if (Messages[index].Id != items[index].Id)
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameMessageItem(GroupChatMessageItem left, GroupChatMessageItem right) =>
        left.Id == right.Id
        && left.Body == right.Body
        && left.Direction == right.Direction
        && left.State == right.State
        && left.CreatedAt == right.CreatedAt
        && left.Attachments.SequenceEqual(right.Attachments);

    private static void SyncMemberItems(
        ObservableCollection<GroupMemberItem> target,
        IReadOnlyList<GroupMemberItem> items)
    {
        if (target.Count == items.Count && target.SequenceEqual(items))
        {
            return;
        }

        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private void OnStagedAttachmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RaisePropertyChanged(nameof(HasStagedAttachments));
        RaisePropertyChanged(nameof(StagedAttachmentSummary));
        SendCommand.RaiseCanExecuteChanged();
        ClearAttachmentsCommand.RaiseCanExecuteChanged();
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusMessage = message;
        IsStatusError = isError;
    }

    private void RaiseMemberCommandCanExecuteChanged()
    {
        AddMemberCommand.RaiseCanExecuteChanged();
        PromoteMemberCommand.RaiseCanExecuteChanged();
        DemoteMemberCommand.RaiseCanExecuteChanged();
        RemoveMemberCommand.RaiseCanExecuteChanged();
        MarkPendingRemovalCommand.RaiseCanExecuteChanged();
        UndoPendingRemovalCommand.RaiseCanExecuteChanged();
    }

    private enum MemberAction
    {
        Add,
        Promote,
        Demote,
        Remove,
        MarkPending,
        UndoPending
    }
}
