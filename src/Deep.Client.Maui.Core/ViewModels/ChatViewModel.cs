using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Deep.Client.Maui.Core.Commands;
using Deep.Client.Maui.Core.Presentation;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Platform;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;
using DeepDisplayName = Deep.Client.Maui.Core.Presentation.DeepDisplayName;

namespace Deep.Client.Maui.Core.ViewModels;

public sealed record MessageReactionChip(MessageId MessageId, string Emoji, int Count);

public sealed class ChatMessageItem : INotifyPropertyChanged
{
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

    public ChatMessageItem(
        MessageId id,
        string body,
        MessageDirection direction,
        MessageDeliveryState state,
        DateTimeOffset createdAt,
        IReadOnlyList<AttachmentMetadata> attachments,
        MessageReply? replyTo,
        IReadOnlyList<MessageReaction> reactions,
        DateTimeOffset? expiresAt = null,
        SessionId? senderId = null,
        bool isRetryAvailable = false)
    {
        Id = id;
        Body = body;
        Direction = direction;
        State = state;
        CreatedAt = createdAt;
        Attachments = attachments;
        ReplyTo = replyTo;
        Reactions = reactions;
        ExpiresAt = expiresAt;
        SenderId = senderId;
        IsRetryAvailable = isRetryAvailable
            && direction == MessageDirection.Outgoing
            && state == MessageDeliveryState.Failed
            && senderId is not null;
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

    public MessageReply? ReplyTo { get; }

    public IReadOnlyList<MessageReaction> Reactions { get; }

    public DateTimeOffset? ExpiresAt { get; }

    public SessionId? SenderId { get; }

    public bool IsRetryAvailable { get; }

    public bool HasAttachments => Attachments.Count > 0;

    public bool IsOutgoing => Direction == MessageDirection.Outgoing;

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

    public string ImageMetadataDescription => PrimaryImageAttachment is { } image
        ? $"{image.FileName}; {image.ContentType}; {image.SizeBytes}; {image.Width ?? 0}x{image.Height ?? 0}"
        : string.Empty;

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

public sealed class ChatViewModel : ViewModelBase
{
    private const int InitialMessagePageSize = 20;
    private const int HistoryMessagePageSize = 20;
    private readonly ClientRuntime runtime;
    private readonly ICallService callService;
    private readonly IAttachmentPickerService? attachmentPicker;
    private readonly IVoiceMessageRecorder? voiceRecorder;
    private readonly ChatOpenUiCache? openCache;
    private readonly HashSet<MessageId> locallyQueuedMessageIds = [];
    private Conversation? conversation;
    private SessionAccount? account;
    private SessionId? counterpart;
    private DateTimeOffset? oldestLoadedMessageAt;
    private MessageId? oldestLoadedMessageId;
    private bool hasOlderMessages;
    private string draft = string.Empty;
    private CallSessionState? activeCallState;
    private string? activeCallId;
    private bool isMessageRequest;
    private bool isBlocked;
    private bool isRecordingVoice;
    private ChatMessageItem? replyingTo;
    private long messageContextGeneration;

    public ChatViewModel(
        ClientRuntime runtime,
        ICallService? callService = null,
        IAttachmentPickerService? attachmentPicker = null,
        IVoiceMessageRecorder? voiceRecorder = null,
        ChatOpenUiCache? openCache = null)
    {
        this.runtime = runtime;
        this.callService = callService ?? new UnavailableCallService();
        this.attachmentPicker = attachmentPicker;
        this.voiceRecorder = voiceRecorder;
        this.openCache = openCache;
        Messages = new ObservableRangeCollection<ChatMessageItem>();
        StagedAttachments = [];
        StagedAttachments.CollectionChanged += OnStagedAttachmentsChanged;
        SendCommand = new AsyncCommand(SendAsync, CanSend);
        ReceiveCommand = new AsyncCommand(ReceiveAsync, () => account is not null);
        PickAttachmentsCommand = new AsyncCommand(PickAttachmentsAsync, () => attachmentPicker is not null);
        ClearAttachmentsCommand = new AsyncCommand(ClearAttachmentsAsync, () => StagedAttachments.Count > 0);
        AcceptMessageRequestCommand = new AsyncCommand(AcceptMessageRequestAsync, () => counterpart is not null && IsMessageRequest);
        BlockContactCommand = new AsyncCommand(BlockContactAsync, () => counterpart is not null && !IsBlocked);
        CancelReplyCommand = new AsyncCommand(CancelReplyAsync, () => ReplyingTo is not null);
        RetryMessageCommand = new AsyncCommand<ChatMessageItem>(RetryMessageAsync, CanRetryMessage);
    }

    public Conversation? Conversation
    {
        get => conversation;
        private set
        {
            if (SetProperty(ref conversation, value))
            {
                RaisePropertyChanged(nameof(ConversationTitle));
            }
        }
    }

    public SessionId? Counterpart => counterpart;

    public string ConversationTitle =>
        Conversation is null
            ? string.Empty
            : counterpart is { } id
                ? DeepDisplayName.ContactTitleOrFallback(id, Conversation.DisplayName)
                : Conversation.DisplayName;

    public bool IsSelfConversation => account is not null && counterpart == account.SessionId;

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

    public ObservableCollection<ChatMessageItem> Messages { get; }

    public ObservableCollection<AttachmentMetadata> StagedAttachments { get; }

    public bool HasStagedAttachments => StagedAttachments.Count > 0;

    public bool HasComposedContent => !string.IsNullOrWhiteSpace(Draft) || StagedAttachments.Count > 0;

    public bool CanRecordVoice => voiceRecorder?.IsSupported == true
        && account is not null
        && counterpart is not null
        && !IsBlocked;

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

    public bool IsMessageRequest
    {
        get => isMessageRequest;
        private set
        {
            if (SetProperty(ref isMessageRequest, value))
            {
                AcceptMessageRequestCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsBlocked
    {
        get => isBlocked;
        private set
        {
            if (SetProperty(ref isBlocked, value))
            {
                RaisePropertyChanged(nameof(IsComposerEnabled));
                SendCommand.RaiseCanExecuteChanged();
                BlockContactCommand.RaiseCanExecuteChanged();
                RaiseComposerStateChanged();
            }
        }
    }

    public bool IsComposerEnabled => !IsBlocked;

    public ChatMessageItem? ReplyingTo
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

    public string StagedAttachmentSummary => StagedAttachments.Count switch
    {
        0 => "Нет вложений",
        1 => StagedAttachments[0].FileName,
        _ => $"{StagedAttachments.Count} влож."
    };

    public string StagedAttachmentMetadataDescription => StagedAttachments.Count == 1
        ? CanonicalAttachmentMetadataDescription(StagedAttachments[0])
        : string.Empty;

    public CallSessionState? ActiveCallState
    {
        get => activeCallState;
        private set
        {
            if (SetProperty(ref activeCallState, value))
            {
                RaisePropertyChanged(nameof(IsIncomingCall));
                RaisePropertyChanged(nameof(HasCallSession));
                RaisePropertyChanged(nameof(CallActionText));
                RaisePropertyChanged(nameof(CallStatusText));
            }
        }
    }

    public string? ActiveCallId
    {
        get => activeCallId;
        private set
        {
            if (SetProperty(ref activeCallId, value))
            {
                RaisePropertyChanged(nameof(IsIncomingCall));
                RaisePropertyChanged(nameof(HasCallSession));
                RaisePropertyChanged(nameof(CallActionText));
                RaisePropertyChanged(nameof(CallStatusText));
            }
        }
    }

    public bool IsIncomingCall => ActiveCallId is not null && ActiveCallState == CallSessionState.Ringing;

    public bool HasCallSession =>
        ActiveCallId is not null
        && ActiveCallState is not (CallSessionState.Ended or CallSessionState.Failed);

    public string CallActionText => ActiveCallState switch
    {
        CallSessionState.Ringing => "Принять",
        CallSessionState.Signaling or CallSessionState.Connecting or CallSessionState.Connected or CallSessionState.Reconnecting => "Завершить",
        _ => "Звонок"
    };

    public string CallStatusText => ActiveCallState switch
    {
        null => "Ожидание",
        CallSessionState.Ringing => "Входящий звонок",
        CallSessionState.Signaling => "Сигналинг",
        CallSessionState.Connecting => "Подключение",
        CallSessionState.Connected => "Подключено",
        CallSessionState.Reconnecting => "Переподключение",
        CallSessionState.Ended => "Завершено",
        CallSessionState.Failed => "Ошибка",
        _ => ActiveCallState.Value.ToString()
    };

    public AsyncCommand SendCommand { get; }

    public AsyncCommand ReceiveCommand { get; }

    public AsyncCommand PickAttachmentsCommand { get; }

    public AsyncCommand ClearAttachmentsCommand { get; }

    public AsyncCommand AcceptMessageRequestCommand { get; }

    public AsyncCommand BlockContactCommand { get; }

    public AsyncCommand CancelReplyCommand { get; }

    public AsyncCommand<ChatMessageItem> RetryMessageCommand { get; }

    public bool PrepareRoute(SessionId recipient, string? displayName = null)
    {
        Interlocked.Increment(ref messageContextGeneration);
        ResetLocalQueueTrackingIfConversationChanged(ConversationId.ForOneToOne(recipient));
        ReplyingTo = null;
        StagedAttachments.Clear();
        if (openCache?.TryGet(recipient, runtime.Clock.UtcNow, out var cached) == true)
        {
            ApplyUiSnapshot(cached);
            return true;
        }

        account = null;
        counterpart = recipient;
        oldestLoadedMessageAt = null;
        oldestLoadedMessageId = null;
        hasOlderMessages = false;
        Conversation = CreateRouteConversation(recipient, displayName);
        IsBlocked = false;
        IsMessageRequest = false;
        Messages.Clear();

        RaisePropertyChanged(nameof(Counterpart));
        RaisePropertyChanged(nameof(IsSelfConversation));
        SendCommand.RaiseCanExecuteChanged();
        ReceiveCommand.RaiseCanExecuteChanged();
        RaiseComposerStateChanged();
        return false;
    }

    private void ApplyUiSnapshot(ChatOpenUiSnapshot snapshot)
    {
        ResetLocalQueueTrackingIfConversationChanged(snapshot.Conversation.Id);
        account = snapshot.ActiveAccount;
        counterpart = snapshot.Counterpart;
        oldestLoadedMessageAt = snapshot.OldestLoadedMessageAt;
        oldestLoadedMessageId = snapshot.OldestLoadedMessageId;
        hasOlderMessages = snapshot.HasOlderMessages;
        Conversation = snapshot.Conversation;
        IsBlocked = snapshot.IsBlocked;
        IsMessageRequest = snapshot.IsMessageRequest;
        SyncMessageItems(snapshot.Messages, observePersistedIds: false);
        RaisePropertyChanged(nameof(Counterpart));
        RaisePropertyChanged(nameof(IsSelfConversation));
        SendCommand.RaiseCanExecuteChanged();
        ReceiveCommand.RaiseCanExecuteChanged();
        RaiseComposerStateChanged();
        CacheCurrentConversation();
    }

    public async Task OpenOneToOneAsync(SessionAccount activeAccount, SessionId recipient, string? displayName = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref messageContextGeneration);
        var conversationId = ConversationId.ForOneToOne(recipient);
        ResetLocalQueueTrackingIfConversationChanged(conversationId);
        account = activeAccount;
        counterpart = recipient;
        RaisePropertyChanged(nameof(Counterpart));
        RaisePropertyChanged(nameof(IsSelfConversation));
        oldestLoadedMessageAt = null;
        oldestLoadedMessageId = null;
        hasOlderMessages = false;
        if (Conversation?.Id != conversationId)
        {
            Messages.Clear();
        }

        Conversation = CreateRouteConversation(recipient, displayName);
        SendCommand.RaiseCanExecuteChanged();
        ReceiveCommand.RaiseCanExecuteChanged();
        RaiseComposerStateChanged();
        await ReloadMessagesAsync(cancellationToken);

        Conversation = await runtime.Conversations.GetOrCreateOneToOneAsync(
            recipient,
            displayName,
            cancellationToken: cancellationToken);
        var contact = await runtime.Conversations.GetContactAsync(recipient, cancellationToken);
        IsBlocked = contact?.IsBlocked == true;
        IsMessageRequest = recipient != activeAccount.SessionId
            && contact is { IsApproved: false, IsBlocked: false };

        SendCommand.RaiseCanExecuteChanged();
        ReceiveCommand.RaiseCanExecuteChanged();
        RaiseComposerStateChanged();
    }

    public async Task OpenFromRouteAsync(string sessionId, string? displayName = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref messageContextGeneration);
        var recipient = SessionId.Parse(sessionId);
        if (runtime.Store is IOneToOneConversationOpenRepository fastOpenStore)
        {
            var snapshot = await fastOpenStore.OpenOneToOneConversationAsync(
                recipient,
                displayName,
                InitialMessagePageSize,
                runtime.Clock.UtcNow,
                cancellationToken);
            if (snapshot is null)
            {
                throw new InvalidOperationException("Войдите в аккаунт перед открытием чата.");
            }

            ApplyOpenSnapshot(recipient, snapshot);
            return;
        }

        var active = await runtime.Accounts.GetActiveAccountAsync(cancellationToken)
            ?? throw new InvalidOperationException("Войдите в аккаунт перед открытием чата.");

        await OpenOneToOneAsync(active, recipient, displayName, cancellationToken);
    }

    public async Task RefreshConversationMetadataAsync(CancellationToken cancellationToken = default)
    {
        if (account is null || counterpart is null)
        {
            return;
        }

        Conversation = await runtime.Conversations.GetOrCreateOneToOneAsync(
            counterpart.Value,
            cancellationToken: cancellationToken);
        var contact = await runtime.Conversations.GetContactAsync(counterpart.Value, cancellationToken);
        IsBlocked = contact?.IsBlocked == true;
        IsMessageRequest = counterpart.Value != account.SessionId
            && contact is { IsApproved: false, IsBlocked: false };
    }

    public void StageAttachment(AttachmentMetadata attachment) => StagedAttachments.Add(attachment);

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

    public async Task SendAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (account is null || counterpart is null)
            {
                throw new InvalidOperationException("Откройте чат перед отправкой.");
            }

            ErrorMessage = null;
            var body = string.IsNullOrWhiteSpace(Draft)
                ? MessageAttachmentPresentation.GenericAttachmentPlaceholder
                : Draft;
            var attachments = StagedAttachments.ToArray();

            await QueueMessageAsync(body, attachments, cancellationToken);
            Draft = string.Empty;
            StagedAttachments.Clear();
            ReplyingTo = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    public async Task StartVoiceRecordingAsync(CancellationToken cancellationToken = default)
    {
        if (voiceRecorder is null)
        {
            ErrorMessage = "Запись голосовых сообщений недоступна на этом устройстве.";
            return;
        }

        try
        {
            ErrorMessage = null;
            await voiceRecorder.StartAsync(cancellationToken);
            IsRecordingVoice = true;
        }
        catch (Exception ex)
        {
            IsRecordingVoice = false;
            ErrorMessage = ex.Message;
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
                return;
            }

            await QueueMessageAsync(
                MessageAttachmentPresentation.VoiceAttachmentPlaceholder,
                [attachment],
                cancellationToken);
        }
        catch (Exception ex)
        {
            await CancelVoiceRecordingAsync(cancellationToken);
            ErrorMessage = ex.Message;
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
    }

    private async Task QueueMessageAsync(
        string body,
        IReadOnlyList<AttachmentMetadata> attachments,
        CancellationToken cancellationToken)
    {
        if (account is null || counterpart is null)
        {
            throw new InvalidOperationException("Откройте чат перед отправкой.");
        }

        var messageId = MessageId.NewId();
        var createdAt = runtime.Clock.UtcNow;
        var reply = ReplyingTo is null
            ? null
            : new MessageReply(
                ReplyingTo.Id,
                ReplyingTo.Direction == MessageDirection.Outgoing ? account.SessionId : counterpart.Value,
                ReplyingTo.Body);
        var optimistic = new Message(
            messageId,
            ConversationId.ForOneToOne(counterpart.Value),
            account.SessionId,
            counterpart.Value,
            body.Trim(),
            MessageDirection.Outgoing,
            MessageDeliveryState.Sending,
            createdAt,
            attachments,
            ReplyTo: reply);
        locallyQueuedMessageIds.Add(messageId);
        UpsertMessageItem(optimistic);

        try
        {
            var pending = await runtime.Messages.QueueOneToOneAsync(
                account.SessionId,
                counterpart.Value,
                body,
                attachments,
                ReplyingTo?.Id,
                messageId,
                createdAt,
                cancellationToken);

            IsMessageRequest = false;
            ReplaceMessageItem(pending);
            CacheCurrentConversation();
            ReplyingTo = null;
            _ = DispatchPendingMessageAsync(pending);
        }
        catch
        {
            locallyQueuedMessageIds.Remove(messageId);
            RemoveMessageItem(messageId);
            throw;
        }
    }

    public void BeginReply(ChatMessageItem message) => ReplyingTo = message;

    public Task CancelReplyAsync(CancellationToken cancellationToken = default)
    {
        ReplyingTo = null;
        return Task.CompletedTask;
    }

    public Task ClearConversationAsync(CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            if (Conversation is null)
            {
                return;
            }

            await runtime.Messages.ClearConversationMessagesAsync(Conversation.Id, ct);
            Messages.Clear();
            locallyQueuedMessageIds.Clear();
            oldestLoadedMessageAt = null;
            oldestLoadedMessageId = null;
            hasOlderMessages = false;
            CacheCurrentConversation();
        }, cancellationToken);

    public Task DeleteConversationAsync(CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            if (Conversation is null)
            {
                return;
            }

            await runtime.Messages.ClearConversationMessagesAsync(Conversation.Id, ct);
            await runtime.Conversations.SetConversationHiddenAsync(Conversation.Id, true, ct);
            Messages.Clear();
            locallyQueuedMessageIds.Clear();
            oldestLoadedMessageAt = null;
            oldestLoadedMessageId = null;
            hasOlderMessages = false;
            if (counterpart is { } currentCounterpart)
            {
                openCache?.Remove(currentCounterpart);
            }
        }, cancellationToken);

    public Task DeleteMessageAsync(ChatMessageItem message, CancellationToken cancellationToken = default) =>
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

    public Task ToggleReactionAsync(ChatMessageItem message, string emoji, CancellationToken cancellationToken = default) =>
        ToggleReactionAsync(message.Id, emoji, cancellationToken);

    public Task ToggleReactionAsync(MessageId messageId, string emoji, CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            if (account is null || counterpart is null)
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
            var updated = await runtime.Messages.SendReactionOneToOneAsync(
                account.SessionId,
                counterpart.Value,
                message.Id,
                emoji,
                remove,
                ct);
            if (updated is not null)
            {
                ReplaceMessageItem(updated);
            }
        }, cancellationToken);

    public Task AcceptMessageRequestAsync(CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            if (counterpart is null)
            {
                return;
            }

            await runtime.Conversations.ApproveContactAsync(counterpart.Value, ct);
            IsBlocked = false;
            IsMessageRequest = false;
        }, cancellationToken);

    public Task BlockContactAsync(CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            if (counterpart is null)
            {
                return;
            }

            await runtime.Conversations.SetContactBlockedAsync(counterpart.Value, true, ct);
            IsMessageRequest = false;
            IsBlocked = true;
        }, cancellationToken);

    private async Task DispatchPendingMessageAsync(Message pending)
    {
        try
        {
            var sent = await runtime.Messages.DispatchOneToOneAsync(pending);
            ReplaceMessageItem(sent);
        }
        catch (Exception ex)
        {
            ReplaceMessageItem(pending.Mark(MessageDeliveryState.Failed));
            ErrorMessage = $"Не удалось отправить сообщение: {ex.Message}";
        }
    }

    public async Task RetryMessageAsync(
        ChatMessageItem message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!CanRetryMessage(message))
        {
            return;
        }

        var activeAccount = await runtime.Accounts.GetActiveAccountAsync(cancellationToken);
        var stored = await ((IMessageRepository)runtime.Store).GetAsync(message.Id, cancellationToken);
        if (activeAccount is null
            || account?.SessionId != activeAccount.SessionId
            || stored is null
            || stored.Sender != activeAccount.SessionId
            || Conversation is null
            || stored.ConversationId != Conversation.Id
            || stored.Recipient != counterpart
            || stored.Direction != MessageDirection.Outgoing
            || stored.DeliveryState != MessageDeliveryState.Failed)
        {
            return;
        }

        ErrorMessage = null;
        var retryContext = new RetryContext(
            Volatile.Read(ref messageContextGeneration),
            activeAccount.SessionId,
            stored.ConversationId,
            stored.Recipient);
        ReplaceMessageItem(stored.Mark(MessageDeliveryState.Sending));
        RetryMessageCommand.RaiseCanExecuteChanged();
        try
        {
            var result = await runtime.Messages.RetryOutgoingAsync(
                activeAccount.SessionId,
                stored.Id,
                cancellationToken);
            if (await IsRetryContextCurrentAsync(retryContext))
            {
                ReplaceMessageItem(result);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var restore = await RestoreAuthoritativeRetryStateAsync(
                retryContext,
                stored,
                allowFailed: false);
            if (restore == RetryRestoreOutcome.Pending
                && await IsRetryContextCurrentAsync(retryContext))
            {
                ErrorMessage = "Статус повторной отправки уточняется.";
            }
        }
        catch
        {
            var restore = await RestoreAuthoritativeRetryStateAsync(
                retryContext,
                stored,
                allowFailed: true);
            if (await IsRetryContextCurrentAsync(retryContext))
            {
                ErrorMessage = restore == RetryRestoreOutcome.Applied
                    ? "Не удалось повторно отправить сообщение."
                    : "Статус повторной отправки уточняется.";
            }
        }
        finally
        {
            RetryMessageCommand.RaiseCanExecuteChanged();
        }
    }

    private bool CanRetryMessage(ChatMessageItem message) =>
        account is not null
        && message.IsRetryAvailable
        && message.Direction == MessageDirection.Outgoing
        && message.State == MessageDeliveryState.Failed
        && message.SenderId == account.SessionId
        && Messages.Any(current =>
            current.Id == message.Id
            && current.State == MessageDeliveryState.Failed
            && current.SenderId == account.SessionId);

    private async Task<RetryRestoreOutcome> RestoreAuthoritativeRetryStateAsync(
        RetryContext context,
        Message original,
        bool allowFailed)
    {
        Message? stored;
        try
        {
            stored = await ((IMessageRepository)runtime.Store).GetAsync(original.Id, CancellationToken.None);
        }
        catch
        {
            stored = null;
        }

        if (!await IsRetryContextCurrentAsync(context))
        {
            return RetryRestoreOutcome.ContextChanged;
        }

        if (IsMatchingRetryMessage(stored, context)
            && (allowFailed || stored!.DeliveryState != MessageDeliveryState.Failed))
        {
            ReplaceMessageItem(stored!);
            return RetryRestoreOutcome.Applied;
        }

        ScheduleRetryReconciliation(context, original.Id, allowFailed);
        return RetryRestoreOutcome.Pending;
    }

    private void ScheduleRetryReconciliation(RetryContext context, MessageId messageId, bool allowFailed) =>
        _ = ReconcileRetryStateAsync(context, messageId, allowFailed);

    private async Task ReconcileRetryStateAsync(
        RetryContext context,
        MessageId messageId,
        bool allowFailed)
    {
        foreach (var delay in new[] { 250, 500, 1_000, 2_000, 4_000 })
        {
            await Task.Delay(delay);
            if (!await IsRetryContextCurrentAsync(context))
            {
                return;
            }

            Message? stored;
            try
            {
                stored = await ((IMessageRepository)runtime.Store).GetAsync(messageId, CancellationToken.None);
            }
            catch
            {
                continue;
            }

            if (!IsMatchingRetryMessage(stored, context)
                || (!allowFailed && stored!.DeliveryState == MessageDeliveryState.Failed))
            {
                continue;
            }

            if (!await IsRetryContextCurrentAsync(context))
            {
                return;
            }

            ReplaceMessageItem(stored!);
            ErrorMessage = stored!.DeliveryState == MessageDeliveryState.Failed
                ? "Не удалось повторно отправить сообщение."
                : null;
            return;
        }
    }

    private async Task<bool> IsRetryContextCurrentAsync(RetryContext context)
    {
        if (!IsRetryContextCurrent(context))
        {
            return false;
        }

        try
        {
            var active = await runtime.Accounts.GetActiveAccountAsync(CancellationToken.None);
            return IsRetryContextCurrent(context) && active?.SessionId == context.AccountId;
        }
        catch
        {
            return false;
        }
    }

    private bool IsRetryContextCurrent(RetryContext context) =>
        Volatile.Read(ref messageContextGeneration) == context.Generation
        && account?.SessionId == context.AccountId
        && Conversation?.Id == context.ConversationId
        && counterpart == context.Recipient;

    private static bool IsMatchingRetryMessage(Message? message, RetryContext context) =>
        message is not null
        && message.Sender == context.AccountId
        && message.ConversationId == context.ConversationId
        && message.Recipient == context.Recipient
        && message.Direction == MessageDirection.Outgoing;

    private readonly record struct RetryContext(
        long Generation,
        SessionId AccountId,
        ConversationId ConversationId,
        SessionId? Recipient);

    private enum RetryRestoreOutcome
    {
        Applied,
        Pending,
        ContextChanged
    }

    public Task ReceiveAsync(CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            if (account is null || Conversation is null)
            {
                throw new InvalidOperationException("Откройте чат перед получением сообщений.");
            }

            var activeConversation = Conversation;
            var hadLoadedMessages = Messages.Count > 0;
            var loadedMessageIds = hadLoadedMessages
                ? Messages.Select(static message => message.Id).ToHashSet()
                : [];
            _ = await runtime.Messages.ReceiveAsync(account.SessionId, ct);
            var persistedMessages = await runtime.Messages.ListRecentConversationMessagesAsync(
                activeConversation.Id,
                InitialMessagePageSize,
                ct);
            var readAt = await runtime.Messages.MarkConversationAsReadAsync(
                activeConversation.Id,
                runtime.Clock.UtcNow,
                ct);
            var reconciledMessages = persistedMessages
                .OrderBy(message => message.CreatedAt)
                .ThenBy(message => message.Id.Value, StringComparer.Ordinal)
                .Select(message => MarkIncomingRead(message, readAt))
                .ToArray();
            var replaceRecentWindow = hadLoadedMessages
                && reconciledMessages.Length == InitialMessagePageSize
                && !reconciledMessages.Any(message => loadedMessageIds.Contains(message.Id));
            if (replaceRecentWindow)
            {
                SyncMessageItems(reconciledMessages.Select(ToItem).ToArray());
            }
            else
            {
                foreach (var message in reconciledMessages)
                {
                    locallyQueuedMessageIds.Remove(message.Id);
                    UpsertMessageItem(message);
                }
            }

            if ((!hadLoadedMessages || replaceRecentWindow) && reconciledMessages.Length > 0)
            {
                oldestLoadedMessageAt = reconciledMessages[0].CreatedAt;
                oldestLoadedMessageId = reconciledMessages[0].Id;
                hasOlderMessages = reconciledMessages.Length == InitialMessagePageSize;
            }
        }, cancellationToken);

    public Task LoadOlderMessagesAsync(CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            if (Conversation is null || !hasOlderMessages || oldestLoadedMessageAt is null || oldestLoadedMessageId is null)
            {
                return;
            }

            var messages = await runtime.Messages.ListConversationMessagesBeforeAsync(
                Conversation.Id,
                oldestLoadedMessageAt.Value,
                oldestLoadedMessageId.Value,
                HistoryMessagePageSize,
                ct);
            if (messages.Count == 0)
            {
                hasOlderMessages = false;
                return;
            }

            var readCursor = await runtime.Messages.GetReadCursorAsync(Conversation.Id, ct);
            var items = new List<ChatMessageItem>(messages.Count);
            foreach (var message in messages)
            {
                items.Add(ToItem(ApplyReadCursor(message, readCursor)));
            }

            PrependMessageItems(items);
            oldestLoadedMessageAt = messages[0].CreatedAt;
            oldestLoadedMessageId = messages[0].Id;
            hasOlderMessages = messages.Count == HistoryMessagePageSize;
        }, cancellationToken);

    public async Task<CallSessionSnapshot?> StartCallAsync(CancellationToken cancellationToken = default)
    {
        CallSessionSnapshot? started = null;
        await RunBusyAsync(async ct =>
        {
            if (account is null || counterpart is null || Conversation is null)
            {
                throw new InvalidOperationException("Откройте чат перед началом звонка.");
            }

            if (!callService.IsAvailable)
            {
                throw new InvalidOperationException("Сервис звонков сейчас недоступен.");
            }

            if (ActiveCallId is not null && ActiveCallState is not (CallSessionState.Ended or CallSessionState.Failed))
            {
                throw new InvalidOperationException("В этом чате уже есть активный звонок.");
            }

            started = await callService
                .StartAsync(account.SessionId, counterpart.Value, Conversation.Id.Value, ct);

            ActiveCallId = started.CallId;
            ActiveCallState = started.State;
        }, cancellationToken);

        return started;
    }

    public async Task<CallSessionSnapshot?> EndCallAsync(CancellationToken cancellationToken = default)
    {
        CallSessionSnapshot? ended = null;
        await RunBusyAsync(async ct =>
        {
            if (account is null)
            {
                throw new InvalidOperationException("Войдите в аккаунт перед завершением звонка.");
            }

            if (ActiveCallId is null)
            {
                throw new InvalidOperationException("Нет активного звонка для завершения.");
            }

            ended = await callService.EndAsync(ActiveCallId, account.SessionId, cancellationToken: ct);

            if (ended is not null)
            {
                ActiveCallState = ended.State;
            }
        }, cancellationToken);

        return ended;
    }

    public async Task<CallSessionSnapshot?> PollCallAsync(CancellationToken cancellationToken = default)
    {
        CallSessionSnapshot? updated = null;
        await RunBusyAsync(async ct =>
        {
            if (account is null || Conversation is null)
            {
                throw new InvalidOperationException("Откройте чат перед обновлением звонков.");
            }

            if (!callService.IsAvailable)
            {
                throw new InvalidOperationException("Сервис звонков сейчас недоступен.");
            }

            var snapshots = await callService.PollAsync(account.SessionId, ct);
            if (snapshots.Count == 0)
            {
                return;
            }

            updated = snapshots
                .LastOrDefault(snapshot =>
                    string.Equals(snapshot.ConversationId, Conversation.Id.Value, StringComparison.Ordinal)
                    || (counterpart is { } remoteParty && snapshot.RemoteParty == remoteParty));

            if (updated is null)
            {
                return;
            }

            ActiveCallId = updated.CallId;
            ActiveCallState = updated.State;
        }, cancellationToken);

        return updated;
    }

    public async Task<CallSessionSnapshot?> AcceptIncomingCallAsync(CancellationToken cancellationToken = default)
    {
        CallSessionSnapshot? accepted = null;
        await RunBusyAsync(async ct =>
        {
            if (account is null)
            {
                throw new InvalidOperationException("Войдите в аккаунт перед принятием звонка.");
            }

            if (ActiveCallId is null || ActiveCallState != CallSessionState.Ringing)
            {
                throw new InvalidOperationException("Нет входящего звонка для принятия.");
            }

            accepted = await callService.AcceptAsync(ActiveCallId, account.SessionId, ct);
            if (accepted is not null)
            {
                ActiveCallState = accepted.State;
            }
        }, cancellationToken);

        return accepted;
    }

    public async Task<CallSessionSnapshot?> DeclineIncomingCallAsync(CancellationToken cancellationToken = default)
    {
        CallSessionSnapshot? declined = null;
        await RunBusyAsync(async ct =>
        {
            if (account is null)
            {
                throw new InvalidOperationException("Войдите в аккаунт перед отклонением звонка.");
            }

            if (ActiveCallId is null || ActiveCallState != CallSessionState.Ringing)
            {
                throw new InvalidOperationException("Нет входящего звонка для отклонения.");
            }

            declined = await callService
                .EndAsync(ActiveCallId, account.SessionId, reason: "local-decline", cancellationToken: ct);

            if (declined is not null)
            {
                ActiveCallState = declined.State;
            }
        }, cancellationToken);

        return declined;
    }

    public async Task<CallSessionSnapshot?> ApplyCallNetworkSampleAsync(
        CallNetworkSample sample,
        CancellationToken cancellationToken = default)
    {
        CallSessionSnapshot? updated = null;
        await RunBusyAsync(async ct =>
        {
            if (account is null)
            {
                throw new InvalidOperationException("Войдите в аккаунт перед отправкой диагностики звонка.");
            }

            if (ActiveCallId is null)
            {
                throw new InvalidOperationException("Нет активного звонка для обновления диагностики.");
            }

            updated = await callService
                .ApplyNetworkSampleAsync(ActiveCallId, account.SessionId, sample, ct);

            if (updated is not null)
            {
                ActiveCallState = updated.State;
            }
        }, cancellationToken);

        return updated;
    }

    private void ApplyOpenSnapshot(SessionId recipient, OneToOneConversationOpenSnapshot snapshot)
    {
        ResetLocalQueueTrackingIfConversationChanged(snapshot.Conversation.Id);
        account = snapshot.ActiveAccount;
        counterpart = recipient;
        oldestLoadedMessageAt = null;
        oldestLoadedMessageId = null;
        hasOlderMessages = false;
        Conversation = snapshot.Conversation;
        IsBlocked = snapshot.Contact?.IsBlocked == true;
        IsMessageRequest = counterpart != snapshot.ActiveAccount.SessionId
            && snapshot.Contact is { IsApproved: false, IsBlocked: false };

        var items = new List<ChatMessageItem>(snapshot.RecentMessages.Count);
        foreach (var message in snapshot.RecentMessages)
        {
            items.Add(ToItem(ApplyReadCursor(message, snapshot.ReadAt)));
        }

        SyncMessageItems(items);
        oldestLoadedMessageAt = snapshot.RecentMessages.Count == 0 ? null : snapshot.RecentMessages[0].CreatedAt;
        oldestLoadedMessageId = snapshot.RecentMessages.Count == 0 ? null : snapshot.RecentMessages[0].Id;
        hasOlderMessages = snapshot.RecentMessages.Count == InitialMessagePageSize;
        RaisePropertyChanged(nameof(Counterpart));
        RaisePropertyChanged(nameof(IsSelfConversation));
        SendCommand.RaiseCanExecuteChanged();
        ReceiveCommand.RaiseCanExecuteChanged();
        RaiseComposerStateChanged();
        CacheCurrentConversation();
    }

    private async Task ReloadMessagesAsync(CancellationToken cancellationToken)
    {
        if (Conversation is null)
        {
            return;
        }

        var messages = await runtime.Messages.ListRecentConversationMessagesAsync(
            Conversation.Id,
            InitialMessagePageSize,
            cancellationToken);
        oldestLoadedMessageAt = messages.Count == 0 ? null : messages[0].CreatedAt;
        oldestLoadedMessageId = messages.Count == 0 ? null : messages[0].Id;
        hasOlderMessages = messages.Count == InitialMessagePageSize;

        var readAt = await runtime.Messages.MarkConversationAsReadAsync(
            Conversation.Id,
            runtime.Clock.UtcNow,
            cancellationToken);
        var items = new List<ChatMessageItem>(messages.Count);
        foreach (var message in messages)
        {
            items.Add(ToItem(MarkIncomingRead(message, readAt)));
        }

        SyncMessageItems(items);
        CacheCurrentConversation();
    }

    private Conversation CreateRouteConversation(SessionId recipient, string? displayName)
    {
        var now = runtime.Clock.UtcNow;
        return new Conversation(
            ConversationId.ForOneToOne(recipient),
            ConversationKind.OneToOne,
            string.IsNullOrWhiteSpace(displayName) ? recipient.Value : displayName.Trim(),
            ConversationSettings.Default(ConversationKind.OneToOne),
            now,
            now);
    }

    private ChatMessageItem ToItem(Message message) =>
        new(
            message.Id,
            message.Body,
            message.Direction,
            message.DeliveryState,
            message.CreatedAt,
            message.Attachments,
            message.ReplyTo,
            message.ReactionItems,
            message.ExpiresAt,
            message.Sender,
            message.Direction == MessageDirection.Outgoing
                && message.DeliveryState == MessageDeliveryState.Failed
                && account?.SessionId == message.Sender);

    private void ReplaceMessageItem(Message message)
    {
        for (var index = 0; index < Messages.Count; index++)
        {
            if (Messages[index].Id == message.Id)
            {
                var candidate = ToItem(message);
                if (!MessageDeliveryProgress.IsRegression(Messages[index].State, candidate.State))
                {
                    Messages[index] = candidate;
                }

                CacheCurrentConversation();
                RetryMessageCommand.RaiseCanExecuteChanged();
                return;
            }
        }

        if (Conversation?.Id == message.ConversationId
            && account?.SessionId == message.Sender)
        {
            UpsertMessageItem(message);
        }
    }

    private void UpsertMessageItem(Message message)
    {
        var item = ToItem(message);
        for (var index = 0; index < Messages.Count; index++)
        {
            if (Messages[index].Id == item.Id)
            {
                if (!MessageDeliveryProgress.IsRegression(Messages[index].State, item.State)
                    && !SameMessageItem(Messages[index], item))
                {
                    Messages[index] = item;
                    CacheCurrentConversation();
                }

                return;
            }

            if (Messages[index].CreatedAt > item.CreatedAt)
            {
                Messages.Insert(index, item);
                CacheCurrentConversation();
                return;
            }
        }

        Messages.Add(item);
        CacheCurrentConversation();
    }

    private void RemoveMessageItem(MessageId messageId)
    {
        locallyQueuedMessageIds.Remove(messageId);
        for (var index = 0; index < Messages.Count; index++)
        {
            if (Messages[index].Id == messageId)
            {
                Messages.RemoveAt(index);
                CacheCurrentConversation();
                return;
            }
        }
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

    private void SyncMessageItems(
        IReadOnlyList<ChatMessageItem> items,
        bool observePersistedIds = true)
    {
        if (observePersistedIds)
        {
            foreach (var item in items)
            {
                locallyQueuedMessageIds.Remove(item.Id);
            }
        }

        items = MessageDeliveryProgress.MergeSnapshot(
            Messages,
            items,
            locallyQueuedMessageIds,
            static item => item.Id,
            static item => item.State,
            static item => item.CreatedAt);

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

    private ObservableRangeCollection<ChatMessageItem> MessageItems => (ObservableRangeCollection<ChatMessageItem>)Messages;

    private void ResetLocalQueueTrackingIfConversationChanged(ConversationId conversationId)
    {
        if (Conversation?.Id != conversationId)
        {
            locallyQueuedMessageIds.Clear();
        }
    }

    private void PrependMessageItems(IReadOnlyList<ChatMessageItem> items)
    {
        MessageItems.InsertRange(0, items);
        CacheCurrentConversation();
    }

    private void CacheCurrentConversation()
    {
        if (openCache is null || account is null || counterpart is null || Conversation is null)
        {
            return;
        }

        var cachedMessages = Messages.Count <= InitialMessagePageSize
            ? Messages.ToArray()
            : Messages.Skip(Messages.Count - InitialMessagePageSize).ToArray();
        DateTimeOffset? cachedOldestMessageAt = cachedMessages.Length == 0
            ? null
            : cachedMessages[0].CreatedAt;
        MessageId? cachedOldestMessageId = cachedMessages.Length == 0
            ? null
            : cachedMessages[0].Id;

        openCache.Store(new ChatOpenUiSnapshot(
            account,
            counterpart.Value,
            Conversation,
            IsBlocked,
            IsMessageRequest,
            cachedMessages,
            cachedOldestMessageAt,
            cachedOldestMessageId,
            hasOlderMessages || Messages.Count > cachedMessages.Length,
            runtime.Clock.UtcNow));
    }

    private bool HasSamePrefix(IReadOnlyList<ChatMessageItem> items)
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

    private bool HasSameSuffix(IReadOnlyList<ChatMessageItem> items, out int offset)
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

    private static bool SameMessageItem(ChatMessageItem left, ChatMessageItem right) =>
        left.Id == right.Id
        && left.Body == right.Body
        && left.Direction == right.Direction
        && left.State == right.State
        && left.SenderId == right.SenderId
        && left.IsRetryAvailable == right.IsRetryAvailable
        && left.CreatedAt == right.CreatedAt
        && left.Attachments.SequenceEqual(right.Attachments)
        && Equals(left.ReplyTo, right.ReplyTo)
        && left.Reactions.SequenceEqual(right.Reactions);

    private bool CanSend() =>
        account is not null
        && counterpart is not null
        && !IsBlocked
        && (!string.IsNullOrWhiteSpace(Draft) || StagedAttachments.Count > 0);

    private void OnStagedAttachmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RaisePropertyChanged(nameof(HasStagedAttachments));
        RaisePropertyChanged(nameof(StagedAttachmentSummary));
        RaisePropertyChanged(nameof(StagedAttachmentMetadataDescription));
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

    private static string CanonicalAttachmentMetadataDescription(AttachmentMetadata attachment) =>
        $"{attachment.FileName}; {attachment.ContentType}; {attachment.SizeBytes}; {attachment.Width ?? 0}x{attachment.Height ?? 0}";

    private sealed class UnavailableCallService : ICallService
    {
        public bool IsAvailable => false;

        public Task<CallSessionSnapshot> StartAsync(SessionId local, SessionId remote, string conversationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Звонки недоступны.");

        public Task<IReadOnlyList<CallSessionSnapshot>> PollAsync(SessionId local, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CallSessionSnapshot>>([]);

        public Task<CallSessionSnapshot?> AcceptAsync(string callId, SessionId local, CancellationToken cancellationToken = default) =>
            Task.FromResult<CallSessionSnapshot?>(null);

        public Task<CallSessionSnapshot?> EndAsync(string callId, SessionId local, string reason = "local-hangup", CancellationToken cancellationToken = default) =>
            Task.FromResult<CallSessionSnapshot?>(null);

        public Task<CallSessionSnapshot?> ApplyNetworkSampleAsync(string callId, SessionId local, CallNetworkSample sample, CancellationToken cancellationToken = default) =>
            Task.FromResult<CallSessionSnapshot?>(null);
    }
}
