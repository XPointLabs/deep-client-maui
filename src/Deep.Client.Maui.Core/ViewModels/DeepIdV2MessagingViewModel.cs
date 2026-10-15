using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Deep.Client.Maui.Core.Commands;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Protocol.Identity;

namespace Deep.Client.Maui.Core.ViewModels;

public sealed class DeepIdV2MessagingViewModel : ViewModelBase
{
    private readonly IDeepIdV2AccountRuntimeAccessor accounts;
    private readonly DeepIdV2AccountViewModel account;
    private readonly IDeepIdV2ConversationRuntime? runtime;
    private readonly DeepIdV2NetworkReconnect? reconnect;
    private readonly Action<Exception>? failureObserver;
    private readonly Dictionary<string, byte[]> contactIntents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> acceptanceOperations = new(StringComparer.Ordinal);
    private string contactAddress = string.Empty, draftText = string.Empty, status = string.Empty;
    private DeepIdV2ConversationSnapshot? selected;
    private int accountGeneration;
    private CancellationTokenSource? activeOperation;
    private bool activeRequiresNetwork;
    private string? projectedAccount;
    private readonly DeepIdV2TextComposerState textComposer = new();

    public DeepIdV2MessagingViewModel(IDeepIdV2AccountRuntimeAccessor accounts,
        DeepIdV2AccountViewModel account, IDeepIdV2ConversationRuntime? runtime = null,
        DeepIdV2NetworkReconnect? reconnect = null, Action<Exception>? failureObserver = null)
    {
        this.accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        this.account = account ?? throw new ArgumentNullException(nameof(account)); this.runtime = runtime;
        this.reconnect = reconnect;
        this.failureObserver = failureObserver;
        projectedAccount = account.Account?.PermanentId.CanonicalText;
        RefreshCommand = new(RefreshAsync, () => CanReadLocal && !IsBusy);
        StartContactCommand = new(StartContactAsync, () => CanReadLocal && !IsBusy && !string.IsNullOrWhiteSpace(ContactAddress));
        StopContactRetryCommand = new(StopContactRetryAsync, () => IsReady && !IsBusy && HasContactRetry);
        AcceptContactCommand = new(AcceptContactAsync, () => IsReady && !IsBusy && CanAccept);
        SendTextCommand = new(SendTextAsync, () => IsReady && !IsBusy && CanSend && !string.IsNullOrWhiteSpace(DraftText));
        PropertyChanged += (_, change) => { if (change.PropertyName == nameof(IsBusy)) NotifyCommands(); };
        account.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName is nameof(DeepIdV2AccountViewModel.Account) or nameof(DeepIdV2AccountViewModel.IsNetworkVerified) or nameof(DeepIdV2AccountViewModel.IsBusy))
            {
                if (change.PropertyName == nameof(DeepIdV2AccountViewModel.Account))
                {
                    accountGeneration++;
                    activeOperation?.Cancel();
                    var nextAccount = account.Account?.PermanentId.CanonicalText;
                    if (projectedAccount != nextAccount)
                    {
                        projectedAccount = nextAccount;
                        foreach (var op in contactIntents.Values.Concat(acceptanceOperations.Values)) CryptographicOperations.ZeroMemory(op);
                        contactIntents.Clear(); acceptanceOperations.Clear(); ClearTextOperation();
                        ContactAddress = string.Empty; DraftText = string.Empty;
                        Conversations.Clear(); Messages.Clear(); Selected = null;
                    }
                }
                if (account.Account is null)
                {
                    activeOperation?.Cancel();
                    Conversations.Clear(); Messages.Clear(); Selected = null;
                }
                else if (!account.IsNetworkVerified && activeRequiresNetwork) activeOperation?.Cancel();
                RaisePropertyChanged(nameof(CanReadLocal)); RaisePropertyChanged(nameof(IsReady)); NotifyCommands();
            }
        };
    }
    public ObservableCollection<DeepIdV2ConversationSnapshot> Conversations { get; } = [];
    public ObservableCollection<DirectMessageCreateSnapshot> Messages { get; } = [];
    public bool HasRuntime => runtime is not null;
    public bool CanReadLocal => HasRuntime && account.Account is not null && !account.IsBusy;
    public bool IsReady => CanReadLocal && account.IsNetworkVerified;
    public string ContactAddress { get => contactAddress; set { if (SetProperty(ref contactAddress, value)) NotifyContactRetry(); } }
    public bool HasContactRetry => ContactPeer() is { } peer && contactIntents.ContainsKey(peer);
    public string StartContactCaption => HasContactRetry ? "Повторить запрос" : "Добавить контакт";
    public string DraftText { get => draftText; set { if (SetProperty(ref draftText, value)) NotifyCommands(); } }
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public DeepIdV2ConversationSnapshot? Selected
    {
        get => selected;
        private set
        {
            if (!SetProperty(ref selected, value)) return;
            RaisePropertyChanged(nameof(CanAccept)); RaisePropertyChanged(nameof(CanSend));
            RaisePropertyChanged(nameof(AcceptCaption));
            RaisePropertyChanged(nameof(SelectionTitle)); RaisePropertyChanged(nameof(SelectionStatus)); NotifyCommands();
            RaisePropertyChanged(nameof(HasSelection));
        }
    }
    public bool CanAccept => Selected?.ContactState is DeepIdV2ContactState.IncomingRequest or DeepIdV2ContactState.LocalAcceptanceRetained;
    public string AcceptCaption => Selected?.ContactState == DeepIdV2ContactState.LocalAcceptanceRetained ? "Повторить доставку принятия" : "Принять контакт";
    public bool HasSelection => Selected is not null;
    public bool CanSend => Selected?.ContactState is DeepIdV2ContactState.LocalAcceptanceRetained or DeepIdV2ContactState.PeerAcceptanceRetained;
    public string SelectionTitle => Selected is null ? "Выберите диалог" : "Контакт " + Selected.Conversation.PeerAccountId[..8];
    public string SelectionStatus => Selected?.ContactState switch
    {
        DeepIdV2ContactState.IncomingRequest => "Входящий запрос — требуется ваше принятие",
        DeepIdV2ContactState.OutgoingRequest => "Запрос отправлен — ожидаем принятия",
        DeepIdV2ContactState.LocalAcceptanceRetained => "Вы приняли контакт",
        DeepIdV2ContactState.PeerAcceptanceRetained => "Контакт принят",
        _ => ""
    };

    public Task RefreshAsync(CancellationToken ct = default) => RunMessagingAsync(async token =>
    {
        var owner = await accounts.GetAccountsAsync(token);
        RequireLocalOperation(token);
        await LoadConversationsAsync(owner, Selected?.Conversation.ConversationId, token);
        if (!IsReady) { Status = "Локальная история. Для отправки требуется подключение к сети."; return; }
        activeRequiresNetwork = true;
        var received = reconnect is null ? await runtime!.SynchronizeAsync(owner, token) :
            await reconnect.RunForegroundAsync(next => runtime!.SynchronizeAsync(owner,next),token);
        RequireCurrentOperation(token);
        await LoadConversationsAsync(owner, Selected?.Conversation.ConversationId, token);
        Status = received.HasMore ? "Страница получена. Обновите ещё раз для продолжения." : "Синхронизация завершена";
    }, ct, requiresNetwork: false);

    public Task SelectConversationAsync(DeepIdV2ConversationSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return RunMessagingAsync(async token =>
        {
            if (!Conversations.Contains(snapshot)) throw new InvalidOperationException("The selection is not in the current projection.");
            var owner = await accounts.GetAccountsAsync(token);
            RequireLocalOperation(token);
            await RecoverPendingTextAsync(owner, token);
            DraftText = textComposer.DraftAfterSelection(Selected?.Conversation.ConversationId,
                snapshot.Conversation.ConversationId, DraftText);
            Selected = snapshot; Messages.Clear();
            var messages = await runtime!.MessagesAsync(owner, snapshot.Conversation, token);
            RequireLocalOperation(token);
            foreach (var message in messages) Messages.Add(message);
        }, ct, requiresNetwork: false);
    }
    public void CloseConversation() { Selected = null; DraftText = string.Empty; Messages.Clear(); }

    public Task StartContactAsync(CancellationToken ct = default)
    {
        var text = ContactAddress.Trim();
        return RunMessagingAsync(async token =>
        {
            var address = DeepPermanentIdV2.ParseCanonical(text);
            var peer = Convert.ToHexString(address.ExactDid2Hash.Span);
            var owner = await accounts.GetAccountsAsync(token);
            RequireLocalOperation(token);
            if (!contactIntents.TryGetValue(peer, out var intent))
            {
                var existing = (await owner.ListContactStartOperationsAsync(token))
                    .Where(value => value.PeerDid2Hash == peer && !value.IsStopped).OrderByDescending(value => value.CreatedAtUnixMilliseconds).FirstOrDefault();
                RequireLocalOperation(token);
                if (existing is not null)
                {
                    intent = existing.LogicalIntent.ToArray(); contactIntents.Add(peer, intent);
                    NotifyContactRetry();
                    Status = "Сохранён незавершённый запрос. Повторите его или остановите повторы. Остановка не отзывает запрос у получателя.";
                    return;
                }
            }
            if (!IsReady) { Status = "Для отправки или остановки повторов требуется проверенное подключение к сети."; return; }
            activeRequiresNetwork = true;
            async Task<DeepIdV2ContactStartResult> DispatchAsync(CancellationToken next)
            {
                RequireCurrentOperation(next);
                // Do not allocate even UI retry metadata while queued behind
                // background work. Existing owned originals were read locally.
                if (intent is null)
                { intent = NewOperation(); contactIntents.Add(peer, intent); NotifyContactRetry(); }
                return await runtime!.StartAsync(owner, address, intent, next);
            }
            var started = reconnect is null ? await DispatchAsync(token) :
                await reconnect.RunForegroundAsync(DispatchAsync, token);
            RequireCurrentOperation(token);
            await LoadConversationsAsync(owner, started.Conversation.ConversationId, token);
            Status = "Запрос контакта сохранён в сети. Ожидаем принятия.";
        }, ct, requiresNetwork: false);
    }

    public Task StopContactRetryAsync(CancellationToken ct = default)
    {
        var peer = ContactPeer();
        return RunMessagingAsync(async token =>
        {
            if (peer is null || !contactIntents.TryGetValue(peer, out var intent))
                throw new InvalidOperationException("An exact retained contact intent is required.");
            var owner = await accounts.GetAccountsAsync(token);
            RequireCurrentOperation(token);
            await runtime!.StopContactRetryAsync(owner, intent, token);
            RequireCurrentOperation(token);
            // A failed/lost stop reply retains this exact UI retry choice. Only
            // confirmed owned stop allows a later explicit Add to select new work.
            contactIntents.Remove(peer); CryptographicOperations.ZeroMemory(intent);
            NotifyContactRetry();
            Status = "Повторы остановлены. Это не отзывает запрос у получателя. Можно отдельно добавить контакт заново.";
        }, ct);
    }

    private string? ContactPeer()
    {
        try { return Convert.ToHexString(DeepPermanentIdV2.ParseCanonical(ContactAddress.Trim()).ExactDid2Hash.Span); }
        catch (Exception error) when (error is FormatException or ArgumentException) { return null; }
    }
    private void NotifyContactRetry()
    {
        RaisePropertyChanged(nameof(HasContactRetry)); RaisePropertyChanged(nameof(StartContactCaption)); NotifyCommands();
    }

    public Task AcceptContactAsync(CancellationToken ct = default)
    {
        var target = Selected;
        return RunMessagingAsync(async token =>
        {
            if (target is null || target.ContactState is not (DeepIdV2ContactState.IncomingRequest or DeepIdV2ContactState.LocalAcceptanceRetained)) throw new InvalidOperationException("Explicit recipient acceptance is required.");
            var key = target.Conversation.ConversationId;
            if (!acceptanceOperations.TryGetValue(key, out var op)) { op = NewOperation(); acceptanceOperations.Add(key, op); }
            var owner = await accounts.GetAccountsAsync(token);
            RequireCurrentOperation(token);
            await runtime!.AcceptAsync(owner, target.Conversation, op, token);
            RequireCurrentOperation(token);
            await LoadConversationsAsync(owner, key, token); Status = "Принятие контакта сохранено в сети";
        }, ct);
    }

    public Task SendTextAsync(CancellationToken ct = default)
    {
        var target = Selected; var text = DraftText;
        return RunMessagingAsync(async token =>
        {
            if (target is null || target.ContactState is not (DeepIdV2ContactState.LocalAcceptanceRetained or DeepIdV2ContactState.PeerAcceptanceRetained) || string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("An accepted contact and text are required.");
            var key = target.Conversation.ConversationId;
            var owner = await accounts.GetAccountsAsync(token);
            RequireCurrentOperation(token);
            await RecoverPendingTextAsync(owner, token);
            var operation = textComposer.ReadRetry(key, text);
            if (operation is null)
            {
                var queued = await runtime!.QueueTextAsync(owner, target.Conversation, text, token);
                RequireCurrentOperation(token);
                textComposer.Restore(queued.Span, key, text);
                operation = textComposer.ReadRetry(key, text) ?? throw new InvalidOperationException("Queued text lost its owned operation.");
            }
            try { await runtime!.RetryTextAsync(owner, target.Conversation, operation, text, token); }
            finally { CryptographicOperations.ZeroMemory(operation); }
            RequireCurrentOperation(token);
            if (DraftText == text) DraftText = string.Empty;
            ClearTextOperation();
            await LoadConversationsAsync(owner, key, token);
            Status = "Сообщение сохранено в сети";
        }, ct);
    }
    private async Task LoadConversationsAsync(DeepIdV2AccountService owner, string? selection, CancellationToken ct)
    {
        var list = await runtime!.ListAsync(owner, ct);
        RequireLocalOperation(ct);
        await RecoverPendingTextAsync(owner, ct);
        Conversations.Clear(); foreach (var conversation in list) Conversations.Add(conversation);
        var next = selection is null ? null : list.SingleOrDefault(value => value.Conversation.ConversationId == selection);
        DraftText = textComposer.DraftAfterSelection(Selected?.Conversation.ConversationId,
            next?.Conversation.ConversationId, DraftText);
        Selected = next;
        Messages.Clear();
        if (Selected is { } active)
        {
            if (string.IsNullOrEmpty(DraftText)) DraftText = textComposer.DraftFor(active.Conversation.ConversationId);
            var messages = await runtime.MessagesAsync(owner, active.Conversation, ct);
            RequireLocalOperation(ct);
            foreach (var message in messages) Messages.Add(message);
        }
    }
    private async Task RecoverPendingTextAsync(DeepIdV2AccountService owner, CancellationToken ct)
    {
        var pending = (await owner.ListPendingTextOperationsAsync(ct)).FirstOrDefault();
        RequireLocalOperation(ct);
        if (pending is null) return;
        var restored = pending.LogicalOperation.ToArray();
        try { textComposer.Restore(restored, pending.ConversationId, pending.Text); }
        finally { CryptographicOperations.ZeroMemory(restored); }
    }
    private Task RunMessagingAsync(Func<CancellationToken, Task> action, CancellationToken ct, bool requiresNetwork = true)
    {
        var generation = accountGeneration;
        return RunBusyAsync(async token =>
        {
            using var scope = CancellationTokenSource.CreateLinkedTokenSource(token);
            activeOperation = scope;
            activeRequiresNetwork = requiresNetwork;
            try
            {
                if (!(requiresNetwork ? IsReady : CanReadLocal) || generation != accountGeneration) throw new InvalidOperationException("The DID2 runtime is unavailable.");
                if (requiresNetwork && reconnect is not null)
                    await reconnect.RunForegroundAsync(action,scope.Token);
                else await action(scope.Token);
                if (!(requiresNetwork ? IsReady : CanReadLocal) || generation != accountGeneration)
                {
                    throw new InvalidOperationException("Account state changed during the operation.");
                }
            }
            catch (Exception error)
            {
                // Optional QA metadata cannot alter the original failure,
                // retain authority or prevent this error from reaching the UI.
                try { failureObserver?.Invoke(error); } catch { }
                if (account.Account is null || error is CryptographicException or InvalidDataException)
                { Conversations.Clear(); Messages.Clear(); Selected = null; }
                Status = string.Empty;
                var message = error switch
                {
                    FormatException or ArgumentException => "Проверьте адрес контакта или размер сообщения.",
                    OperationCanceledException => "Операция отменена. Сохранённый запрос можно повторить.",
                    CryptographicException or InvalidDataException => "Проверка защищённого состояния не пройдена. Данные не отправлены повторно.",
                    _ => "Операция не завершена. Повторите её с тем же адресом или текстом."
                };
                throw new InvalidOperationException(message,error);
            }
            finally { activeOperation = null; activeRequiresNetwork = false; }
        }, ct);
    }
    private void RequireCurrentOperation(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsReady) throw new InvalidOperationException("The verified DID2 runtime is unavailable.");
    }
    private void RequireLocalOperation(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!CanReadLocal) throw new InvalidOperationException("The local DID2 account is unavailable.");
    }
    private void ClearTextOperation()
        => textComposer.Clear();
    private static byte[] NewOperation()
    { var bytes = new byte[32]; do RandomNumberGenerator.Fill(bytes); while (bytes.AsSpan().IndexOfAnyExcept((byte)0) < 0); return bytes; }
    private void NotifyCommands()
    { RefreshCommand.RaiseCanExecuteChanged(); StartContactCommand.RaiseCanExecuteChanged(); StopContactRetryCommand.RaiseCanExecuteChanged(); AcceptContactCommand.RaiseCanExecuteChanged(); SendTextCommand.RaiseCanExecuteChanged(); }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand StartContactCommand { get; }
    public AsyncCommand StopContactRetryCommand { get; }
    public AsyncCommand AcceptContactCommand { get; }
    public AsyncCommand SendTextCommand { get; }
}

// UI retry metadata only. Restore is called solely with actual owner projection;
// this state cannot construct a Shared handle or authorize send/completion.
internal sealed class DeepIdV2TextComposerState
{
    private byte[]? operation;
    private string? conversation, text;
    internal string DraftFor(string conversationId) => conversation == conversationId ? text ?? string.Empty : string.Empty;
    // Display-only selection metadata. Never carry editable text to a different
    // peer; an unknown send's original text is restored only for its own dialog.
    internal string DraftAfterSelection(string? previousConversation, string? nextConversation, string displayedDraft)
        => nextConversation is null ? string.Empty
            : previousConversation == nextConversation ? displayedDraft
            : DraftFor(nextConversation);
    internal void Restore(ReadOnlySpan<byte> retainedOperation, string conversationId, string retainedText)
    {
        if (retainedOperation.Length != 32 || retainedOperation.IndexOfAnyExcept((byte)0) < 0)
            throw new ArgumentException("An exact retained operation is required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(retainedText);
        if (operation is not null && retainedOperation.SequenceEqual(operation))
        {
            if (conversation != conversationId || text != retainedText)
                throw new CryptographicException("Retained UI operation changed its original metadata.");
            return;
        }
        Clear(); operation = retainedOperation.ToArray(); conversation = conversationId; text = retainedText;
    }
    internal byte[]? ReadRetry(string conversationId, string candidateText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateText);
        if (operation is not null && (conversation != conversationId || text != candidateText))
            throw new InvalidOperationException("The pending text operation must be reconciled before replacing it.");
        return operation?.ToArray();
    }
    internal void Clear()
    {
        if (operation is not null) CryptographicOperations.ZeroMemory(operation);
        operation = null; conversation = null; text = null;
    }
}
