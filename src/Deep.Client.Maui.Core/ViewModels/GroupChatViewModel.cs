using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Deep.Client.Maui.Core.Commands;
using Deep.Client.Maui.Core.Presentation;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.Core.ViewModels;

public sealed class GroupChatMessageItem : INotifyPropertyChanged
{
    private string senderLabel;
    private bool isVoicePlaying;
    private double voicePlaybackProgress;
    private string? voicePlaybackPositionLabel;
    private string? imagePreviewPath;
    private readonly bool hasVisibleBody;
    private readonly bool isVoiceMessage;
    private readonly bool isImageMessage;
    private readonly AttachmentMetadata? primaryImageAttachment;
    private readonly bool hasMultipleImages;
    private readonly bool hasGenericAttachments;
    private readonly bool hasNonVoiceAttachments;
    private readonly string attachmentSummary;
    private readonly string attachmentTitle;
    private readonly string attachmentSubtitle;
    private readonly string voiceDurationLabel;
    private readonly string imageCountLabel;
    private readonly double imagePreviewWidthRequest;
    private readonly double imagePreviewHeightRequest;
    private readonly string? voiceAttachmentId;
    private readonly IReadOnlyList<MessageReactionChip> reactionChips;

    public GroupChatMessageItem(
        MessageId id,
        string body,
        MessageDirection direction,
        MessageDeliveryState state,
        DateTimeOffset createdAt,
        IReadOnlyList<AttachmentMetadata> attachments,
        string senderLabel,
        MessageReply? replyTo,
        IReadOnlyList<MessageReaction> reactions,
        SessionId? senderId = null)
    {
        Id = id;
        Body = body;
        Direction = direction;
        State = state;
        CreatedAt = createdAt;
        Attachments = attachments;
        this.senderLabel = senderLabel;
        SenderId = senderId;
        ReplyTo = replyTo;
        Reactions = reactions;
        hasVisibleBody = MessageAttachmentPresentation.HasVisibleBody(body);
        isVoiceMessage = MessageAttachmentPresentation.IsVoiceMessage(attachments);
        isImageMessage = MessageAttachmentPresentation.IsInlineImage(attachments);
        primaryImageAttachment = MessageAttachmentPresentation.PrimaryInlineImage(attachments);
        hasMultipleImages = isImageMessage && attachments.Count > 1;
        hasGenericAttachments = attachments.Count > 0 && !isVoiceMessage && !isImageMessage;
        hasNonVoiceAttachments = attachments.Count > 0 && !isVoiceMessage;
        attachmentSummary = MessageAttachmentPresentation.Summary(attachments);
        attachmentTitle = MessageAttachmentPresentation.Title(attachments);
        attachmentSubtitle = MessageAttachmentPresentation.Subtitle(attachments);
        voiceDurationLabel = MessageAttachmentPresentation.VoiceDuration(attachments);
        imageCountLabel = MessageAttachmentPresentation.ImageCountLabel(attachments);
        var previewSize = MessageAttachmentPresentation.ImagePreviewSize(primaryImageAttachment);
        imagePreviewWidthRequest = previewSize.Width;
        imagePreviewHeightRequest = previewSize.Height;
        voiceAttachmentId = isVoiceMessage ? attachments[0].AttachmentId : null;
        reactionChips = reactions.Count == 0
            ? []
            : reactions
                .GroupBy(static reaction => reaction.Emoji, StringComparer.Ordinal)
                .Select(group => new MessageReactionChip(Id, group.Key, group.Count()))
                .ToArray();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MessageId Id { get; }

    public string Body { get; }

    public MessageDirection Direction { get; }

    public MessageDeliveryState State { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset LocalCreatedAt => CreatedAt.ToLocalTime();

    public IReadOnlyList<AttachmentMetadata> Attachments { get; }

    public SessionId? SenderId { get; }

    public string SenderLabel => senderLabel;

    public MessageReply? ReplyTo { get; }

    public IReadOnlyList<MessageReaction> Reactions { get; }

    public bool HasAttachments => Attachments.Count > 0;

    public bool IsOutgoing => Direction == MessageDirection.Outgoing;

    public bool ShowSenderLabel => Direction == MessageDirection.Incoming && !string.IsNullOrWhiteSpace(SenderLabel);

    public bool IsStatusVisible => MessageStatusPresentation.IsVisible(Direction, State);

    public bool IsReadStatus => State == MessageDeliveryState.Read;

    public bool IsFailedStatus => State == MessageDeliveryState.Failed;

    public string StatusGlyph => MessageStatusPresentation.Glyph(State);

    public string StatusDescription => MessageStatusPresentation.Description(State);

    public bool HasReply => ReplyTo is not null;

    public string ReplyPreview => ReplyTo?.Body ?? string.Empty;

    public IReadOnlyList<MessageReactionChip> ReactionChips => reactionChips;

    public bool HasReactions => Reactions.Count > 0;

    public bool HasVisibleBody => hasVisibleBody;

    public bool IsVoiceMessage => isVoiceMessage;

    public bool IsImageMessage => isImageMessage;

    public AttachmentMetadata? PrimaryImageAttachment => primaryImageAttachment;

    public bool HasImagePreview => IsImageMessage && !string.IsNullOrWhiteSpace(ImagePreviewPath);

    public bool IsImagePreviewLoading => IsImageMessage && string.IsNullOrWhiteSpace(ImagePreviewPath);

    public bool HasMultipleImages => hasMultipleImages;

    public bool HasGenericAttachments => hasGenericAttachments;

    public bool HasNonVoiceAttachments => hasNonVoiceAttachments;

    public string AttachmentSummary => attachmentSummary;

    public string AttachmentTitle => attachmentTitle;

    public string AttachmentSubtitle => attachmentSubtitle;

    public string VoiceDurationLabel => voiceDurationLabel;

    public string ImageCountLabel => imageCountLabel;

    public double ImagePreviewWidthRequest => imagePreviewWidthRequest;

    public double ImagePreviewHeightRequest => imagePreviewHeightRequest;

    public string? ImagePreviewPath
    {
        get => imagePreviewPath;
        private set
        {
            if (SetProperty(ref imagePreviewPath, value))
            {
                RaisePropertyChanged(nameof(HasImagePreview));
                RaisePropertyChanged(nameof(IsImagePreviewLoading));
            }
        }
    }

    public string VoicePlaybackLabel => isVoicePlaying && !string.IsNullOrWhiteSpace(voicePlaybackPositionLabel)
        ? voicePlaybackPositionLabel
        : VoiceDurationLabel;

    public bool IsVoicePlaying
    {
        get => isVoicePlaying;
        private set
        {
            if (SetProperty(ref isVoicePlaying, value))
            {
                RaisePropertyChanged(nameof(IsVoiceIdle));
            }
        }
    }

    public bool IsVoiceIdle => !IsVoicePlaying;

    public double VoicePlaybackProgress
    {
        get => voicePlaybackProgress;
        private set => SetProperty(ref voicePlaybackProgress, value);
    }

    public string? VoiceAttachmentId => voiceAttachmentId;

    public void SetVoicePlayback(bool isPlaying, double progress, TimeSpan position)
    {
        IsVoicePlaying = isPlaying;
        VoicePlaybackProgress = Math.Clamp(progress, 0, 1);
        voicePlaybackPositionLabel = FormatVoicePosition(position);
        RaisePropertyChanged(nameof(VoicePlaybackLabel));
    }

    public void ClearVoicePlayback()
    {
        IsVoicePlaying = false;
        VoicePlaybackProgress = 0;
        voicePlaybackPositionLabel = null;
        RaisePropertyChanged(nameof(VoicePlaybackLabel));
    }

    public void SetImagePreviewPath(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            ImagePreviewPath = path;
        }
    }

    public void UpdateSenderLabel(string value)
    {
        if (SetProperty(ref senderLabel, value, nameof(SenderLabel)))
        {
            RaisePropertyChanged(nameof(ShowSenderLabel));
        }
    }

    private static string FormatVoicePosition(TimeSpan value)
    {
        var seconds = Math.Max(0, (int)Math.Round(value.TotalSeconds));
        return $"{seconds / 60:00}:{seconds % 60:00}";
    }

    private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return false;
        }

        storage = value;
        RaisePropertyChanged(propertyName);
        return true;
    }

    private void RaisePropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record GroupMemberItem(
    SessionId SessionId,
    string DisplayName,
    GroupMemberRole Role,
    bool IsPendingRemoval);

public sealed class GroupChatViewModel : ViewModelBase
{
    private const int InitialMessagePageSize = 20;
    private const int HistoryMessagePageSize = 20;
    private readonly ClientRuntime runtime;
    private readonly IContactRepository contacts;
    private readonly IAttachmentPickerService? attachmentPicker;
    private readonly IVoiceMessageRecorder? voiceRecorder;
    private readonly Dictionary<string, string> senderLabels = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string> contactDisplayNames = new Dictionary<string, string>(StringComparer.Ordinal);
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
    private MessageId? oldestLoadedMessageId;
    private bool hasOlderMessages;
    private bool isRecordingVoice;
    private GroupChatMessageItem? replyingTo;

    public GroupChatViewModel(
        ClientRuntime runtime,
        IAttachmentPickerService? attachmentPicker = null,
        IVoiceMessageRecorder? voiceRecorder = null)
    {
        this.runtime = runtime;
        contacts = (IContactRepository)runtime.Store;
        this.attachmentPicker = attachmentPicker;
        this.voiceRecorder = voiceRecorder;
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
        CancelReplyCommand = new AsyncCommand(CancelReplyAsync, () => ReplyingTo is not null);
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

    public AsyncCommand CancelReplyCommand { get; }

    public string Draft
    {
        get => draft;
        set
        {
            if (SetProperty(ref draft, value))
            {
                SendCommand.RaiseCanExecuteChanged();
                RaiseComposerStateChanged();
            }
        }
    }

    public bool HasStagedAttachments => StagedAttachments.Count > 0;

    public bool HasComposedContent => !string.IsNullOrWhiteSpace(Draft) || StagedAttachments.Count > 0;

    public bool CanRecordVoice => voiceRecorder?.IsSupported == true
        && group is not null
        && account is not null;

    public bool IsRecordingVoice
    {
        get => isRecordingVoice;
        private set
        {
            if (SetProperty(ref isRecordingVoice, value))
            {
                RaiseComposerStateChanged();
                RaisePropertyChanged(nameof(VoiceRecordingLabel));
            }
        }
    }

    public bool ShowVoiceButton => CanRecordVoice && (!HasComposedContent || IsRecordingVoice);

    public bool ShowSendButton => HasComposedContent && !IsRecordingVoice;

    public string VoiceRecordingLabel => IsRecordingVoice
        ? "Идет запись голосового сообщения"
        : string.Empty;

    public string StagedAttachmentSummary => StagedAttachments.Count switch
    {
        0 => "Нет вложений",
        1 => StagedAttachments[0].FileName,
        _ => $"{StagedAttachments.Count} влож."
    };

    public GroupChatMessageItem? ReplyingTo
    {
        get => replyingTo;
        private set
        {
            if (SetProperty(ref replyingTo, value))
            {
                RaisePropertyChanged(nameof(IsReplying));
                RaisePropertyChanged(nameof(ReplyingToPreview));
                CancelReplyCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsReplying => ReplyingTo is not null;

    public string ReplyingToPreview => ReplyingTo?.Body ?? string.Empty;

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
        var id = ConversationId.Parse(groupId);
        senderLabels.Clear();
        if (runtime.Store is IGroupConversationOpenRepository openRepository)
        {
            var snapshot = await openRepository.OpenGroupConversationAsync(
                id,
                InitialMessagePageSize,
                runtime.Clock.UtcNow,
                cancellationToken);
            if (snapshot is null)
            {
                throw new InvalidOperationException("Группа не найдена или аккаунт не активен.");
            }

            account = snapshot.ActiveAccount;
            group = snapshot.Group;
            SeedSenderLabels(snapshot.RecentMessages, snapshot.SenderContacts);
            await ReloadContactDisplayNamesAsync(cancellationToken);
            var items = new List<GroupChatMessageItem>(snapshot.RecentMessages.Count);
            foreach (var message in snapshot.RecentMessages)
            {
                items.Add(ToItem(message));
            }

            SyncMessageItems(items);
            messagesLoaded = true;
            oldestLoadedMessageAt = snapshot.RecentMessages.Count == 0 ? null : snapshot.RecentMessages[0].CreatedAt;
            oldestLoadedMessageId = snapshot.RecentMessages.Count == 0 ? null : snapshot.RecentMessages[0].Id;
            hasOlderMessages = snapshot.RecentMessages.Count == InitialMessagePageSize;
        }
        else
        {
            account = await runtime.Accounts.GetActiveAccountAsync(cancellationToken)
                ?? throw new InvalidOperationException("Войдите в аккаунт перед открытием групп.");
            group = await runtime.Conversations.GetGroupAsync(id, cancellationToken)
                ?? throw new InvalidOperationException("Группа не найдена.");
            messagesLoaded = false;
            oldestLoadedMessageAt = null;
            oldestLoadedMessageId = null;
            hasOlderMessages = false;
            await ReloadContactDisplayNamesAsync(cancellationToken);
            await LoadMessagesAsync(cancellationToken);
        }

        GroupTitle = string.IsNullOrWhiteSpace(displayName) ? group.Name : displayName!;
        UpdateGroupSummary();
        SyncMembers();
        UpdateMemberPermissions();
        SetStatus("Группа загружена.");
        RefreshCommand.RaiseCanExecuteChanged();
        SendCommand.RaiseCanExecuteChanged();
        RaiseMemberCommandCanExecuteChanged();
        RaiseComposerStateChanged();
    }

    public Task RefreshContactDisplayNamesAsync(CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            await ReloadContactDisplayNamesAsync(ct);
            SyncMembers();
        }, cancellationToken);

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
            }

            await ReloadContactDisplayNamesAsync(ct);
            UpdateGroupSummary();
            SyncMembers();
            UpdateMemberPermissions();
            RaiseMemberCommandCanExecuteChanged();

            var activeGroup = group ?? throw new InvalidOperationException("Откройте группу перед обновлением.");
            var received = account is not null
                ? await runtime.Messages.ReceiveGroupAsync(account.SessionId, activeGroup.Id, ct)
                : [];

            if (!messagesLoaded)
            {
                await LoadMessagesAsync(ct);
            }
            else if (received.Count > 0)
            {
                var readAt = await runtime.Messages.MarkConversationAsReadAsync(
                    activeGroup.Id,
                    runtime.Clock.UtcNow,
                    ct);
                foreach (var message in received.OrderBy(message => message.CreatedAt))
                {
                    UpsertMessageItem(MarkIncomingRead(message, readAt));
                }
            }

            SetStatus("Группа обновлена.");
        }, cancellationToken);

    public async Task SendAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (group is null || account is null)
            {
                throw new InvalidOperationException("Откройте группу перед отправкой сообщений.");
            }

            ErrorMessage = null;
            var body = string.IsNullOrWhiteSpace(Draft)
                ? MessageAttachmentPresentation.GenericAttachmentPlaceholder
                : Draft;
            var attachments = StagedAttachments.ToArray();

            SetStatus("Отправка сообщения...");
            await QueueMessageAsync(body, attachments, cancellationToken);
            Draft = string.Empty;
            StagedAttachments.Clear();
            ReplyingTo = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            SetStatus(ex.Message, isError: true);
        }
    }

    public async Task StartVoiceRecordingAsync(CancellationToken cancellationToken = default)
    {
        if (voiceRecorder is null)
        {
            ErrorMessage = "Запись голосовых сообщений недоступна на этом устройстве.";
            SetStatus(ErrorMessage, isError: true);
            return;
        }

        try
        {
            ErrorMessage = null;
            await voiceRecorder.StartAsync(cancellationToken);
            IsRecordingVoice = true;
            SetStatus("Запись голосового сообщения...");
        }
        catch (Exception ex)
        {
            IsRecordingVoice = false;
            ErrorMessage = ex.Message;
            SetStatus(ex.Message, isError: true);
        }
    }

    public async Task StopVoiceRecordingAndSendAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRecordingVoice || voiceRecorder is null)
        {
            return;
        }

        try
        {
            ErrorMessage = null;
            IsRecordingVoice = false;
            var attachment = await voiceRecorder.StopAsync(cancellationToken);
            if (attachment is null)
            {
                SetStatus("Голосовое сообщение отменено.");
                return;
            }

            SetStatus("Отправка голосового сообщения...");
            await QueueMessageAsync(
                MessageAttachmentPresentation.VoiceAttachmentPlaceholder,
                [attachment],
                cancellationToken);
        }
        catch (Exception ex)
        {
            await CancelVoiceRecordingAsync(cancellationToken);
            ErrorMessage = ex.Message;
            SetStatus(ex.Message, isError: true);
        }
    }

    public async Task CancelVoiceRecordingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (voiceRecorder is not null)
            {
                await voiceRecorder.CancelAsync(cancellationToken);
            }
        }
        finally
        {
            IsRecordingVoice = false;
        }
        SetStatus("Запись отменена.");
    }

    public void BeginReply(GroupChatMessageItem message) => ReplyingTo = message;

    public Task CancelReplyAsync(CancellationToken cancellationToken = default)
    {
        ReplyingTo = null;
        return Task.CompletedTask;
    }

    public Task ToggleReactionAsync(GroupChatMessageItem message, string emoji, CancellationToken cancellationToken = default) =>
        ToggleReactionAsync(message.Id, emoji, cancellationToken);

    public Task ToggleReactionAsync(MessageId messageId, string emoji, CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            if (account is null || group is null)
            {
                return;
            }

            var message = Messages.FirstOrDefault(item => item.Id == messageId);
            if (message is null)
            {
                return;
            }

            var remove = message.Reactions.Any(reaction =>
                reaction.Reactor == account.SessionId
                && string.Equals(reaction.Emoji, emoji, StringComparison.Ordinal));
            var updated = await runtime.Messages.SendGroupReactionAsync(
                account.SessionId,
                group.Id,
                message.Id,
                emoji,
                remove,
                ct);
            if (updated is not null)
            {
                ReplaceMessageItem(updated);
            }
        }, cancellationToken);

    private async Task DispatchPendingMessageAsync(Message pending)
    {
        try
        {
            var sent = await runtime.Messages.DispatchGroupAsync(pending);
            ReplaceMessageItem(sent);
            SetStatus("Сообщение отправлено.");
        }
        catch (Exception ex)
        {
            ReplaceMessageItem(pending.Mark(MessageDeliveryState.Failed));
            ErrorMessage = $"Не удалось отправить сообщение: {ex.Message}";
            SetStatus(ErrorMessage, isError: true);
        }
    }

    public Task PickAttachmentsAsync(CancellationToken cancellationToken = default) =>
        PickAttachmentsAsync(null, cancellationToken);

    public Task PickAttachmentsAsync(AttachmentPickKind kind, CancellationToken cancellationToken = default) =>
        PickAttachmentsAsync((AttachmentPickKind?)kind, cancellationToken);

    private Task PickAttachmentsAsync(AttachmentPickKind? kind, CancellationToken cancellationToken) =>
        RunBusyAsync(async ct =>
        {
            if (attachmentPicker is null)
            {
                throw new InvalidOperationException("Выбор вложений недоступен.");
            }

            var picked = kind is { } requestedKind && attachmentPicker is ITypedAttachmentPickerService typedPicker
                ? await typedPicker.PickAsync(requestedKind, ct)
                : await attachmentPicker.PickAsync(ct);
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

    public Task DeleteMessageAsync(GroupChatMessageItem message, CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            if (await runtime.Messages.DeleteMessageAsync(message.Id, ct))
            {
                RemoveMessageItem(message.Id);
                if (ReplyingTo?.Id == message.Id)
                {
                    ReplyingTo = null;
                }
            }
        }, cancellationToken);

    public Task LoadOlderMessagesAsync(CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            if (group is null || !hasOlderMessages || oldestLoadedMessageAt is null || oldestLoadedMessageId is null)
            {
                return;
            }

            var messages = await runtime.Messages.ListConversationMessagesBeforeAsync(
                group.Id,
                oldestLoadedMessageAt.Value,
                oldestLoadedMessageId.Value,
                HistoryMessagePageSize,
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
            oldestLoadedMessageId = messages[0].Id;
            hasOlderMessages = messages.Count == HistoryMessagePageSize;
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
            .Select(member => new GroupMemberItem(
                member.SessionId,
                ResolveContactDisplayName(member.SessionId),
                member.Role,
                member.IsPendingRemoval))
            .ToArray();

        SyncMemberItems(Members, members);
        SyncMemberItems(PendingRemovalMembers, members.Where(member => member.IsPendingRemoval).ToArray());
    }

    private async Task ReloadContactDisplayNamesAsync(CancellationToken cancellationToken)
    {
        contactDisplayNames = await LoadContactDisplayNamesAsync(cancellationToken);
        foreach (var senderId in senderLabels.Keys.ToArray())
        {
            senderLabels[senderId] = ResolveSenderDisplayName(senderId);
        }

        foreach (var message in Messages)
        {
            if (message.Direction == MessageDirection.Incoming && message.SenderId is { } senderId)
            {
                message.UpdateSenderLabel(ResolveSenderDisplayName(senderId.Value));
            }
        }
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

    private string ResolveContactDisplayName(SessionId sessionId) =>
        contactDisplayNames.TryGetValue(sessionId.Value, out var displayName)
            ? displayName
            : DeepDisplayName.ShortId(sessionId.Value);

    private string ResolveSenderDisplayName(string sessionId) =>
        contactDisplayNames.TryGetValue(sessionId, out var displayName)
            ? displayName
            : AbbreviateSessionId(sessionId);

    private void UpdateMemberPermissions()
    {
        CanManageMembers = account is not null && group is not null && group.HasAdmin(account.SessionId);
    }

    private bool CanSend() =>
        group is not null
        && account is not null
        && (!string.IsNullOrWhiteSpace(Draft) || StagedAttachments.Count > 0);

    private GroupChatMessageItem ToItem(Message message)
    {
        var senderLabel = string.Empty;
        if (message.Direction == MessageDirection.Incoming)
        {
            if (!senderLabels.TryGetValue(message.Sender.Value, out senderLabel))
            {
                senderLabel = ResolveSenderDisplayName(message.Sender.Value);
                senderLabels[message.Sender.Value] = senderLabel;
            }
        }

        return new GroupChatMessageItem(
            message.Id,
            message.Body,
            message.Direction,
            message.DeliveryState,
            message.CreatedAt,
            message.Attachments,
            senderLabel,
            message.ReplyTo,
            message.ReactionItems,
            message.Sender);
    }

    private void ReplaceMessageItem(Message message)
    {
        for (var index = 0; index < Messages.Count; index++)
        {
            if (Messages[index].Id == message.Id)
            {
                Messages[index] = ToItem(message);
                return;
            }
        }
    }

    private void UpsertMessageItem(Message message)
    {
        var item = ToItem(message);
        for (var index = 0; index < Messages.Count; index++)
        {
            if (Messages[index].Id == item.Id)
            {
                if (!SameMessageItem(Messages[index], item))
                {
                    Messages[index] = item;
                }

                return;
            }

            if (Messages[index].CreatedAt > item.CreatedAt)
            {
                Messages.Insert(index, item);
                return;
            }
        }

        Messages.Add(item);
    }

    private void RemoveMessageItem(MessageId messageId)
    {
        for (var index = 0; index < Messages.Count; index++)
        {
            if (Messages[index].Id == messageId)
            {
                Messages.RemoveAt(index);
                return;
            }
        }
    }

    private static string AbbreviateSessionId(string value) => value.Length <= 14
        ? value
        : $"{value[..8]}…{value[^4..]}";

    private async Task QueueMessageAsync(
        string body,
        IReadOnlyList<AttachmentMetadata> attachments,
        CancellationToken cancellationToken)
    {
        if (group is null || account is null)
        {
            throw new InvalidOperationException("Откройте группу перед отправкой сообщений.");
        }

        var messageId = MessageId.NewId();
        var createdAt = runtime.Clock.UtcNow;
        var reply = ReplyingTo is null
            ? null
            : new MessageReply(
                ReplyingTo.Id,
                account.SessionId,
                ReplyingTo.Body);
        var optimistic = new Message(
            messageId,
            group.Id,
            account.SessionId,
            Recipient: null,
            body.Trim(),
            MessageDirection.Outgoing,
            MessageDeliveryState.Sending,
            createdAt,
            attachments,
            ReplyTo: reply);
        Messages.Add(new GroupChatMessageItem(
            optimistic.Id,
            optimistic.Body,
            optimistic.Direction,
            optimistic.DeliveryState,
            optimistic.CreatedAt,
            optimistic.Attachments,
            string.Empty,
            optimistic.ReplyTo,
            optimistic.ReactionItems));

        try
        {
            var pending = await runtime.Messages.QueueGroupAsync(
                account.SessionId,
                group.Id,
                body,
                attachments,
                ReplyingTo?.Id,
                messageId,
                createdAt,
                cancellationToken);
            ReplaceMessageItem(pending);
            ReplyingTo = null;
            _ = DispatchPendingMessageAsync(pending);
        }
        catch
        {
            RemoveMessageItem(messageId);
            throw;
        }
    }

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
            runtime.Clock.UtcNow,
            cancellationToken);
        var items = new List<GroupChatMessageItem>(messages.Count);
        foreach (var message in messages)
        {
            items.Add(ToItem(MarkIncomingRead(message, readAt)));
        }

        SyncMessageItems(items);
        messagesLoaded = true;
        oldestLoadedMessageAt = messages.Count == 0 ? null : messages[0].CreatedAt;
        oldestLoadedMessageId = messages.Count == 0 ? null : messages[0].Id;
        hasOlderMessages = messages.Count == InitialMessagePageSize;
    }

    private static Message ApplyReadCursor(Message message, DateTimeOffset? readAt) =>
        readAt is not null
        && message.Direction == MessageDirection.Incoming
        && message.CreatedAt <= readAt.Value
            ? message.Mark(MessageDeliveryState.Read, readAt)
            : message;

    private static Message MarkIncomingRead(Message message, DateTimeOffset readAt) =>
        message.Direction == MessageDirection.Incoming
            ? message.Mark(MessageDeliveryState.Read, readAt)
            : message;

    private void SeedSenderLabels(
        IReadOnlyList<Message> messages,
        IReadOnlyDictionary<SessionId, Contact> contactsBySender)
    {
        senderLabels.Clear();
        foreach (var sender in messages
                     .Where(static message => message.Direction == MessageDirection.Incoming)
                     .Select(static message => message.Sender)
                     .Distinct())
        {
            var displayName = contactsBySender.TryGetValue(sender, out var contact)
                ? DeepDisplayName.ContactTitle(sender, contact.DisplayName)
                : string.Empty;
            senderLabels[sender.Value] = string.IsNullOrWhiteSpace(displayName)
                ? AbbreviateSessionId(sender.Value)
                : displayName;
        }
    }

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

        if (items.Count > 0 && Messages.Count > items.Count && HasSameSuffix(items, out var suffixOffset))
        {
            for (var index = 0; index < items.Count; index++)
            {
                var messageIndex = suffixOffset + index;
                if (!SameMessageItem(Messages[messageIndex], items[index]))
                {
                    Messages[messageIndex] = items[index];
                }
            }

            return;
        }

        MessageItems.ReplaceRange(items);
    }

    private ObservableRangeCollection<GroupChatMessageItem> MessageItems => (ObservableRangeCollection<GroupChatMessageItem>)Messages;

    private void PrependMessageItems(IReadOnlyList<GroupChatMessageItem> items)
    {
        MessageItems.InsertRange(0, items);
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

    private bool HasSameSuffix(IReadOnlyList<GroupChatMessageItem> items, out int offset)
    {
        offset = Messages.Count - items.Count;
        if (items.Count == 0 || offset < 0)
        {
            return false;
        }

        for (var index = 0; index < items.Count; index++)
        {
            if (Messages[offset + index].Id != items[index].Id)
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
        && left.Attachments.SequenceEqual(right.Attachments)
        && left.SenderLabel == right.SenderLabel
        && Equals(left.ReplyTo, right.ReplyTo)
        && left.Reactions.SequenceEqual(right.Reactions);

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
        RaiseComposerStateChanged();
        SendCommand.RaiseCanExecuteChanged();
        ClearAttachmentsCommand.RaiseCanExecuteChanged();
    }

    private void RaiseComposerStateChanged()
    {
        RaisePropertyChanged(nameof(HasComposedContent));
        RaisePropertyChanged(nameof(CanRecordVoice));
        RaisePropertyChanged(nameof(ShowVoiceButton));
        RaisePropertyChanged(nameof(ShowSendButton));
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
