using System.Text;
using Deep.Client.Maui.Core.Commands;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Core.ViewModels;

/// <summary>
/// Offline DID2 first-device account experience. Transport-dependent controls
/// must not use this model until they consume V2 account authority themselves.
/// </summary>
public sealed class DeepIdV2AccountViewModel : ViewModelBase
{
    private readonly IDeepIdV2AccountRuntimeAccessor runtime;
    private readonly IDeepIdV2NetworkAdmission? networkAdmission;
    private readonly IDeepIdV2ContactDiscovery? contactDiscovery;
    private string displayName = string.Empty;
    private DeepIdV2AccountSnapshot? account;
    private string revealedRecoveryPhrase = string.Empty;
    private bool hasRetainedRecoveryPhrase;
    private bool isNetworkVerified;
    private bool isContactProofVerified;
    private int contactInputRevision;

    public DeepIdV2AccountViewModel(IDeepIdV2AccountRuntimeAccessor runtime,
        IDeepIdV2NetworkAdmission? networkAdmission = null,
        IDeepIdV2ContactDiscovery? contactDiscovery = null)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.networkAdmission = networkAdmission;
        this.contactDiscovery = contactDiscovery;
        CreateAccountCommand = new AsyncCommand(CreateAccountAsync,
            () => Account is null && !string.IsNullOrWhiteSpace(DisplayName));
        RevealRecoveryPhraseCommand = new AsyncCommand(RevealRecoveryPhraseAsync,
            () => HasRetainedRecoveryPhrase && !IsRecoveryPhraseRevealed);
        HideRecoveryPhraseCommand = new AsyncCommand(_ =>
        {
            HideRecoveryPhrase();
            return Task.CompletedTask;
        }, () => IsRecoveryPhraseRevealed);
        DeleteRecoveryPhraseCommand = new AsyncCommand(DeleteRecoveryPhraseAsync,
            () => HasRetainedRecoveryPhrase);
        VerifyNetworkCommand = new AsyncCommand(VerifyNetworkAsync,
            () => Account is not null && networkAdmission is not null);
    }

    public string DisplayName
    {
        get => displayName;
        set
        {
            if (SetProperty(ref displayName, value))
                CreateAccountCommand.RaiseCanExecuteChanged();
        }
    }

    public DeepIdV2AccountSnapshot? Account
    {
        get => account;
        private set
        {
            if (SetProperty(ref account, value))
            {
                CreateAccountCommand.RaiseCanExecuteChanged();
                VerifyNetworkCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string RevealedRecoveryPhrase
    {
        get => revealedRecoveryPhrase;
        private set
        {
            if (SetProperty(ref revealedRecoveryPhrase, value))
            {
                RaisePropertyChanged(nameof(IsRecoveryPhraseRevealed));
                RevealRecoveryPhraseCommand.RaiseCanExecuteChanged();
                HideRecoveryPhraseCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsRecoveryPhraseRevealed =>
        RevealedRecoveryPhrase.Length != 0;

    public bool HasRetainedRecoveryPhrase
    {
        get => hasRetainedRecoveryPhrase;
        private set
        {
            if (SetProperty(ref hasRetainedRecoveryPhrase, value))
            {
                RevealRecoveryPhraseCommand.RaiseCanExecuteChanged();
                DeleteRecoveryPhraseCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasNetworkAdmission => networkAdmission is not null;
    public bool HasContactDiscovery => contactDiscovery is not null;

    public bool IsNetworkVerified
    {
        get => isNetworkVerified;
        private set => SetProperty(ref isNetworkVerified, value);
    }

    public bool IsContactProofVerified
    {
        get => isContactProofVerified;
        private set => SetProperty(ref isContactProofVerified, value);
    }

    public AsyncCommand CreateAccountCommand { get; }
    public AsyncCommand RevealRecoveryPhraseCommand { get; }
    public AsyncCommand HideRecoveryPhraseCommand { get; }
    public AsyncCommand DeleteRecoveryPhraseCommand { get; }
    public AsyncCommand VerifyNetworkCommand { get; }

    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            HideRecoveryPhrase();
            IsNetworkVerified = false;
            InvalidateContactProof();
            var accounts = await runtime.GetAccountsAsync(ct);
            Account = await accounts.GetCurrentAsync(ct);
            DisplayName = Account?.DisplayName ?? string.Empty;
            using var phrase = Account is null ? null :
                await accounts.ReadRetainedRecoveryPhraseAsync(ct);
            HasRetainedRecoveryPhrase = phrase is not null;
        }, cancellationToken);

    public Task CreateAccountAsync(CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            HideRecoveryPhrase();
            IsNetworkVerified = false;
            InvalidateContactProof();
            var accounts = await runtime.GetAccountsAsync(ct);
            Account = await accounts.CreateAsync(DisplayName, ct);
            DisplayName = Account.DisplayName;
            HasRetainedRecoveryPhrase = true;
        }, cancellationToken);

    public Task VerifyNetworkAsync(CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            IsNetworkVerified = false;
            InvalidateContactProof();
            if (networkAdmission is null || Account is null)
                throw new InvalidOperationException(
                    "DID2 network admission is unavailable for this account.");
            var accounts = await runtime.GetAccountsAsync(ct);
            await networkAdmission.VerifyAsync(accounts, ct);
            IsNetworkVerified = true;
        }, cancellationToken);

    public Task VerifyContactProofAsync(string compactDescriptor,
        string exactDid2Hex,
        CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            IsContactProofVerified = false;
            var inputRevision = Volatile.Read(ref contactInputRevision);
            if (contactDiscovery is null || Account is null ||
                !IsNetworkVerified)
                throw new InvalidOperationException(
                    "DID2 contact proof requires a verified local network account.");
            var accounts = await runtime.GetAccountsAsync(ct);
            await contactDiscovery.VerifyAsync(accounts, compactDescriptor,
                exactDid2Hex, ct);
            if (inputRevision != Volatile.Read(ref contactInputRevision))
                throw new InvalidOperationException(
                    "Contact input changed during DID2 proof verification.");
            IsContactProofVerified = true;
        }, cancellationToken);

    public void InvalidateContactProof()
    {
        Interlocked.Increment(ref contactInputRevision);
        IsContactProofVerified = false;
    }

    public Task RevealRecoveryPhraseAsync(
        CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            HideRecoveryPhrase();
            var accounts = await runtime.GetAccountsAsync(ct);
            using var phrase = await accounts.ReadRetainedRecoveryPhraseAsync(ct);
            if (phrase is null)
            {
                HasRetainedRecoveryPhrase = false;
                return;
            }
            phrase.UseCanonicalUtf8(bytes =>
                RevealedRecoveryPhrase = Encoding.UTF8.GetString(bytes));
            HasRetainedRecoveryPhrase = true;
        }, cancellationToken);

    public void HideRecoveryPhrase() => RevealedRecoveryPhrase = string.Empty;

    public Task DeleteRecoveryPhraseAsync(
        CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            HideRecoveryPhrase();
            var accounts = await runtime.GetAccountsAsync(ct);
            await accounts.DeleteRetainedRecoveryPhraseAsync(ct);
            HasRetainedRecoveryPhrase = false;
        }, cancellationToken);
}
