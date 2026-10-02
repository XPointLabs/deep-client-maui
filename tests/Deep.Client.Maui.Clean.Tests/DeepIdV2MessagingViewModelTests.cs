using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Protocol.ApplicationCore;
using Deep.Protocol.Identity;

namespace Deep.Client.Maui.Clean.Tests;

// UI state/error plumbing only. Stub admission/transport is never network,
// crypto, mailbox or physical delivery evidence; handles cannot be forged here.
public sealed class DeepIdV2MessagingViewModelTests
{
    [Fact]
    public void ComposerRestartRestoresOriginalOperationAndCannotReplaceUnknownText()
    {
        // UI metadata only; no forged account/conversation/freshness authority.
        var original = new DeepIdV2TextComposerState();
        var intent = original.Prepare("conversation-a", "retained 📨");
        Assert.Equal(intent, original.Prepare("conversation-a", "retained 📨"));
        var restarted = new DeepIdV2TextComposerState(); restarted.Restore(intent, "conversation-a", "retained 📨");
        var callerCopy = restarted.Prepare("conversation-a", "retained 📨"); callerCopy.AsSpan().Clear();
        Assert.Equal(intent, restarted.Prepare("conversation-a", "retained 📨"));
        Assert.Equal("retained 📨", restarted.DraftFor("conversation-a")); Assert.Empty(restarted.DraftFor("conversation-b"));
        Assert.Throws<InvalidOperationException>(() => restarted.Prepare("conversation-a", "changed"));
        Assert.Throws<InvalidOperationException>(() => restarted.Prepare("conversation-b", "retained 📨"));
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => restarted.Restore(intent, "conversation-a", "changed"));
        restarted.Clear(); Assert.Empty(restarted.DraftFor("conversation-a"));
        Assert.NotEqual(intent, restarted.Prepare("conversation-a", "next"));
        original.Clear(); restarted.Clear();
    }

    [Theory]
    [InlineData(0)] [InlineData(31)] [InlineData(32)] [InlineData(33)]
    public void ComposerRejectsMalformedRetainedOperationBeforeReplacingMetadata(int length)
    {
        var state = new DeepIdV2TextComposerState(); var original = state.Prepare("a", "text");
        Assert.Throws<ArgumentException>(() => state.Restore(new byte[length], "b", "other"));
        Assert.Equal(original, state.Prepare("a", "text")); state.Clear();
    }

    [Fact]
    public void ConversationProjectionsHaveNoPublicConstructorOrWritableTrustSurface()
    {
        foreach (var type in new[] { typeof(DeepIdV2Conversation), typeof(DeepIdV2ConversationSnapshot),
            typeof(DeepIdV2ContactStartResult), typeof(DeepIdV2ContactOperationSnapshot), typeof(DeepIdV2PendingTextSnapshot) })
        {
            Assert.Empty(type.GetConstructors());
            Assert.All(type.GetProperties(), property => Assert.False(property.SetMethod?.IsPublic == true));
            Assert.DoesNotContain(type.GetProperties(), property => property.Name is "Scope" or "PrivateKey" or "Signer" or "Authority" or "IsVerified");
        }
        var properties = typeof(DeepIdV2Conversation).GetProperties();
        Assert.All(properties, property => Assert.Contains(property.PropertyType, new[] { typeof(string), typeof(bool) }));
    }

    [Fact]
    public async Task MissingAccountOrRuntimeRejectsBeforeAnyTransportOrAccountAccess()
    {
        await using var accounts = new UnavailableAccounts();
        var account = new DeepIdV2AccountViewModel(accounts);
        var transport = new ThrowingRuntime();
        var view = new DeepIdV2MessagingViewModel(accounts, account, transport);
        Assert.False(view.IsReady);
        Assert.False(view.StartContactCommand.CanExecute(null));
        Assert.False(view.AcceptContactCommand.CanExecute(null));
        Assert.False(view.SendTextCommand.CanExecute(null));
        await view.RefreshAsync();
        Assert.Equal(0, accounts.Calls); Assert.Equal(0, transport.Calls);
        Assert.NotNull(view.ErrorMessage); Assert.Empty(view.Conversations); Assert.Empty(view.Messages);
        Assert.DoesNotContain("PRIVATE", view.ErrorMessage);
        Assert.False(new DeepIdV2MessagingViewModel(accounts, account).HasRuntime);
    }

    [Fact]
    public async Task UnknownContactOutcomeRetainsIntentAndSanitizesErrorsWhileResetClosesCommands()
    {
        var directory = Path.Combine(Path.GetTempPath(), "deep-did2-messaging-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var accounts = new OwnedAccounts(directory);
            var account = new DeepIdV2AccountViewModel(accounts, new UiAdmission()) { DisplayName = "UI contract" };
            await account.CreateAccountAsync(); Assert.Null(account.ErrorMessage);
            await account.VerifyNetworkAsync(); Assert.True(account.IsNetworkVerified);
            var transport = new ThrowingRuntime();
            var view = new DeepIdV2MessagingViewModel(accounts, account, transport) { ContactAddress = "not an address" };
            Assert.True(view.IsReady);
            await view.StartContactAsync();
            Assert.Equal(0, transport.Calls); Assert.Contains("Проверьте", view.ErrorMessage);
            view.ContactAddress = account.Account!.PermanentId.CanonicalText;
            await view.StartContactAsync();
            var original = Assert.Single(transport.Intents);
            Assert.Equal(32, original.Length); Assert.Contains(original, value => value != 0);
            await view.StartContactAsync();
            Assert.Equal(original, transport.Intents[1]);
            Assert.DoesNotContain("PRIVATE", view.ErrorMessage);
            Assert.DoesNotContain(view.ContactAddress, view.ErrorMessage);
            Assert.Empty(view.Status); Assert.False(view.IsBusy);
            await view.RefreshAsync(); Assert.Empty(view.Conversations); Assert.Empty(view.Messages);
            Assert.False(view.CanAccept); Assert.False(view.CanSend);
            var calls = transport.Calls;
            await view.AcceptContactAsync(); await view.SendTextAsync();
            Assert.Equal(calls, transport.Calls);
            await account.ResetAccountAfterConfirmationAsync(); Assert.Null(account.ErrorMessage);
            Assert.False(view.IsReady); Assert.Null(view.Selected);
            Assert.Empty(view.ContactAddress); Assert.Empty(view.DraftText);
            Assert.False(view.RefreshCommand.CanExecute(null));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountInvalidationCancelsDelayedContactBeforeTransportEvenAfterReverification(bool reset)
    {
        var directory = Path.Combine(Path.GetTempPath(), "deep-did2-messaging-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var owned = new OwnedAccounts(directory);
            await using var accessor = new DelayedAccounts(owned);
            var account = new DeepIdV2AccountViewModel(accessor, new UiAdmission()) { DisplayName = "Before" };
            await account.CreateAccountAsync(); Assert.Null(account.ErrorMessage);
            await account.VerifyNetworkAsync(); Assert.True(account.IsNetworkVerified);
            var transport = new ThrowingRuntime();
            var view = new DeepIdV2MessagingViewModel(accessor, account, transport)
                { ContactAddress = account.Account!.PermanentId.CanonicalText };
            var owner = await owned.GetAccountsAsync();
            var delayed = accessor.DelayNextAccess();
            var starting = view.StartContactAsync();
            Assert.False(starting.IsCompleted);
            if (reset)
            {
                await account.ResetAccountAfterConfirmationAsync(); Assert.Null(account.ErrorMessage);
                account.DisplayName = "After";
                await account.CreateAccountAsync(); Assert.Null(account.ErrorMessage);
            }
            else await account.RefreshAsync();
            await account.VerifyNetworkAsync(); Assert.True(view.IsReady);
            // Deliberately ignore cancellation in the delayed platform lookup.
            // The resumed command must recheck before reading intent or dispatch.
            delayed.SetResult(owner);
            await starting;
            Assert.Equal(0, transport.Calls); Assert.Empty(transport.Intents);
            Assert.Empty(view.Conversations); Assert.Empty(view.Messages); Assert.Null(view.Selected);
            Assert.False(view.IsBusy); Assert.NotNull(view.ErrorMessage);
            Assert.DoesNotContain("PRIVATE", view.ErrorMessage);
            view.ContactAddress = account.Account!.PermanentId.CanonicalText;
            await view.StartContactAsync();
            Assert.Equal(1, transport.Calls); Assert.Single(transport.Intents);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class DelayedAccounts(IDeepIdV2AccountRuntimeAccessor inner) : IDeepIdV2AccountRuntimeAccessor
    {
        private TaskCompletionSource<DeepIdV2AccountService>? pending;
        public TaskCompletionSource<DeepIdV2AccountService> DelayNextAccess()
        {
            var result = new TaskCompletionSource<DeepIdV2AccountService>(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.Null(Interlocked.CompareExchange(ref pending, result, null));
            return result;
        }
        public Task<DeepIdV2AccountService> GetAccountsAsync(CancellationToken ct = default)
            => Interlocked.Exchange(ref pending, null)?.Task ?? inner.GetAccountsAsync(ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UnavailableAccounts : IDeepIdV2AccountRuntimeAccessor
    {
        public int Calls { get; private set; }
        public Task<DeepIdV2AccountService> GetAccountsAsync(CancellationToken ct = default)
        { Calls++; throw new InvalidOperationException("PRIVATE account unavailable"); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class OwnedAccounts : IDeepIdV2AccountRuntimeAccessor
    {
        private readonly InMemoryDeepSecureStorage storage = new();
        private readonly DeepIdV2AccountService owner;
        public OwnedAccounts(string directory) => owner = new(storage, directory,
            Enumerable.Range(1, 16).Select(value => (byte)value).ToArray(), 1,
            new FrozenClock(DateTimeOffset.FromUnixTimeSeconds(1_900_000_000)), DeepMlDsa65CandidateVerifierFactory.OpenForCurrentProcess);
        public Task<DeepIdV2AccountService> GetAccountsAsync(CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(owner); }
        public ValueTask DisposeAsync() { storage.Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class UiAdmission : IDeepIdV2NetworkAdmission
    {
        public Task VerifyAsync(DeepIdV2AccountService accounts, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    }
    private sealed class ThrowingRuntime : IDeepIdV2ConversationRuntime
    {
        public int Calls { get; private set; }
        public List<byte[]> Intents { get; } = [];
        private Exception Failure() { Calls++; return new IOException("PRIVATE payload key endpoint password"); }
        public Task<DeepIdV2ContactStartResult> StartAsync(DeepIdV2AccountService accounts, DeepPermanentIdV2 address, ReadOnlyMemory<byte> intent, CancellationToken ct = default)
        { Intents.Add(intent.ToArray()); throw Failure(); }
        public Task<IReadOnlyList<DeepIdV2ConversationSnapshot>> ListAsync(DeepIdV2AccountService accounts, CancellationToken ct = default) => throw Failure();
        public Task AcceptAsync(DeepIdV2AccountService accounts, DeepIdV2Conversation conversation, ReadOnlyMemory<byte> operation, CancellationToken ct = default) => throw Failure();
        public Task SendTextAsync(DeepIdV2AccountService accounts, DeepIdV2Conversation conversation, ReadOnlyMemory<byte> operation, string text, CancellationToken ct = default) => throw Failure();
        public Task<IReadOnlyList<DirectMessageCreateSnapshot>> MessagesAsync(DeepIdV2AccountService accounts, DeepIdV2Conversation conversation, CancellationToken ct = default) => throw Failure();
        public Task<DeepIdV2MailboxSynchronizationResult> SynchronizeAsync(DeepIdV2AccountService accounts, CancellationToken ct = default) => throw Failure();
    }
}
