using System.Security.Cryptography;
using Deep.Client.Shared.Persistence;

#if ANDROID
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
#endif

namespace Deep.Client.Maui.Services;

internal static class PlatformDeepSecureStorage
{
    private const string RelativeDirectory = "deep-store-v1";
    private const string StateFileName = "secure-storage.dss";

    internal static JournaledDeepSecureStorage Create(string appDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        var root = Path.GetFullPath(appDataDirectory);
        var statePath = Path.Combine(root, RelativeDirectory, StateFileName);
        return new JournaledDeepSecureStorage(statePath, new PlatformDeepSecretProtector());
    }
}

internal sealed class PlatformDeepSecretProtector : IDeepSecretProtector
{
#if WINDOWS
    private static readonly byte[] Entropy =
        "Deep/Store/V1/Windows-DPAPI/aggregate"u8.ToArray();
#elif ANDROID
    private const string KeyStoreName = "AndroidKeyStore";
    private const string KeyAlias = "network.xpoint.deep.store.v1.aggregate";
    private const string Transformation = "AES/GCM/NoPadding";
#endif

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.IsEmpty)
        {
            throw new ArgumentException("Secure-storage plaintext must not be empty.", nameof(plaintext));
        }

#if WINDOWS
        var clear = plaintext.ToArray();
        try
        {
            var protectedPayload = ProtectedData.Protect(
                clear,
                Entropy,
                DataProtectionScope.CurrentUser);
            var output = new byte[4 + protectedPayload.Length];
            "WDS1"u8.CopyTo(output);
            protectedPayload.CopyTo(output, 4);
            CryptographicOperations.ZeroMemory(protectedPayload);
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
#elif ANDROID
        using var key = GetOrCreateAndroidKey();
        using var cipher = Cipher.GetInstance(Transformation)
            ?? throw new CryptographicException("Android AES-GCM cipher is unavailable.");
        cipher.Init(Javax.Crypto.CipherMode.EncryptMode, key);
        var nonce = cipher.GetIV()
            ?? throw new CryptographicException("Android Keystore returned no AES-GCM nonce.");
        if (nonce.Length != 12)
        {
            CryptographicOperations.ZeroMemory(nonce);
            throw new CryptographicException("Android Keystore returned an invalid AES-GCM nonce.");
        }

        var clear = plaintext.ToArray();
        byte[]? ciphertext = null;
        try
        {
            ciphertext = cipher.DoFinal(clear)
                ?? throw new CryptographicException("Android Keystore returned no ciphertext.");
            if (ciphertext.Length != clear.Length + 16)
            {
                throw new CryptographicException("Android Keystore returned an invalid AES-GCM payload.");
            }
            var output = new byte[4 + nonce.Length + ciphertext.Length];
            "ADS1"u8.CopyTo(output);
            nonce.CopyTo(output, 4);
            ciphertext.CopyTo(output, 16);
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            CryptographicOperations.ZeroMemory(nonce);
            if (ciphertext is not null)
            {
                CryptographicOperations.ZeroMemory(ciphertext);
            }
        }
#else
        throw new PlatformNotSupportedException(
            "Deep secure storage requires Android Keystore or Windows DPAPI.");
#endif
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
    {
#if WINDOWS
        if (protectedBytes.Length <= 4 || !protectedBytes[..4].SequenceEqual("WDS1"u8))
        {
            throw new CryptographicException("Windows secure-storage envelope is invalid.");
        }
        var payload = protectedBytes[4..].ToArray();
        try
        {
            return ProtectedData.Unprotect(
                payload,
                Entropy,
                DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
#elif ANDROID
        if (protectedBytes.Length < 4 + 12 + 16
            || !protectedBytes[..4].SequenceEqual("ADS1"u8))
        {
            throw new CryptographicException("Android secure-storage envelope is invalid.");
        }

        using var key = GetExistingAndroidKey();
        using var cipher = Cipher.GetInstance(Transformation)
            ?? throw new CryptographicException("Android AES-GCM cipher is unavailable.");
        var nonce = protectedBytes.Slice(4, 12).ToArray();
        var ciphertext = protectedBytes[16..].ToArray();
        try
        {
            using var parameters = new GCMParameterSpec(128, nonce);
            cipher.Init(Javax.Crypto.CipherMode.DecryptMode, key, parameters);
            return cipher.DoFinal(ciphertext)
                ?? throw new CryptographicException("Android Keystore returned no plaintext.");
        }
        catch (Exception exception) when (exception is not CryptographicException)
        {
            throw new CryptographicException(
                "Android secure-storage authentication failed.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
#else
        throw new PlatformNotSupportedException(
            "Deep secure storage requires Android Keystore or Windows DPAPI.");
#endif
    }

#if ANDROID
    private static Java.Security.IKey GetOrCreateAndroidKey()
    {
        using var keyStore = LoadAndroidKeyStore();
        if (keyStore.ContainsAlias(KeyAlias))
        {
            return keyStore.GetKey(KeyAlias, null)
                ?? throw new CryptographicException("Android Keystore key is unavailable.");
        }

        using var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, KeyStoreName)
            ?? throw new CryptographicException("Android Keystore AES generator is unavailable.");
        using var specification = new KeyGenParameterSpec.Builder(
                KeyAlias,
                KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetKeySize(256)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetRandomizedEncryptionRequired(true)
            .Build();
        generator.Init(specification);
        return generator.GenerateKey()
            ?? throw new CryptographicException("Android Keystore did not generate an AES key.");
    }

    private static Java.Security.IKey GetExistingAndroidKey()
    {
        using var keyStore = LoadAndroidKeyStore();
        if (!keyStore.ContainsAlias(KeyAlias))
        {
            throw new CryptographicException(
                "Android secure-storage key is missing; local state reset is required.");
        }
        return keyStore.GetKey(KeyAlias, null)
            ?? throw new CryptographicException("Android Keystore key is unavailable.");
    }

    private static KeyStore LoadAndroidKeyStore()
    {
        var keyStore = KeyStore.GetInstance(KeyStoreName)
            ?? throw new CryptographicException("Android Keystore is unavailable.");
        keyStore.Load(null);
        return keyStore;
    }
#endif
}
