using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Persistence.ContactV1;
using Deep.Client.Shared.Services.ContactV1;

namespace Deep.Client.Maui.Core.ViewModels;

public enum ArbitraryContactUiState
{
    Idle = 0,
    Resolving = 1,
    InputRejected = 2,
    PendingRetry = 3,
    Verified = 4,
    Terminal = 5,
    FailClosed = 6,
    LocalUnavailable = 7,
}

public sealed class NewConversationViewModel : ViewModelBase
{
    private readonly IDeepContactRuntimeAccessor contactRuntime;
    private readonly SemaphoreSlim resolveGate = new(1, 1);
    private string addressInput = string.Empty;
    private string attemptedInput = string.Empty;
    private string statusTitle = string.Empty;
    private string statusMessage = string.Empty;
    private ArbitraryContactUiState state;
    private VerifiedDirectConversationTarget? verifiedConversation;

    public NewConversationViewModel(IDeepContactRuntimeAccessor contactRuntime) =>
        this.contactRuntime = contactRuntime ?? throw new ArgumentNullException(nameof(contactRuntime));

    public string AddressInput
    {
        get => addressInput;
        set
        {
            if (!SetProperty(ref addressInput, value ?? string.Empty))
            {
                return;
            }

            if (!string.Equals(addressInput, attemptedInput, StringComparison.Ordinal))
            {
                ResetOutcome();
            }
            RaiseActionProperties();
        }
    }

    public ArbitraryContactUiState State
    {
        get => state;
        private set
        {
            if (SetProperty(ref state, value))
            {
                RaiseActionProperties();
            }
        }
    }

    public string StatusTitle
    {
        get => statusTitle;
        private set => SetProperty(ref statusTitle, value);
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public VerifiedDirectConversationTarget? VerifiedConversation
    {
        get => verifiedConversation;
        private set
        {
            if (SetProperty(ref verifiedConversation, value))
            {
                RaisePropertyChanged(nameof(HasVerifiedConversation));
            }
        }
    }

    public bool HasStatus => State is ArbitraryContactUiState.PendingRetry or
        ArbitraryContactUiState.Verified or
        ArbitraryContactUiState.Terminal or
        ArbitraryContactUiState.FailClosed;

    public bool CanResolve => !IsBusy && !string.IsNullOrEmpty(AddressInput) &&
        State is not ArbitraryContactUiState.Verified and
        not ArbitraryContactUiState.Terminal and
        not ArbitraryContactUiState.FailClosed;

    public bool CanRetry => State == ArbitraryContactUiState.PendingRetry && CanResolve;

    public bool ShowResolveAction => State is ArbitraryContactUiState.Idle or
        ArbitraryContactUiState.Resolving or
        ArbitraryContactUiState.InputRejected or
        ArbitraryContactUiState.LocalUnavailable;

    public bool HasVerifiedConversation => VerifiedConversation is not null;

    public bool CanEditAddress => !IsBusy;

    public async Task ResolveAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(AddressInput))
        {
            ApplyInputFailure("Введите полный постоянный Deep ID или canonical deepinvite.");
            return;
        }

        await resolveGate.WaitAsync(cancellationToken);
        try
        {
            if (IsBusy)
            {
                return;
            }

            IsBusy = true;
            attemptedInput = AddressInput;
            ErrorMessage = null;
            VerifiedConversation = null;
            State = ArbitraryContactUiState.Resolving;
            StatusTitle = string.Empty;
            StatusMessage = string.Empty;
            RaisePropertyChanged(nameof(HasStatus));

            var result = await contactRuntime.ImportAndEnqueueResolveAsync(
                attemptedInput,
                cancellationToken);
            if (!string.Equals(AddressInput, attemptedInput, StringComparison.Ordinal))
            {
                ResetOutcome();
                return;
            }
            Apply(result);
        }
        catch (ContactAddressImportException exception)
        {
            ApplyInputFailure(exception.Failure switch
            {
                ContactAddressImportFailure.WrongNetwork =>
                    "Приглашение относится к другой XPoint Network.",
                ContactAddressImportFailure.LocalCapacityExceeded =>
                    "Локальная очередь контактов заполнена. Удалите ненужный ожидающий контакт и повторите попытку.",
                _ => "Введите полный canonical permanent deep1… Deep ID или поддерживаемый canonical deepinvite DIA1.",
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            StatusTitle = "Проверка не пройдена";
            StatusMessage =
                "Получено несогласованное состояние ContactV1. Контакт не открыт; повторять ту же попытку небезопасно.";
            ErrorMessage = null;
            VerifiedConversation = null;
            State = ArbitraryContactUiState.FailClosed;
        }
        catch (Exception)
        {
            StatusTitle = "Локальное хранилище недоступно";
            StatusMessage =
                "Не удалось открыть локальную очередь контактов. Интернет для запуска аккаунта не требуется; повторите попытку позже.";
            ErrorMessage = StatusMessage;
            VerifiedConversation = null;
            State = ArbitraryContactUiState.LocalUnavailable;
            RaisePropertyChanged(nameof(HasStatus));
        }
        finally
        {
            IsBusy = false;
            RaiseActionProperties();
            resolveGate.Release();
        }
    }

    public void ReportDirectRuntimeUnavailable()
    {
        if (State != ArbitraryContactUiState.Verified || VerifiedConversation is null)
        {
            throw new InvalidOperationException(
                "Only a verified ContactV1 conversation can report direct-runtime availability.");
        }

        StatusTitle = "Контакт подтверждён";
        StatusMessage =
            "Контакт безопасно сохранён. Защищённый чат пока недоступен в этой сборке: новый модуль личных сообщений не активирован.";
        RaisePropertyChanged(nameof(HasStatus));
    }

    private void Apply(ContactImportAndResolveResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.QueueState == ContactResolveQueueState.Verified &&
            result.VerifiedConversation is null)
        {
            throw new InvalidDataException(
                "A verified ContactV1 result has no verifier-minted conversation target.");
        }
        if (result.QueueState != ContactResolveQueueState.Verified &&
            result.VerifiedConversation is not null)
        {
            throw new InvalidDataException(
                "An unverified ContactV1 result cannot expose a conversation target.");
        }
        if ((result.QueueState == ContactResolveQueueState.PendingRetry) != result.CanRetry)
        {
            throw new InvalidDataException(
                "ContactV1 retry state and retry capability differ.");
        }
        if (result.QueueState == ContactResolveQueueState.Verified &&
            result.ResolverDisposition != ContactResolverDisposition.Verified)
        {
            throw new InvalidDataException(
                "A verified ContactV1 UI result has no verified resolver outcome.");
        }

        StatusTitle = result.Title;
        StatusMessage = result.Message;
        VerifiedConversation = result.VerifiedConversation;
        State = result.QueueState switch
        {
            ContactResolveQueueState.PendingRetry => ArbitraryContactUiState.PendingRetry,
            ContactResolveQueueState.Verified => ArbitraryContactUiState.Verified,
            ContactResolveQueueState.Terminal => ArbitraryContactUiState.Terminal,
            ContactResolveQueueState.FailClosed => ArbitraryContactUiState.FailClosed,
            _ => throw new ArgumentOutOfRangeException(nameof(result)),
        };
        RaisePropertyChanged(nameof(HasStatus));
    }

    private void ApplyInputFailure(string message)
    {
        StatusTitle = "Адрес не принят";
        StatusMessage = message;
        ErrorMessage = message;
        VerifiedConversation = null;
        State = ArbitraryContactUiState.InputRejected;
        RaisePropertyChanged(nameof(HasStatus));
    }

    private void ResetOutcome()
    {
        State = ArbitraryContactUiState.Idle;
        StatusTitle = string.Empty;
        StatusMessage = string.Empty;
        ErrorMessage = null;
        VerifiedConversation = null;
        RaisePropertyChanged(nameof(HasStatus));
    }

    private void RaiseActionProperties()
    {
        RaisePropertyChanged(nameof(CanResolve));
        RaisePropertyChanged(nameof(CanRetry));
        RaisePropertyChanged(nameof(ShowResolveAction));
        RaisePropertyChanged(nameof(HasStatus));
        RaisePropertyChanged(nameof(CanEditAddress));
    }
}
