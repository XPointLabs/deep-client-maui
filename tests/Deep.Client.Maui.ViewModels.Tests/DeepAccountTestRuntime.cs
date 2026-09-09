using System.Security.Cryptography;
using System.Text;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.ViewModels.Tests;

internal sealed class DeepAccountTestRuntime : IDeepAccountRuntimeAccessor
{
    private static readonly byte[] NetworkId = Enumerable.Range(1, 16).Select(static value => (byte)value).ToArray();
    private readonly InMemoryDeepAccountStore store = new();
    private readonly InMemoryDeepSecureStorage secureStorage = new();

    internal DeepAccountTestRuntime()
    {
        Accounts = new DeepAccountService(
            store,
            secureStorage,
            new FrozenClock(DateTimeOffset.Parse("2026-09-07T00:00:00Z")),
            NetworkId);
    }

    internal DeepAccountService Accounts { get; }

    internal int AccessCalls { get; private set; }

    public Task<DeepAccountService> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AccessCalls++;
        return Task.FromResult(Accounts);
    }

    public Task ResetLocalStateAsync(CancellationToken cancellationToken = default) =>
        Accounts.ResetLocalAccountAsync(cancellationToken);

    internal async Task<(DeepAccountCreationResult Result, string RecoveryPhrase)> CreateAsync(
        string displayName = "Alice")
    {
        using var draft = Accounts.PrepareCreate(displayName);
        string? phrase = null;
        draft.RevealCanonicalPhraseOnce(bytes => phrase = Encoding.UTF8.GetString(bytes));
        byte[]? confirmationUtf8 = null;
        try
        {
            confirmationUtf8 = Encoding.UTF8.GetBytes(phrase!);
            using var confirmation = DeepOwnedRecoveryPhraseUtf8.CopyFrom(confirmationUtf8);
            var result = await Accounts.CommitPreparedAsync(draft, confirmation);
            return (result, phrase!);
        }
        finally
        {
            if (confirmationUtf8 is not null)
            {
                CryptographicOperations.ZeroMemory(confirmationUtf8);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await store.DisposeAsync();
        secureStorage.Dispose();
    }
}
