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
    private readonly Dictionary<string, byte[]> contactIntents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> acceptanceOperations = new(StringComparer.Ordinal);
    private string contactAddress = string.Empty, draftText = string.Empty, status = string.Empty;
    private DeepIdV2ConversationSnapshot? selected;
    private int accountGeneration;
    private CancellationTokenSource? activeOperation;
    private readonly DeepIdV2TextComposerState textComposer = new();

    public DeepIdV2MessagingViewModel(IDeepIdV2AccountRuntimeAccessor accounts,
        DeepIdV2AccountViewModel account, IDeepIdV2ConversationRuntime? runtime = null)
    {
        this.accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        this.account = account ?? throw new ArgumentNullException(nameof(account)); this.runtime = runtime;
        RefreshCommand = new(RefreshAsync, () => IsReady && !IsBusy);
        StartContactCommand = new(StartContactAsync, () => IsReady && !IsBusy && !string.IsNullOrWhiteSpace(ContactAddress));
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
                    foreach (var op in contactIntents.Values.Concat(acceptanceOperations.Values)) CryptographicOperations.ZeroMemory(op);
                    contactIntents.Clear(); acceptanceOperations.Clear(); ClearTextOperation();
                    ContactAddress = string.Empty; DraftText = string.Empty;
                }
                if (account.Account is null || !account.IsNetworkVerified)
                {
                    activeOperation?.Cancel();
                    Conversations.Clear(); Messages.Clear(); Selected = null;
                }
                RaisePropertyChanged(nameof(IsReady)); NotifyCommands();
            }
        };
    }
    public ObservableCollection<DeepIdV2ConversationSnapshot> Conversations { get; } = [];
    public ObservableCollection<DirectMessageCreateSnapshot> Messages { get; } = [];
    public bool HasRuntime => runtime is not null;
    public bool IsReady => HasRuntime && account.Account is not null && account.IsNetworkVerified && !account.IsBusy;
    public string ContactAddress { get => contactAddress; set { if (SetProperty(ref contactAddress, value)) NotifyCommands(); } }
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
        RequireCurrentOperation(token);
        var received = await runtime!.SynchronizeAsync(owner, token);
        RequireCurrentOperation(token);
        await LoadConversationsAsync(owner, Selected?.Conversation.ConversationId, token);
        Status = received.HasMore ? "Страница получена. Обновите ещё раз для продолжения." : "Синхронизация завершена";
    }, ct, clearProjectionOnFailure: true);

    public Task SelectConversationAsync(DeepIdV2ConversationSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return RunMessagingAsync(async token =>
        {
            if (!Conversations.Contains(snapshot)) throw new InvalidOperationException("The selection is not in the current projection.");
            var owner = await accounts.GetAccountsAsync(token);
            RequireCurrentOperation(token);
            await RecoverPendingTextAsync(owner, token);
            DraftText = textComposer.DraftAfterSelection(Selected?.Conversation.ConversationId,
                snapshot.Conversation.ConversationId, DraftText);
            Selected = snapshot; Messages.Clear();
            var messages = await runtime!.MessagesAsync(owner, snapshot.Conversation, token);
            RequireCurrentOperation(token);
            foreach (var message in messages) Messages.Add(message);
        }, ct, clearProjectionOnFailure: true);
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
            RequireCurrentOperation(token);
            if (!contactIntents.TryGetValue(peer, out var intent))
            {
                var existing = (await owner.ListContactStartOperationsAsync(token))
                    .Where(value => value.PeerDid2Hash == peer).OrderByDescending(value => value.CreatedAtUnixMilliseconds).FirstOrDefault();
                RequireCurrentOperation(token);
                intent = existing?.LogicalIntent.ToArray() ?? NewOperation(); contactIntents.Add(peer, intent);
            }
            var started = await runtime!.StartAsync(owner, address, intent, token);
            RequireCurrentOperation(token);
            await LoadConversationsAsync(owner, started.Conversation.ConversationId, token);
            Status = "Запрос контакта сохранён в сети. Ожидаем принятия.";
        }, ct);
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
            var operation = textComposer.Prepare(key, text);
            try { await runtime!.SendTextAsync(owner, target.Conversation, operation, text, token); }
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
        RequireCurrentOperation(ct);
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
            RequireCurrentOperation(ct);
            foreach (var message in messages) Messages.Add(message);
        }
    }
    private async Task RecoverPendingTextAsync(DeepIdV2AccountService owner, CancellationToken ct)
    {
        var pending = (await owner.ListPendingTextOperationsAsync(ct)).FirstOrDefault();
        RequireCurrentOperation(ct);
        if (pending is null) return;
        var restored = pending.LogicalOperation.ToArray();
        try { textComposer.Restore(restored, pending.ConversationId, pending.Text); }
        finally { CryptographicOperations.ZeroMemory(restored); }
    }
    private Task RunMessagingAsync(Func<CancellationToken, Task> action, CancellationToken ct, bool clearProjectionOnFailure = false)
    {
        var generation = accountGeneration;
        return RunBusyAsync(async token =>
        {
            using var scope = CancellationTokenSource.CreateLinkedTokenSource(token);
            activeOperation = scope;
            try
            {
                if (!IsReady || generation != accountGeneration) throw new InvalidOperationException("The verified DID2 runtime is unavailable.");
                await action(scope.Token);
                if (!IsReady || generation != accountGeneration)
                {
                    Conversations.Clear(); Messages.Clear(); Selected = null;
                    throw new InvalidOperationException("Account state changed during the operation.");
                }
            }
            catch (Exception error)
            {
                if (clearProjectionOnFailure || !IsReady || generation != accountGeneration) { Conversations.Clear(); Messages.Clear(); Selected = null; }
                Status = string.Empty;
                var message = error switch
                {
                    FormatException or ArgumentException => "Проверьте адрес контакта или размер сообщения.",
                    OperationCanceledException => "Операция отменена. Сохранённый запрос можно повторить.",
                    CryptographicException or InvalidDataException => "Проверка защищённого состояния не пройдена. Данные не отправлены повторно.",
                    _ => "Операция не завершена. Повторите её с тем же адресом или текстом."
                };
                throw new InvalidOperationException(message);
            }
            finally { activeOperation = null; }
        }, ct);
    }
    private void RequireCurrentOperation(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsReady) throw new InvalidOperationException("The verified DID2 runtime is unavailable.");
    }
    private void ClearTextOperation()
        => textComposer.Clear();
    private static byte[] NewOperation()
    { var bytes = new byte[32]; do RandomNumberGenerator.Fill(bytes); while (bytes.AsSpan().IndexOfAnyExcept((byte)0) < 0); return bytes; }
    private void NotifyCommands()
    { RefreshCommand.RaiseCanExecuteChanged(); StartContactCommand.RaiseCanExecuteChanged(); AcceptContactCommand.RaiseCanExecuteChanged(); SendTextCommand.RaiseCanExecuteChanged(); }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand StartContactCommand { get; }
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
    internal byte[] Prepare(string conversationId, string candidateText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateText);
        if (operation is not null && (conversation != conversationId || text != candidateText))
            throw new InvalidOperationException("The pending text operation must be reconciled before replacing it.");
        if (operation is null)
        {
            operation = new byte[32];
            do RandomNumberGenerator.Fill(operation); while (operation.AsSpan().IndexOfAnyExcept((byte)0) < 0);
            conversation = conversationId; text = candidateText;
        }
        return operation.ToArray();
    }
    internal void Clear()
    {
        if (operation is not null) CryptographicOperations.ZeroMemory(operation);
        operation = null; conversation = null; text = null;
    }
}
