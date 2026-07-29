using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class SecureRecoverySessionStoreTests
{
    [Fact]
    public async Task AtomicBoundedSettingsOperations_AreForwardedWithCompareAndExchangeSemantics()
    {
        using var store = new SecureRecoverySessionStore(new InMemorySessionStore());
        const string key = "p14.native-composition";
        const int maximumBytes = 128;
        var firstValue = "{\"generation\":1}"u8.ToArray();
        var secondValue = "{\"generation\":2}"u8.ToArray();

        Assert.Equal(
            AtomicBoundedSettingMutationResult.Applied,
            await store.CreateAtomicBoundedSettingAsync(key, firstValue, maximumBytes));

        var first = await store.ReadAtomicBoundedSettingAsync(key, maximumBytes);
        Assert.Equal(AtomicBoundedSettingReadResult.Found, first.Result);
        Assert.Equal(firstValue, first.GetValueCopy());
        Assert.NotNull(first.Revision);

        Assert.Equal(
            AtomicBoundedSettingMutationResult.Applied,
            await store.ReplaceAtomicBoundedSettingAsync(
                key,
                first.Revision!,
                secondValue,
                maximumBytes));
        Assert.Equal(
            AtomicBoundedSettingMutationResult.Conflict,
            await store.DeleteAtomicBoundedSettingAsync(
                key,
                first.Revision!,
                maximumBytes));

        var second = await store.ReadAtomicBoundedSettingAsync(key, maximumBytes);
        Assert.Equal(AtomicBoundedSettingReadResult.Found, second.Result);
        Assert.Equal(secondValue, second.GetValueCopy());
        Assert.NotNull(second.Revision);
        Assert.Equal(
            AtomicBoundedSettingMutationResult.Applied,
            await store.DeleteAtomicBoundedSettingAsync(
                key,
                second.Revision!,
                maximumBytes));
        Assert.Equal(
            AtomicBoundedSettingReadResult.Missing,
            (await store.ReadAtomicBoundedSettingAsync(key, maximumBytes)).Result);
    }

    [Fact]
    public async Task DurableInboxOperations_AreForwardedWithoutLosingCursorOrItems()
    {
        using var store = new SecureRecoverySessionStore(new InMemorySessionStore());
        var scope = new DurableInboxScope(SessionId.CreateNew(), 0);
        var entry = DurableInboxWireEntry.Create("server-hash", 42, "wire-payload");

        var staged = await store.StageInboxBatchAsync(scope, null, entry.ServerHash, [entry]);

        Assert.Equal(1, staged.StagedCount);
        Assert.Equal(entry.ServerHash, await store.GetInboxCursorAsync(scope));
        Assert.Equal(entry.ServerHash, Assert.Single(await store.ListStagedInboxItemsAsync(scope, 10)).ServerHash);
        Assert.Equal(1, await store.CountPendingInboxItemsAsync(scope));

        await store.DiscardInboxItemAsync(scope, entry.ServerHash);

        Assert.Equal(0, await store.CountPendingInboxItemsAsync(scope));
    }

    [Fact]
    public async Task BulkInboxCleanup_IsForwardedOnceWithoutUsingTheDefaultPerItemFallback()
    {
        var proxyStore = DispatchProxy.Create<ILocalSessionStore, TrackingInboxStoreProxy>();
        var tracker = (TrackingInboxStoreProxy)(object)proxyStore;
        using var store = new SecureRecoverySessionStore(proxyStore);
        var scope = new DurableInboxScope(SessionId.CreateNew(), 0);
        var retainedRoutes = new HashSet<string>(StringComparer.Ordinal)
        {
            ConversationId.CreateGroupV2().Value
        };

        var discarded = await store.DiscardDecodedInboxItemsOutsideRoutesAsync(
            scope,
            DurableInboxItemKind.GroupMessage,
            retainedRoutes);

        Assert.Equal(300, discarded);
        Assert.Equal(1, tracker.BulkDiscardCalls);
        Assert.Equal(0, tracker.ListDecodedCalls);
        Assert.Equal(0, tracker.PerItemDiscardCalls);
    }

    [Fact]
    public async Task PurgeAccountData_RemovesSecureRecoveryMaterial()
    {
        var inner = new InMemorySessionStore();
        using var store = new SecureRecoverySessionStore(inner);
        var account = new SessionAccount(SessionId.CreateNew(), "Alice", DateTimeOffset.Parse("2026-05-28T00:00:00Z"));

        await store.SetAsync(LocalSettingsKeys.ActiveAccount, account);
        await store.SetAsync(SessionAccountService.ActiveRecoveryPhraseKey,
            "amber anchor april arrow atom aurora autumn badge bamboo beacon berry blade");

        await store.PurgeAccountDataAsync();

        Assert.Null(await store.GetAsync<SessionAccount>(LocalSettingsKeys.ActiveAccount));
        Assert.Null(await store.GetAsync<string>(SessionAccountService.ActiveRecoveryPhraseKey));
        Assert.Null(await inner.GetAsync<string>(SessionAccountService.ActiveRecoveryPhraseKey));
    }

    [Fact]
    public async Task PurgeAccountData_KeepsRecoveryMaterialWhenDatabasePurgeFails()
    {
        var inner = new InMemorySessionStore();
        var proxyStore = DispatchProxy.Create<ILocalSessionStore, FailingPurgeStoreProxy>();
        ((FailingPurgeStoreProxy)(object)proxyStore).Inner = inner;
        using var store = new SecureRecoverySessionStore(proxyStore);
        const string phrase = "amber anchor april arrow atom aurora autumn badge bamboo beacon berry blade";
        try
        {
            await store.SetAsync(SessionAccountService.ActiveRecoveryPhraseKey, phrase);

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.PurgeAccountDataAsync());

            Assert.Equal(phrase, await store.GetAsync<string>(SessionAccountService.ActiveRecoveryPhraseKey));
        }
        finally
        {
            await store.DeleteAsync(SessionAccountService.ActiveRecoveryPhraseKey);
        }
    }

    public class FailingPurgeStoreProxy : DispatchProxy
    {
        public ILocalSessionStore Inner { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(IAccountDataPurger.PurgeAccountDataAsync))
            {
                return Task.FromException(new InvalidOperationException("Injected database purge failure."));
            }

            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                return null;
            }
        }
    }

    public class TrackingInboxStoreProxy : DispatchProxy
    {
        public int BulkDiscardCalls { get; private set; }

        public int ListDecodedCalls { get; private set; }

        public int PerItemDiscardCalls { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(IDurableInboxRepository.DiscardDecodedInboxItemsOutsideRoutesAsync))
            {
                BulkDiscardCalls++;
                return Task.FromResult(300);
            }

            if (targetMethod.Name == nameof(IDurableInboxRepository.ListDecodedInboxItemsAsync))
            {
                ListDecodedCalls++;
                throw new InvalidOperationException("The default bulk cleanup fallback was invoked.");
            }

            if (targetMethod.Name == nameof(IDurableInboxRepository.DiscardInboxItemAsync))
            {
                PerItemDiscardCalls++;
                throw new InvalidOperationException("Per-item inbox cleanup was invoked.");
            }

            throw new NotSupportedException($"Unexpected store operation: {targetMethod.Name}");
        }
    }
}
