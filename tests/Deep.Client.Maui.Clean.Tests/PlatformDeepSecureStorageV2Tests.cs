using System.Security.Cryptography;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Protocol.ApplicationCore;

namespace Deep.Client.Maui.Clean.Tests;

public sealed class PlatformDeepSecureStorageV2Tests
{
    [Fact]
    public async Task WindowsPlatformV2StoreResumesExactDid2AndPhraseDeletion()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(),
            "deep-platform-did2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var network = Enumerable.Range(1, 16).Select(static value =>
                (byte)value).ToArray();
            var clock = new FrozenClock(
                DateTimeOffset.FromUnixTimeSeconds(1_900_000_000));
            var privateDirectory = Path.Combine(root, "deep-store-v2");
            string exactDeepId;
            using (var firstStore = PlatformDeepSecureStorage.CreateV2(root))
            {
                var first = new DeepIdV2AccountService(firstStore,
                    privateDirectory, network, 1, clock,
                    DeepMlDsa65CandidateVerifierFactory.OpenForCurrentProcess);
                var created = await first.CreateAsync(" Alice ");
                exactDeepId = created.PermanentId.CanonicalText;
                using var phrase = await first.ReadRetainedRecoveryPhraseAsync();
                Assert.NotNull(phrase);
            }
            using (var reopenedStore = PlatformDeepSecureStorage.CreateV2(root))
            {
                var reopened = new DeepIdV2AccountService(reopenedStore,
                    privateDirectory, network, 1, clock,
                    DeepMlDsa65CandidateVerifierFactory.OpenForCurrentProcess);
                Assert.Equal(exactDeepId,
                    (await reopened.GetCurrentAsync())!.PermanentId.CanonicalText);
                await reopened.DeleteRetainedRecoveryPhraseAsync();
                Assert.Null(await reopened.ReadRetainedRecoveryPhraseAsync());
            }
            using (var finalStore = PlatformDeepSecureStorage.CreateV2(root))
            {
                var final = new DeepIdV2AccountService(finalStore,
                    privateDirectory, network, 1, clock,
                    DeepMlDsa65CandidateVerifierFactory.OpenForCurrentProcess);
                Assert.Equal(exactDeepId,
                    (await final.GetCurrentAsync())!.PermanentId.CanonicalText);
                Assert.Null(await final.ReadRetainedRecoveryPhraseAsync());
            }
        }
        finally { DeleteOwnFiles(root); }
    }

    [Fact]
    public async Task V2HasIndependentProtectedFileAndKeyDomain()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "deep-platform-store-v2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var v1 = PlatformDeepSecureStorage.Create(root);
            using var v2 = PlatformDeepSecureStorage.CreateV2(root);
            Assert.True(Directory.Exists(Path.Combine(root, "deep-store-v1")));
            Assert.True(Directory.Exists(Path.Combine(root, "deep-store-v2")));
            if (!OperatingSystem.IsWindows()) return;

            await v1.WriteBatchAsync(
                [new DeepSecureStorageWrite("deep.store.v1.keep", new byte[] { 1 })]);
            await v2.WriteBatchAsync(
                [new DeepSecureStorageWrite("deep.store.v2.keep", new byte[] { 2 })]);
            Assert.True(File.Exists(Path.Combine(root, "deep-store-v1",
                "secure-storage.dss")));
            Assert.True(File.Exists(Path.Combine(root, "deep-store-v2",
                "secure-storage.dss")));
            using (var oldValue = await v1.ReadOwnedAsync("deep.store.v1.keep"))
                Assert.NotNull(oldValue);
            using (var newValue = await v2.ReadOwnedAsync("deep.store.v2.keep"))
                Assert.NotNull(newValue);
            Assert.Null(await v1.ReadOwnedAsync("deep.store.v2.keep"));
            Assert.Null(await v2.ReadOwnedAsync("deep.store.v1.keep"));

            var oldProtector = new PlatformDeepSecretProtector();
            var newProtector = new PlatformDeepSecretProtector(useV2: true);
            var ciphertext = newProtector.Protect(new byte[] { 3 });
            try
            {
                var rejected = false;
                try
                {
                    var unexpected = oldProtector.Unprotect(ciphertext);
                    CryptographicOperations.ZeroMemory(unexpected);
                }
                catch (CryptographicException) { rejected = true; }
                Assert.True(rejected);
            }
            finally { CryptographicOperations.ZeroMemory(ciphertext); }
        }
        finally
        {
            DeleteOwnFiles(root);
        }
    }

    private static void DeleteOwnFiles(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var generation in new[] { "deep-store-v1", "deep-store-v2" })
        {
            var directory = Path.Combine(root, generation);
            if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory))
                File.Delete(file);
            Directory.Delete(directory);
        }
        Directory.Delete(root);
    }
}
