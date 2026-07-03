using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Deep.Client.Maui.Core.Commands;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Platform;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.Core.ViewModels;

public sealed record MessageReactionChip(string Emoji, int Count);

public sealed record ChatMessageItem(
    MessageId Id,
    string Body,
    MessageDirection Direction,
    MessageDeliveryState State,
    DateTimeOffset CreatedAt,
    IReadOnlyList<AttachmentMetadata> Attachments,
    MessageReply? ReplyTo,
    IReadOnlyList<MessageReaction> Reactions)
{
    public bool HasAttachments => Attachments.Count > 0;

    public bool IsOutgoing => Direction == MessageDirection.Outgoing;

    public bool IsStatusVisible => MessageStatusPresentation.IsVisible(Direction, State);

    public bool IsReadStatus => State == MessageDeliveryState.Read;

    public bool IsFailedStatus => State == MessageDeliveryState.Failed;

    public string StatusGlyph => MessageStatusPresentation.Glyph(State);

    public string StatusDescription => MessageStatusPresentation.Description(State);

    public bool HasReply => ReplyTo is not null;

    public string ReplyPreview => ReplyTo?.Body ?? string.Empty;

    public IReadOnlyList<MessageReactionChip> ReactionChips => Reactions
        .GroupBy(static reaction => reaction.Emoji, StringComparer.Ordinal)
        .Select(static group => new MessageReactionChip(group.Key, group.Count()))
        .ToArray();

    public bool HasReactions => Reactions.Count > 0;

    public string AttachmentSummary => Attachments.Count switch
    {
        0 => string.Empty,
        1 => Attachments[0].FileName,
        _ => $"{Attachments.Count} влож."
    };
}

public sealed class ChatViewModel : ViewModelBase
{
    private const int InitialMessagePageSize = 100;
    private readonly ClientRuntime runtime;
    private readonly ICallService callService;
    private readonly IAttachmentPickerService? attachmentPicker;
    private Conversation? conversation;
    private SessionAccount? account;
    private SessionId? counterpart;
    private DateTimeOffset? oldestLoadedMessageAt;
    private bool hasOlderMessages;
    private string draft = string.Empty;
    private CallSessionState? activeCallState;
    private string? activeCallId;
    private bool isMessageRequest;
    private bool isBlocked;
    private ChatMessageItem? replyingTo;

    public ChatViewModel(ClientRuntime runtime, ICallService? callService = null, IAttachmentPickerService? attachmentPicker = null)
    {
        this.runtime = runtime;
        this.callService = callService ?? new UnavailableCallService();
        this.attachmentPicker = attachmentPicker;
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
    }

    public Conversation? Conversation
    {
        get => conversation;
        private set => SetProperty(ref conversation, value);
    }

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

    public ObservableCollection<ChatMessageItem> Messages { get; }

    public ObservableCollection<AttachmentMetadata> StagedAttachments { get; }

    public bool HasStagedAttachments => StagedAttachments.Count > 0;

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

    public async Task OpenOneToOneAsync(SessionAccount activeAccount, SessionId recipient, string? displayName = null, CancellationToken cancellationToken = default)
    {
        account = activeAccount;
        counterpart = recipient;
        oldestLoadedMessageAt = null;
        hasOlderMessages = false;
        Conversation = await runtime.Conversations.GetOrCreateOneToOneAsync(
            recipient,
            displayName,
            cancellationToken: cancellationToken);
        var contact = await runtime.Conversations.GetContactAsync(recipient, cancellationToken);
        IsBlocked = contact?.IsBlocked == true;
        IsMessageRequest = recipient != activeAccount.SessionId
            && contact is { IsApproved: false, IsBlocked: false };
        if (recipient == activeAccount.SessionId)
        {
            await runtime.Messages.RepairSelfConversationAsync(activeAccount.SessionId, cancellationToken);
        }

        SendCommand.RaiseCanExecuteChanged();
        ReceiveCommand.RaiseCanExecuteChanged();
        await ReloadMessagesAsync(cancellationToken);
    }

    public async Task OpenFromRouteAsync(string sessionId, string? displayName = null, CancellationToken cancellationToken = default)
    {
        var active = await runtime.Accounts.GetActiveAccountAsync(cancellationToken)
            ?? throw new InvalidOperationException("Войдите в аккаунт перед открытием чата.");

        await OpenOneToOneAsync(active, SessionId.Parse(sessionId), displayName, cancellationToken);
    }

    public void StageAttachment(AttachmentMetadata attachment) => StagedAttachments.Add(attachment);

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
                ? "[Вложение]"
                : Draft;
            var attachments = StagedAttachments.ToArray();

            var pending = await runtime.Messages.QueueOneToOneAsync(
                account.SessionId,
                counterpart.Value,
                body,
                attachments,
                ReplyingTo?.Id,
                cancellationToken);

            IsMessageRequest = false;

            Messages.Add(ToItem(pending));
            Draft = string.Empty;
            StagedAttachments.Clear();
            ReplyingTo = null;
            _ = DispatchPendingMessageAsync(pending);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
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
            oldestLoadedMessageAt = null;
            hasOlderMessages = false;
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
            oldestLoadedMessageAt = null;
            hasOlderMessages = false;
        }, cancellationToken);

    public Task ToggleReactionAsync(ChatMessageItem message, string emoji, CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            if (account is null || counterpart is null)
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

    public Task ReceiveAsync(CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            if (account is null)
            {
                throw new InvalidOperationException("Откройте чат перед получением сообщений.");
            }

            var received = await runtime.Messages.ReceiveAsync(account.SessionId, ct);
            if (received.Count > 0)
            {
                await ReloadMessagesAsync(ct);
            }
        }, cancellationToken);

    public Task LoadOlderMessagesAsync(CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            if (Conversation is null || !hasOlderMessages || oldestLoadedMessageAt is null)
            {
                return;
            }

            var messages = await runtime.Messages.ListConversationMessagesBeforeAsync(
                Conversation.Id,
                oldestLoadedMessageAt.Value,
                InitialMessagePageSize,
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
            hasOlderMessages = messages.Count == InitialMessagePageSize;
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
        var readAt = await runtime.Messages.MarkConversationAsReadAsync(
            Conversation.Id,
            LatestIncomingOrNow(messages),
            cancellationToken);
        var items = new List<ChatMessageItem>(messages.Count);
        foreach (var message in messages)
        {
            items.Add(ToItem(ApplyReadCursor(message, readAt)));
        }

        SyncMessageItems(items);
        oldestLoadedMessageAt = messages.Count == 0 ? null : messages[0].CreatedAt;
        hasOlderMessages = messages.Count == InitialMessagePageSize;
    }

    private static ChatMessageItem ToItem(Message message) =>
        new(
            message.Id,
            message.Body,
            message.Direction,
            message.DeliveryState,
            message.CreatedAt,
            message.Attachments,
            message.ReplyTo,
            message.ReactionItems);

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

    private void SyncMessageItems(IReadOnlyList<ChatMessageItem> items)
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

    private ObservableRangeCollection<ChatMessageItem> MessageItems => (ObservableRangeCollection<ChatMessageItem>)Messages;

    private void PrependMessageItems(IReadOnlyList<ChatMessageItem> items)
    {
        for (var index = items.Count - 1; index >= 0; index--)
        {
            Messages.Insert(0, items[index]);
        }
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

    private static bool SameMessageItem(ChatMessageItem left, ChatMessageItem right) =>
        left.Id == right.Id
        && left.Body == right.Body
        && left.Direction == right.Direction
        && left.State == right.State
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
        SendCommand.RaiseCanExecuteChanged();
        ClearAttachmentsCommand.RaiseCanExecuteChanged();
    }

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
