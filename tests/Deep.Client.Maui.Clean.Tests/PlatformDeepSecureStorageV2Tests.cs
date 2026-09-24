using System.Security.Cryptography;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Persistence;

namespace Deep.Client.Maui.Clean.Tests;

public sealed class PlatformDeepSecureStorageV2Tests
{
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
                Assert.Throws<CryptographicException>(() =>
                    oldProtector.Unprotect(ciphertext));
            }
            finally { CryptographicOperations.ZeroMemory(ciphertext); }
        }
        finally
        {
            if (Directory.Exists(root))
            {
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
    }
}
