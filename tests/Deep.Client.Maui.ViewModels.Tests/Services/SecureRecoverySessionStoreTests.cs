using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Microsoft.Maui.Storage;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

[Collection("Secure recovery identity isolation")]
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
    public async Task LogicalDispatchPlan_IsForwardedWithExactRetryAndConflictSemantics()
    {
        using var store = new SecureRecoverySessionStore(new InMemorySessionStore());
        var sender = SessionId.CreateNew();
        var recipient = SessionId.CreateNew();
        var plan = new DurableLogicalDispatchPlan(
            sender,
            new MessageId("durable-plan-message"),
            DurableLogicalDispatchKind.DirectMessage,
            [new DurableLogicalDispatchTarget(
                recipient,
                new MessageId("durable-plan-wire"),
                DurableLogicalDispatchRoute.DirectP2p,
                ReadOnlyMemory<byte>.Empty)],
            DateTimeOffset.Parse("2026-07-11T00:00:00Z"));

        await store.EnsureLogicalDispatchPlanAsync(plan);
        await store.EnsureLogicalDispatchPlanAsync(plan);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.EnsureLogicalDispatchPlanAsync(plan with
            {
                Targets = [new DurableLogicalDispatchTarget(
                    recipient,
                    new MessageId("changed-wire"),
                    DurableLogicalDispatchRoute.DirectP2p,
                    ReadOnlyMemory<byte>.Empty)]
            }));
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
        const string phrase =
            "amaze buffet cake entrance symptoms tiger lamb maze nestle python dusted faxed faxed";
        using var identity = new SessionIdentityProvider(phrase);
        var account = new SessionAccount(
            identity.SessionId, "Alice", DateTimeOffset.Parse("2026-05-28T00:00:00Z"));

        await store.SetAsync(SessionAccountService.ActiveRecoveryPhraseKey, phrase);
        await store.SetAsync(LocalSettingsKeys.ActiveAccount, account);

        await store.PurgeAccountDataAsync();

        Assert.Null(await store.GetAsync<SessionAccount>(LocalSettingsKeys.ActiveAccount));
        Assert.Null(await store.GetAsync<string>(SessionAccountService.ActiveRecoveryPhraseKey));
        Assert.Null(await inner.GetAsync<string>(SessionAccountService.ActiveRecoveryPhraseKey));
    }

    [Fact]
    public async Task PurgeFailureAfterDeletionIntentFailsClosedWithoutIdentityResurrection()
    {
        var inner = new InMemorySessionStore();
        var proxyStore = DispatchProxy.Create<ILocalSessionStore, FailingPurgeStoreProxy>();
        ((FailingPurgeStoreProxy)(object)proxyStore).Inner = inner;
        using var store = new SecureRecoverySessionStore(proxyStore);
        const string phrase = "amaze buffet cake entrance symptoms tiger lamb maze nestle python dusted faxed faxed";
        try
        {
            await store.SetAsync(SessionAccountService.ActiveRecoveryPhraseKey, phrase);
            using var identity = new SessionIdentityProvider(phrase);
            await store.SetAsync(SessionAccountService.ActiveAccountKey,
                new SessionAccount(identity.SessionId, "Alice", DateTimeOffset.UtcNow));

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.PurgeAccountDataAsync());

            Assert.Null(await store.GetAsync<string>(SessionAccountService.ActiveRecoveryPhraseKey));
        }
        finally
        {
            await store.DeleteAsync(SessionAccountService.ActiveRecoveryPhraseKey);
        }
    }

    [Fact]
    public async Task RecoveryDeleteFailureAfterDeletionIntentFailsClosed()
    {
        var inner = new InMemorySessionStore();
        var proxyStore = DispatchProxy.Create<ILocalSessionStore, FailingDeleteStoreProxy>();
        var proxy = (FailingDeleteStoreProxy)(object)proxyStore;
        proxy.Inner = inner;
        using var store = new SecureRecoverySessionStore(proxyStore);
        const string phrase =
            "amaze buffet cake entrance symptoms tiger lamb maze nestle python dusted faxed faxed";
        using var identity = new SessionIdentityProvider(phrase);
        var account = new SessionAccount(identity.SessionId, "Alice", DateTimeOffset.UtcNow);
        await store.SetAsync(SessionAccountService.ActiveRecoveryPhraseKey, phrase);
        await store.SetAsync(SessionAccountService.ActiveAccountKey, account);
        proxy.FailSettingsDelete = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DeleteAsync(SessionAccountService.ActiveRecoveryPhraseKey));

        Assert.Equal(account, await inner.GetAsync<SessionAccount>(
            SessionAccountService.ActiveAccountKey));
        Assert.Null(await store.GetAsync<string>(
            SessionAccountService.ActiveRecoveryPhraseKey));
    }

    [Fact]
    public async Task PurgeAndConcurrentStageAreLinearizedAcrossDatabaseAndSecureSlots()
    {
        var inner = new InMemorySessionStore();
        var proxyStore = DispatchProxy.Create<ILocalSessionStore, BlockingPurgeStoreProxy>();
        var proxy = (BlockingPurgeStoreProxy)(object)proxyStore;
        proxy.Inner = inner;
        using var store = new SecureRecoverySessionStore(proxyStore);
        const string firstPhrase =
            "amaze buffet cake entrance symptoms tiger lamb maze nestle python dusted faxed faxed";
        const string secondPhrase =
            "update vague zinger boxes ornament renting glass gained island nabbing afield calamity nabbing";
        using (var first = new SessionIdentityProvider(firstPhrase))
        {
            await store.SetAsync(SessionAccountService.ActiveRecoveryPhraseKey, firstPhrase);
            await store.SetAsync(SessionAccountService.ActiveAccountKey,
                new SessionAccount(first.SessionId, "First", DateTimeOffset.UtcNow));
        }

        var purge = store.PurgeAccountDataAsync();
        await proxy.PurgeEntered.Task;
        var stage = store.SetAsync(SessionAccountService.ActiveRecoveryPhraseKey, secondPhrase);
        Assert.False(stage.IsCompleted);
        proxy.ReleasePurge.TrySetResult();
        await Task.WhenAll(purge, stage);

        Assert.Null(await inner.GetAsync<SessionAccount>(
            SessionAccountService.ActiveAccountKey));
        using var second = new SessionIdentityProvider(secondPhrase);
        await store.SetAsync(SessionAccountService.ActiveAccountKey,
            new SessionAccount(second.SessionId, "Second", DateTimeOffset.UtcNow));
        Assert.Equal(secondPhrase, await store.GetAsync<string>(
            SessionAccountService.ActiveRecoveryPhraseKey));
    }

    [Fact]
    public async Task MissingSecurePhraseDoesNotReadOrLiftInnerStorePhrase()
    {
        const string secureKey = "deep.account.recovery-phrase.v1";
        const string phrase =
            "amaze buffet cake entrance symptoms tiger lamb maze nestle python dusted faxed faxed";
        SecureStorage.Remove(secureKey);
        var inner = new InMemorySessionStore();
        await inner.SetAsync(SessionAccountService.ActiveRecoveryPhraseKey, phrase);
        using var store = new SecureRecoverySessionStore(inner);

        Assert.Null(
            await store.GetAsync<string>(SessionAccountService.ActiveRecoveryPhraseKey));
        Assert.Equal(
            phrase,
            await inner.GetAsync<string>(SessionAccountService.ActiveRecoveryPhraseKey));
        Assert.Null(await SecureStorage.GetAsync(secureKey));
    }

    [Fact]
    public async Task LegacySecurePhraseIsRejectedWithoutFallingBackToInnerStore()
    {
        const string secureKey = "deep.account.recovery-phrase.v1";
        const string legacy =
            "amber anchor april arrow atom aurora autumn badge bamboo beacon berry blade";
        SecureStorage.Remove(secureKey);
        try
        {
            await SecureStorage.SetAsync(secureKey, legacy);
            var inner = new InMemorySessionStore();
            using var store = new SecureRecoverySessionStore(inner);

            Assert.Null(
                await store.GetAsync<string>(SessionAccountService.ActiveRecoveryPhraseKey));
        }
        finally
        {
            SecureStorage.Remove(secureKey);
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

    public class FailingDeleteStoreProxy : DispatchProxy
    {
        public ILocalSessionStore Inner { get; set; } = null!;
        public bool FailSettingsDelete { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (FailSettingsDelete && targetMethod.Name == nameof(ISettingsRepository.DeleteAsync) &&
                args is [string key, ..] &&
                key == SessionAccountService.ActiveRecoveryPhraseKey)
                return Task.FromException(new InvalidOperationException(
                    "Injected settings delete failure."));
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

    public class BlockingPurgeStoreProxy : DispatchProxy
    {
        public ILocalSessionStore Inner { get; set; } = null!;
        public TaskCompletionSource PurgeEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleasePurge { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(IAccountDataPurger.PurgeAccountDataAsync))
                return PurgeAsync((CancellationToken)args![0]!);
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

        private async Task PurgeAsync(CancellationToken cancellationToken)
        {
            PurgeEntered.TrySetResult();
            await ReleasePurge.Task.WaitAsync(cancellationToken);
            await ((IAccountDataPurger)Inner).PurgeAccountDataAsync(cancellationToken);
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
