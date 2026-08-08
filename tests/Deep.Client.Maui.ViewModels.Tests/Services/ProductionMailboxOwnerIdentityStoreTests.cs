using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Protocol.DeepExtension.MailboxAuthority;
using Microsoft.Maui.Storage;
using Microsoft.Data.Sqlite;
using Sodium;
using System.Diagnostics;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

[Collection("Secure recovery identity isolation")]
public sealed class ProductionMailboxOwnerIdentityStoreTests : IDisposable
{
    private const string FirstPhrase =
        "amaze buffet cake entrance symptoms tiger lamb maze nestle python dusted faxed faxed";
    private const string SecondPhrase =
        "update vague zinger boxes ornament renting glass gained island nabbing afield calamity nabbing";

    public ProductionMailboxOwnerIdentityStoreTests() =>
        ProductionMailboxOwnerIdentityStore.RemoveAsync().GetAwaiter().GetResult();

    [Fact]
    public async Task OwnerKeyIsStableAndSignsOnlyExactLocalOwnerPhp1()
    {
        var inner = new InMemorySessionStore();
        using var store = new SecureRecoverySessionStore(inner);
        var account = Account(FirstPhrase);
        await store.SetAsync(SessionAccountService.ActiveRecoveryPhraseKey, FirstPhrase);
        await store.SetAsync(SessionAccountService.ActiveAccountKey, account);
        using var first = await ProductionMailboxOwnerIdentityStore
            .LoadForAccountAsync(account.SessionId);
        using var second = await ProductionMailboxOwnerIdentityStore
            .LoadForAccountAsync(account.SessionId);
        var owner = first.GetPublicKey();
        Assert.Equal(owner, second.GetPublicKey());
        var holder = PublicKeyAuth.GenerateKeyPair(Bytes(40, 32));
        var input = Input(holder.PublicKey, owner);

        var signature = first.SignLocalOwnerProof(input);
        ProductionMailboxHolderProof.VerifyOwner(
            input,
            signature,
            new SodiumProductionMailboxHolderProofSignatureVerifier());

        Assert.Throws<InvalidOperationException>(() => first.SignLocalOwnerProof(
            input with { Intent = ProductionMailboxIssuanceIntent.PeerDeposit }));
        Assert.Throws<InvalidOperationException>(() => first.SignLocalOwnerProof(
            input with { MailboxOwnerEd25519PublicKey = Bytes(90, 32) }));
    }

    [Fact]
    public async Task CorruptStoredSeedFailsClosedWithoutSilentRouteRotation()
    {
        var account = Account(FirstPhrase);
        await SecureStorage.Default.SetAsync(
            ProductionMailboxOwnerIdentityStore.ActiveSlotKey, "a");
        await SecureStorage.Default.SetAsync(
            ProductionMailboxOwnerIdentityStore.SlotAKey,
            "not-a-canonical-owner-seed");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ProductionMailboxOwnerIdentityStore.LoadForAccountAsync(account.SessionId));
        Assert.Equal(
            "not-a-canonical-owner-seed",
            await SecureStorage.Default.GetAsync(
                ProductionMailboxOwnerIdentityStore.SlotAKey));
    }

    [Fact]
    public async Task AccountReplacementDeletesOwnerRouteIdentity()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), $"deep-owner-account-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var sqlite = new SqliteSessionStore(new SqliteSessionStoreOptions(
                Path.Combine(directory, "client-state.db"),
                new string('A', 64)));
            using var store = new SecureRecoverySessionStore(sqlite);
            var firstAccount = Account(FirstPhrase);
            await store.SetAsync(SessionAccountService.ActiveRecoveryPhraseKey, FirstPhrase);
            await store.SetAsync(SessionAccountService.ActiveAccountKey, firstAccount);
            using var original = await ProductionMailboxOwnerIdentityStore
                .LoadForAccountAsync(firstAccount.SessionId);
            var originalPublic = original.GetPublicKey();

            var secondAccount = Account(SecondPhrase);
            await store.SetAsync(SessionAccountService.ActiveRecoveryPhraseKey, SecondPhrase);
            await store.SetAsync(SessionAccountService.ActiveAccountKey, secondAccount);
            using var replacement = await ProductionMailboxOwnerIdentityStore
                .LoadForAccountAsync(secondAccount.SessionId);

            Assert.NotEqual(originalPublic, replacement.GetPublicKey());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CrashAfterStaging_KeepsDurableOldAccountAndOwnerIdentity()
    {
        var inner = new InMemorySessionStore();
        using (var initial = new SecureRecoverySessionStore(inner))
            await ActivateAsync(initial, FirstPhrase);
        var oldAccount = Account(FirstPhrase);
        byte[] oldOwner;
        using (var owner = await ProductionMailboxOwnerIdentityStore
            .LoadForAccountAsync(oldAccount.SessionId))
            oldOwner = owner.GetPublicKey();

        using (var faulted = new SecureRecoverySessionStore(
            inner,
            accountFaultInjector: point =>
            {
                if (point == ProductionMailboxOwnerIdentityStore.CommitFaultPoint.AfterStaging)
                    throw new InjectedAccountCommitFaultException();
            }))
        {
            await Assert.ThrowsAsync<InjectedAccountCommitFaultException>(() =>
                faulted.SetAsync(
                    SessionAccountService.ActiveRecoveryPhraseKey, SecondPhrase));
        }

        using var restarted = new SecureRecoverySessionStore(inner);
        Assert.Equal(FirstPhrase, await restarted.GetAsync<string>(
            SessionAccountService.ActiveRecoveryPhraseKey));
        using var recovered = await ProductionMailboxOwnerIdentityStore
            .LoadForAccountAsync(oldAccount.SessionId);
        Assert.Equal(oldOwner, recovered.GetPublicKey());
        Assert.Equal(oldAccount.SessionId,
            (await inner.GetAsync<SessionAccount>(SessionAccountService.ActiveAccountKey))!
                .SessionId);
    }

    [Fact]
    public async Task CrashAfterSlotWrite_RetryClaimsSameReservationWithoutOverwrite()
    {
        await Assert.ThrowsAsync<InjectedAccountCommitFaultException>(() =>
            ProductionMailboxOwnerIdentityStore.StageAccountAsync(
                FirstPhrase,
                point =>
                {
                    if (point == ProductionMailboxOwnerIdentityStore.CommitFaultPoint.AfterStagingSlot)
                        throw new InjectedAccountCommitFaultException();
                }));
        Assert.Null(await SecureStorage.Default.GetAsync(
            ProductionMailboxOwnerIdentityStore.ReservationKey));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProductionMailboxOwnerIdentityStore.StageAccountAsync(SecondPhrase));

        await ProductionMailboxOwnerIdentityStore.StageAccountAsync(FirstPhrase);
        Assert.NotNull(await SecureStorage.Default.GetAsync(
            ProductionMailboxOwnerIdentityStore.ReservationKey));
        var inner = new InMemorySessionStore();
        var first = Account(FirstPhrase);
        await ProductionMailboxOwnerIdentityStore.CommitStagedAccountAsync(
            first.SessionId,
            () => inner.SetAsync(SessionAccountService.ActiveAccountKey, first));
        Assert.Equal(first, await inner.GetAsync<SessionAccount>(
            SessionAccountService.ActiveAccountKey));
    }

    [Theory]
    [InlineData((int)ProductionMailboxOwnerIdentityStore.CommitFaultPoint.AfterActivation)]
    [InlineData((int)ProductionMailboxOwnerIdentityStore.CommitFaultPoint.AfterCleanup)]
    public async Task CrashAfterDurableAccountCommit_StartupSelectsMatchingNewSlot(
        int faultPointValue)
    {
        var faultPoint = (ProductionMailboxOwnerIdentityStore.CommitFaultPoint)faultPointValue;
        var inner = new InMemorySessionStore();
        using (var initial = new SecureRecoverySessionStore(inner))
        {
            await ActivateAsync(initial, FirstPhrase);
            await initial.SetAsync(
                SessionAccountService.ActiveRecoveryPhraseKey, SecondPhrase);
        }
        var newAccount = Account(SecondPhrase);
        using (var faulted = new SecureRecoverySessionStore(
            inner,
            accountFaultInjector: point =>
            {
                if (point == faultPoint) throw new InjectedAccountCommitFaultException();
            }))
        {
            await Assert.ThrowsAsync<InjectedAccountCommitFaultException>(() =>
                faulted.SetAsync(SessionAccountService.ActiveAccountKey, newAccount));
        }

        Assert.Equal(newAccount.SessionId,
            (await inner.GetAsync<SessionAccount>(SessionAccountService.ActiveAccountKey))!
                .SessionId);
        using var restarted = new SecureRecoverySessionStore(inner);
        Assert.Equal(SecondPhrase, await restarted.GetAsync<string>(
            SessionAccountService.ActiveRecoveryPhraseKey));
        using var owner = await ProductionMailboxOwnerIdentityStore
            .LoadForAccountAsync(newAccount.SessionId);
        Assert.Equal(32, owner.GetPublicKey().Length);
    }

    [Fact]
    public async Task AccountWithoutStagedIdentityNeverBecomesDurable()
    {
        var inner = new InMemorySessionStore();
        using var store = new SecureRecoverySessionStore(inner);
        await ActivateAsync(store, FirstPhrase);
        var first = Account(FirstPhrase);
        var second = Account(SecondPhrase);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SetAsync(SessionAccountService.ActiveAccountKey, second));

        Assert.Equal(first, await inner.GetAsync<SessionAccount>(
            SessionAccountService.ActiveAccountKey));
        Assert.Equal(FirstPhrase, await store.GetAsync<string>(
            SessionAccountService.ActiveRecoveryPhraseKey));
    }

    [Fact]
    public async Task ConcurrentRemoveWaitsForStageAndLeavesNoSplitSlotState()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stage = Task.Run(() =>
            ProductionMailboxOwnerIdentityStore.StageAccountAsync(
                FirstPhrase,
                point =>
                {
                    if (point != ProductionMailboxOwnerIdentityStore.CommitFaultPoint.AfterStaging)
                        return;
                    entered.TrySetResult();
                    release.Task.GetAwaiter().GetResult();
                }));
        await entered.Task;
        var remove = ProductionMailboxOwnerIdentityStore.RemoveAsync();
        Assert.False(remove.IsCompleted);
        release.TrySetResult();
        await Task.WhenAll(stage, remove);

        Assert.Null(await SecureStorage.Default.GetAsync(
            ProductionMailboxOwnerIdentityStore.ActiveSlotKey));
        Assert.Null(await SecureStorage.Default.GetAsync(
            ProductionMailboxOwnerIdentityStore.SlotAKey));
        Assert.Null(await SecureStorage.Default.GetAsync(
            ProductionMailboxOwnerIdentityStore.SlotBKey));
    }

    [Fact]
    public async Task DifferentAccountCannotOverwriteDurableStagingReservation()
    {
        await ProductionMailboxOwnerIdentityStore.StageAccountAsync(FirstPhrase);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProductionMailboxOwnerIdentityStore.StageAccountAsync(SecondPhrase));

        var inner = new InMemorySessionStore();
        var second = Account(SecondPhrase);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProductionMailboxOwnerIdentityStore.CommitStagedAccountAsync(
                second.SessionId,
                () => inner.SetAsync(SessionAccountService.ActiveAccountKey, second)));
        Assert.Null(await inner.GetAsync<SessionAccount>(
            SessionAccountService.ActiveAccountKey));

        var first = Account(FirstPhrase);
        await ProductionMailboxOwnerIdentityStore.CommitStagedAccountAsync(
            first.SessionId,
            () => inner.SetAsync(SessionAccountService.ActiveAccountKey, first));
        Assert.Equal(first, await inner.GetAsync<SessionAccount>(
            SessionAccountService.ActiveAccountKey));
        Assert.Null(await SecureStorage.Default.GetAsync(
            ProductionMailboxOwnerIdentityStore.ReservationKey));
    }

    [Fact]
    public async Task RecommitOfActiveAccountCannotDiscardAnotherAccountReservation()
    {
        var inner = new InMemorySessionStore();
        using var store = new SecureRecoverySessionStore(inner);
        await ActivateAsync(store, FirstPhrase);
        var first = Account(FirstPhrase);
        await ProductionMailboxOwnerIdentityStore.StageAccountAsync(SecondPhrase);
        var reservation = await SecureStorage.Default.GetAsync(
            ProductionMailboxOwnerIdentityStore.ReservationKey);

        await ProductionMailboxOwnerIdentityStore.CommitStagedAccountAsync(
            first.SessionId,
            () => inner.SetAsync(SessionAccountService.ActiveAccountKey, first));

        Assert.Equal(reservation, await SecureStorage.Default.GetAsync(
            ProductionMailboxOwnerIdentityStore.ReservationKey));
        var second = Account(SecondPhrase);
        await ProductionMailboxOwnerIdentityStore.CommitStagedAccountAsync(
            second.SessionId,
            () => inner.SetAsync(SessionAccountService.ActiveAccountKey, second));
        Assert.Equal(second, await inner.GetAsync<SessionAccount>(
            SessionAccountService.ActiveAccountKey));
        Assert.Null(await SecureStorage.Default.GetAsync(
            ProductionMailboxOwnerIdentityStore.ReservationKey));
    }

    [Fact]
    public async Task CrossProcessFileLockBlocksIndependentChildProcessOwner()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(
            Path.GetTempPath(), $"deep-owner-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "owner.lock");
        var script = "& { $p=$args[0]; " +
            "$s=[System.IO.File]::Open($p,[System.IO.FileMode]::OpenOrCreate," +
            "[System.IO.FileAccess]::ReadWrite,[System.IO.FileShare]::None); " +
            "[Console]::Out.WriteLine('locked'); [Console]::Out.Flush(); " +
            "[Console]::In.ReadLine() | Out-Null; $s.Dispose() }";
        using var child = new Process
        {
            StartInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        child.StartInfo.ArgumentList.Add("-NoProfile");
        child.StartInfo.ArgumentList.Add("-NonInteractive");
        child.StartInfo.ArgumentList.Add("-Command");
        child.StartInfo.ArgumentList.Add(script);
        child.StartInfo.ArgumentList.Add(path);
        try
        {
            Assert.True(child.Start());
            Assert.Equal("locked", await child.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(10)));
            var acquisition = ProductionMailboxOwnerIdentityStore
                .AcquireCrossProcessLockForTestAsync(path);
            await Task.Delay(150);
            Assert.False(acquisition.IsCompleted);

            await child.StandardInput.WriteLineAsync("release");
            await child.StandardInput.FlushAsync();
            using var acquired = await acquisition.WaitAsync(TimeSpan.FromSeconds(10));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, child.ExitCode);
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData((int)ProductionMailboxOwnerIdentityStore.CommitFaultPoint.AfterDeletionTombstone)]
    [InlineData((int)ProductionMailboxOwnerIdentityStore.CommitFaultPoint.AfterDeletionDurableMutation)]
    [InlineData((int)ProductionMailboxOwnerIdentityStore.CommitFaultPoint.AfterDeletionPointerRemoval)]
    [InlineData((int)ProductionMailboxOwnerIdentityStore.CommitFaultPoint.AfterDeletionFirstSlotRemoval)]
    [InlineData((int)ProductionMailboxOwnerIdentityStore.CommitFaultPoint.AfterDeletionSecondSlotRemoval)]
    public async Task InterruptedDeletionTombstonePreventsIdentityResurrection(
        int faultPointValue)
    {
        var faultPoint = (ProductionMailboxOwnerIdentityStore.CommitFaultPoint)faultPointValue;
        var inner = new InMemorySessionStore();
        using (var initial = new SecureRecoverySessionStore(inner))
            await ActivateAsync(initial, FirstPhrase);
        using (var faulted = new SecureRecoverySessionStore(
            inner,
            accountFaultInjector: point =>
            {
                if (point == faultPoint) throw new InjectedAccountCommitFaultException();
            }))
        {
            await Assert.ThrowsAsync<InjectedAccountCommitFaultException>(() =>
                faulted.DeleteAsync(SessionAccountService.ActiveRecoveryPhraseKey));
        }
        Assert.Equal("delete-all-v2", await SecureStorage.Default.GetAsync(
            ProductionMailboxOwnerIdentityStore.DeletionTombstoneKey));

        using var restarted = new SecureRecoverySessionStore(inner);
        Assert.Null(await restarted.GetAsync<string>(
            SessionAccountService.ActiveRecoveryPhraseKey));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProductionMailboxOwnerIdentityStore.LoadForAccountAsync(
                Account(FirstPhrase).SessionId));
        Assert.Null(await SecureStorage.Default.GetAsync(
            ProductionMailboxOwnerIdentityStore.ActiveSlotKey));
        Assert.Null(await SecureStorage.Default.GetAsync(
            ProductionMailboxOwnerIdentityStore.SlotAKey));
        Assert.Null(await SecureStorage.Default.GetAsync(
            ProductionMailboxOwnerIdentityStore.SlotBKey));
        Assert.Null(await SecureStorage.Default.GetAsync(
            ProductionMailboxOwnerIdentityStore.DeletionTombstoneKey));
        Assert.Null(await SecureStorage.Default.GetAsync(
            ProductionMailboxOwnerIdentityStore.ReservationKey));
    }

    public void Dispose() =>
        ProductionMailboxOwnerIdentityStore.RemoveAsync().GetAwaiter().GetResult();

    private static SessionAccount Account(string phrase)
    {
        using var identity = new SessionIdentityProvider(phrase);
        return new SessionAccount(
            identity.SessionId, "Mr. X", DateTimeOffset.Parse("2026-08-08T00:00:00Z"));
    }

    private static async Task ActivateAsync(
        SecureRecoverySessionStore store, string phrase)
    {
        await store.SetAsync(SessionAccountService.ActiveRecoveryPhraseKey, phrase);
        await store.SetAsync(SessionAccountService.ActiveAccountKey, Account(phrase));
    }

    private sealed class InjectedAccountCommitFaultException : Exception;

    private static ProductionMailboxHolderProofInput Input(
        byte[] holder, byte[] owner) => new()
    {
        Intent = ProductionMailboxIssuanceIntent.LocalOwner,
        Platform = ProductionMailboxClientPlatform.Android,
        NetworkId = Bytes(1, 16),
        CanonicalAuthorityHash = Bytes(20, 32),
        HolderEd25519PublicKey = holder,
        MailboxOwnerEd25519PublicKey = owner,
        BlindedMailboxId = new byte[32],
        BlindedPlacementId = new byte[32],
        SelectionInputCommitment = new byte[32],
        SigningCertificateSha256 = Bytes(120, 32),
        BuildArtifactSha256 = Bytes(140, 32),
        IdempotencyKey = Bytes(160, 32),
        EntitlementCommitment = new byte[32],
        ChallengeId = Bytes(180, 16),
        Challenge = Bytes(200, 32),
        ProofOfWorkNonce = 7
    };

    private static byte[] Bytes(byte seed, int length) =>
        Enumerable.Range(0, length)
            .Select(index => unchecked((byte)(seed + index)))
            .ToArray();
}

[CollectionDefinition("Secure recovery identity isolation", DisableParallelization = true)]
public sealed class SecureRecoveryIdentityIsolationCollection;
