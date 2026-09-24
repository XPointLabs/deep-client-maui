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
            string exactDeepId;
            await using (var first = await DeepIdV2AccountRuntimeOwner.OpenAsync(
                root, network, 1, clock,
                DeepMlDsa65CandidateVerifierFactory.OpenForCurrentProcess))
            {
                var created = await first.Accounts.CreateAsync(" Alice ");
                exactDeepId = created.PermanentId.CanonicalText;
                using var phrase = await first.Accounts.ReadRetainedRecoveryPhraseAsync();
                Assert.NotNull(phrase);
            }
            Assert.False(Directory.Exists(Path.Combine(root, "deep-store-v1")));
            var wrongNetwork = network.ToArray();
            wrongNetwork[0] ^= 1;
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                DeepIdV2AccountRuntimeOwner.OpenAsync(root, wrongNetwork, 1,
                    clock, DeepMlDsa65CandidateVerifierFactory.OpenForCurrentProcess));
            await using (var reopened = await DeepIdV2AccountRuntimeOwner.OpenAsync(
                root, network, 1, clock,
                DeepMlDsa65CandidateVerifierFactory.OpenForCurrentProcess))
            {
                Assert.Equal(exactDeepId,
                    (await reopened.Accounts.GetCurrentAsync())!.PermanentId.CanonicalText);
                await reopened.Accounts.DeleteRetainedRecoveryPhraseAsync();
                Assert.Null(await reopened.Accounts.ReadRetainedRecoveryPhraseAsync());
            }
            await using (var final = await DeepIdV2AccountRuntimeOwner.OpenAsync(
                root, network, 1, clock,
                DeepMlDsa65CandidateVerifierFactory.OpenForCurrentProcess))
            {
                Assert.Equal(exactDeepId,
                    (await final.Accounts.GetCurrentAsync())!.PermanentId.CanonicalText);
                Assert.Null(await final.Accounts.ReadRetainedRecoveryPhraseAsync());
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
