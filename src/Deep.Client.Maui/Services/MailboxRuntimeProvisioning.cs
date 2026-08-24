using System.Security.Cryptography;
using System.Text.Json;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Services;
using Deep.Protocol;

namespace Deep.Client.Maui.Services;

internal sealed record MailboxRuntimeProvisioning(
    MailboxCredentialBundleImportOptions ImportOptions)
{
    internal const string DirectoryName = "mailbox-runtime-v1";
    private static readonly string[] ActivationProperties =
        ["schemaVersion", "developmentOnly", "platform", "authoritySha256",
         "issuerPublicKey", "pairGeneration", "pairManifestSha256",
         "peerHolderPublicKey", "peerSessionId", "revocationSnapshotSha256"];

    public static MailboxRuntimeProvisioning LoadDevelopment(
        string appDataDirectory,
        MailboxClientPlatform platform,
        string expectedMrXPublicKeySha256,
        Func<bool>? managedEntitlement = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        try
        {
        var root = SafeRoot(appDataDirectory, DirectoryName);
        if (OperatingSystem.IsWindows()) WindowsMailboxAccessControl.ValidateTree(root);
        var activationBytes = ReadBounded(SafeFile(root, "activation.v1.json"), 32 * 1024);
        using var document = JsonDocument.Parse(activationBytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8
        });
        var activation = document.RootElement;
        RequireExactProperties(activation, ActivationProperties, "mailbox activation");
        var expectedPlatform = platform.ToString().ToLowerInvariant();
        if (activation.GetProperty("schemaVersion").GetInt32() != 1 ||
            !activation.GetProperty("developmentOnly").GetBoolean() ||
            !string.Equals(activation.GetProperty("platform").GetString(),
                expectedPlatform, StringComparison.Ordinal))
        {
            throw new MailboxRuntimeValidationException("platform-binding");
        }

        var publicKeyPin = Hex(expectedMrXPublicKeySha256, 32, "Mr. X public-key pin");
        var policyPayload = ReadBounded(
            SafeFile(root, "mr-x-mailbox-policy.payload.json"), 16 * 1024);
        var policySignature = ReadBounded(
            SafeFile(root, "mr-x-mailbox-policy.signature"), 64);
        var policyPublicKey = ReadBounded(
            SafeFile(root, "mr-x-mailbox-policy.public-key"), 32);
        try
        {
            ValidatePinnedApproval(
                policyPayload, policySignature, policyPublicKey, publicKeyPin);
        }
        catch (InvalidDataException exception)
        {
            throw new MailboxRuntimeValidationException(
                "approval-signature", exception);
        }

        var pairRoot = SafeDirectory(root, "pair");
        var authorityPath = SafeFile(root, "authority.public.json");
        var revocationPath = SafeFile(root, "revocations.v1.json");
        return new MailboxRuntimeProvisioning(
            new MailboxCredentialBundleImportOptions(
                pairRoot,
                root,
                authorityPath,
                platform,
                LowerHex(activation, "authoritySha256", 32),
                LowerHex(activation, "issuerPublicKey", 32),
                LowerHex(activation, "pairGeneration", 32),
                LowerHex(activation, "pairManifestSha256", 32),
                LowerHex(activation, "peerHolderPublicKey", 32),
                SessionId.Parse(activation.GetProperty("peerSessionId").GetString() ?? ""),
                root,
                revocationPath,
                LowerHex(activation, "revocationSnapshotSha256", 32),
                publicKeyPin,
                new MrXSignedMailboxPolicyApproval(
                    policyPayload,
                    policySignature,
                    policyPublicKey),
                DevelopmentOnly: true,
                managedEntitlement,
                timeProvider ?? TimeProvider.System));
        }
        catch (MailboxRuntimeValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new MailboxRuntimeValidationException("inventory", exception);
        }
    }

    internal static void ValidatePinnedApproval(
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> signature,
        ReadOnlySpan<byte> publicKey,
        ReadOnlySpan<byte> expectedPublicKeySha256)
    {
        Require(payload.Length is > 0 and <= 16 * 1024 &&
                signature.Length == 64 &&
                publicKey.Length == 32 &&
                expectedPublicKeySha256.Length == 32 &&
                CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(publicKey), expectedPublicKeySha256) &&
                new SodiumSessionProtocolCrypto().VerifyEd25519Detached(
                    signature, payload, publicKey),
            "Mailbox approval signature or key differs from the build-pinned Mr. X approval.");
    }

    private static string SafeRoot(string appDataDirectory, string name)
    {
        var anchor = CanonicalDirectory(appDataDirectory);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var fileSystemRoot = Path.GetPathRoot(anchor);
        Require(!string.IsNullOrEmpty(fileSystemRoot) &&
                !anchor.Equals(CanonicalDirectory(fileSystemRoot), comparison),
            "App-private data directory cannot be a filesystem root.");
        Require(Directory.Exists(anchor), "App-private data directory is missing.");
        var full = SafeChild(anchor, name);
        Require(Directory.Exists(full), "App-private mailbox runtime directory is missing.");
        RequireNoReparse(full, anchor);
        return full;
    }

    private static string SafeDirectory(string root, string name)
    {
        var path = SafeChild(root, name);
        Require(Directory.Exists(path), $"Mailbox directory '{name}' is missing.");
        return path;
    }

    private static string SafeFile(string root, string name)
    {
        var path = SafeChild(root, name);
        Require(File.Exists(path), $"Mailbox file '{name}' is missing.");
        return path;
    }

    private static string SafeChild(string root, string name)
    {
        Require(name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                !name.Contains(Path.DirectorySeparatorChar) &&
                !name.Contains(Path.AltDirectorySeparatorChar),
            "Mailbox runtime child name is invalid.");
        var path = Path.GetFullPath(Path.Combine(root, name));
        Require(path.StartsWith(root + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal),
            "Mailbox runtime path escaped app-private storage.");
        RequireNoReparse(path, root);
        return path;
    }

    private static void RequireNoReparse(string path, string anchor)
    {
        var current = Path.GetFullPath(path);
        var canonicalAnchor = CanonicalDirectory(anchor);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        Require(current.Equals(canonicalAnchor, comparison) ||
                current.StartsWith(canonicalAnchor + Path.DirectorySeparatorChar, comparison),
            "Mailbox runtime path escaped its trusted app-private anchor.");
        while (true)
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0,
                    "Mailbox runtime path contains a reparse point.");
            }
            if (current.Equals(canonicalAnchor, comparison)) return;
            var parent = Path.GetDirectoryName(current);
            Require(!string.IsNullOrEmpty(parent) &&
                    !string.Equals(parent, current, comparison),
                "Mailbox runtime path did not reach its trusted app-private anchor.");
            current = parent!;
        }
    }

    private static string CanonicalDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static byte[] ReadBounded(string path, int maximumBytes)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.SequentialScan);
        Require(stream.Length is > 0 && stream.Length <= maximumBytes,
            "Mailbox runtime file size is outside its exact bound.");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static byte[] LowerHex(JsonElement root, string property, int bytes) =>
        Hex(root.GetProperty(property).GetString(), bytes, property);

    private static byte[] Hex(string? value, int bytes, string label)
    {
        Require(value is not null && value.Length == bytes * 2 &&
                value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            $"{label} is not canonical lowercase hexadecimal.");
        var decoded = Convert.FromHexString(value!);
        Require(decoded.AsSpan().IndexOfAnyExcept((byte)0) >= 0,
            $"{label} must be nonzero.");
        return decoded;
    }

    private static void RequireExactProperties(
        JsonElement value,
        string[] expected,
        string label)
    {
        Require(value.ValueKind == JsonValueKind.Object, $"{label} must be an object.");
        var actual = value.EnumerateObject().Select(item => item.Name).ToArray();
        Require(actual.Length == expected.Length &&
                actual.ToHashSet(StringComparer.Ordinal).SetEquals(expected),
            $"{label} contains missing, duplicate, or unknown properties.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

internal sealed class MailboxRuntimeValidationException : Exception
{
    internal MailboxRuntimeValidationException(string code, Exception? innerException = null)
        : base("Mailbox runtime validation failed closed.", innerException)
    {
        if (code is not ("inventory" or "platform-binding" or "approval-signature"))
            throw new ArgumentOutOfRangeException(nameof(code));
        Code = code;
    }

    internal string Code { get; }

    internal MailboxRuntimeFailurePresentation ToUserPresentation() =>
        Code switch
        {
            "inventory" => new(
                "Не удалось подготовить Deep",
                "Локальная конфигурация транспорта отсутствует или повреждена. " +
                "Установите доверенную конфигурацию приложения и повторите запуск."),
            "platform-binding" => new(
                "Конфигурация не подходит устройству",
                "Конфигурация транспорта выпущена для другой платформы. " +
                "Установите конфигурацию для этого устройства и повторите запуск."),
            "approval-signature" => new(
                "Конфигурация не подтверждена",
                "Не удалось проверить подпись конфигурации транспорта. " +
                "Установите доверенную конфигурацию и повторите запуск."),
            _ => throw new InvalidOperationException(
                "Mailbox runtime validation code is outside the closed set.")
        };
}

internal readonly record struct MailboxRuntimeFailurePresentation(
    string Status,
    string Guidance);
