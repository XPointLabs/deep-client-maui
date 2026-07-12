using Deep.Client.Maui.Core.Commands;
using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.Core.ViewModels;

public sealed class OnboardingViewModel : ViewModelBase
{
    private readonly ClientRuntime runtime;
    private readonly AuthNavigationState? authNavigationState;
    private string displayName = string.Empty;
    private string recoveryPhrase = string.Empty;
    private string? generatedRecoveryPhrase;
    private string? sessionId;
    private bool isLoggedIn;

    public OnboardingViewModel(ClientRuntime runtime, AuthNavigationState? authNavigationState = null)
    {
        this.runtime = runtime;
        this.authNavigationState = authNavigationState;
        RegisterCommand = new AsyncCommand(RegisterAsync, () => !string.IsNullOrWhiteSpace(DisplayName));
        LoginCommand = new AsyncCommand(LoginAsync, () => !string.IsNullOrWhiteSpace(RecoveryPhrase));
    }

    public string DisplayName
    {
        get => displayName;
        set
        {
            if (SetProperty(ref displayName, value))
            {
                RegisterCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string RecoveryPhrase
    {
        get => recoveryPhrase;
        set
        {
            if (SetProperty(ref recoveryPhrase, value))
            {
                LoginCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? GeneratedRecoveryPhrase
    {
        get => generatedRecoveryPhrase;
        private set
        {
            if (SetProperty(ref generatedRecoveryPhrase, value))
            {
                RaisePropertyChanged(nameof(GeneratedRecoveryPhraseMultiline));
            }
        }
    }

    public string? GeneratedRecoveryPhraseMultiline => FormatRecoveryPhrase(GeneratedRecoveryPhrase);

    public string? SessionId
    {
        get => sessionId;
        private set => SetProperty(ref sessionId, value);
    }

    public bool IsLoggedIn
    {
        get => isLoggedIn;
        private set => SetProperty(ref isLoggedIn, value);
    }

    public SessionAccount? Account { get; private set; }

    public AsyncCommand RegisterCommand { get; }

    public AsyncCommand LoginCommand { get; }

    public Task RegisterAsync(CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            Account = await runtime.Accounts.RegisterAsync(DisplayName, ct);
            GeneratedRecoveryPhrase = await runtime.Accounts.GetRecoveryPhraseAsync(ct);
            SessionId = Account.SessionId.Value;
            IsLoggedIn = true;
            authNavigationState?.MarkAuthenticated();
        }, cancellationToken);

    public async Task LoginAsync(CancellationToken cancellationToken = default)
    {
        var phraseForAttempt = RecoveryPhrase;
        try
        {
            await RunBusyAsync(async ct =>
            {
                Account = await runtime.Accounts.LoginAsync(phraseForAttempt, DisplayName, ct);
                DisplayName = Account.DisplayName;
                SessionId = Account.SessionId.Value;
                IsLoggedIn = true;
                authNavigationState?.MarkAuthenticated();
            }, cancellationToken);
        }
        finally
        {
            ClearRecoveryPhrase();
        }
    }

    public void ClearRecoveryPhrase() => RecoveryPhrase = string.Empty;

    private static string? FormatRecoveryPhrase(string? phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
        {
            return phrase;
        }

        var words = phrase
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        if (words.Length <= 3)
        {
            return string.Join(' ', words);
        }

        var lines = new List<string>();
        for (var i = 0; i < words.Length; i += 3)
        {
            var chunk = words.Skip(i).Take(3);
            lines.Add(string.Join(' ', chunk));
        }

        return string.Join(Environment.NewLine, lines);
    }
}
