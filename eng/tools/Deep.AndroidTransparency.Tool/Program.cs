using System.Security.Cryptography;
using System.Text;
using System.IO.Compression;
using Deep.Client.Maui.Services;
using Sodium;

const int MaximumInventoryBytes = 512 * 1024;

try
{
    if (args.Length == 0) throw new ArgumentException("A command is required.");
    return args[0] switch
    {
        "verify" => await VerifyAsync(Parse(args, "verify",
        [
            "manifest", "expected-manifest-sha256", "expected-mrx-sha256",
            "application-id", "version-code", "signer-lineage", "inventory"
        ])),
        "prepare" => await PrepareAsync(Parse(args, "prepare",
        [
            "inventory", "application-id", "version-code", "signer-lineage",
            "mrx-public-key", "unsigned-manifest", "signing-bytes"
        ])),
        "assemble" => Assemble(Parse(args, "assemble",
            ["unsigned-manifest", "signature", "output"])),
        "sign-uat-seed" => SignUatSeed(Parse(args, "sign-uat-seed",
            ["seed", "signing-bytes", "signature", "public-key"])),
        "duplicate-password-source" => DuplicatePasswordSource(Parse(args,
            "duplicate-password-source", ["source", "output"])),
        "replace-act1" => ReplaceAct1(Parse(args, "replace-act1",
            ["apk", "manifest", "output"])),
        "dispose-password-source" => DisposePasswordSource(Parse(args,
            "dispose-password-source", ["path"])),
        _ => throw new ArgumentException("Unknown Android transparency command.")
    };
}
catch (Exception exception) when (exception is ArgumentException or IOException or
    InvalidDataException or CryptographicException or OverflowException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static async Task<int> VerifyAsync(Dictionary<string, string> options)
{
    var manifest = ReadBounded(options["manifest"],
        ProductionAndroidTransparencyManifestCodec.MaximumEncodedBytes);
    var inventory = ParseInventory(ReadBounded(options["inventory"], MaximumInventoryBytes));
    var lineage = ParseLineage(options["signer-lineage"]);
    var versionCode = ParseVersion(options["version-code"]);
    var verified = await ProductionAndroidCodeTransparencyVerifier.VerifyBuildAsync(
        manifest,
        ParseHash(options["expected-manifest-sha256"], "expected-manifest-sha256"),
        ParseHash(options["expected-mrx-sha256"], "expected-mrx-sha256"),
        options["application-id"], versionCode, lineage, inventory);
    Console.WriteLine(Convert.ToHexStringLower(verified));
    return 0;
}

static async Task<int> PrepareAsync(Dictionary<string, string> options)
{
    var inventory = ParseInventory(ReadBounded(options["inventory"], MaximumInventoryBytes));
    var artifacts = new List<ProductionAndroidTransparencyArtifact>(inventory.Count);
    foreach (var artifact in inventory)
        artifacts.Add(new ProductionAndroidTransparencyArtifact(
            artifact.Key,
            await ProductionAndroidSemanticApkDigest.ComputeAsync(artifact.Value)));
    var manifest = new ProductionAndroidTransparencyManifest
    {
        ApplicationId = options["application-id"],
        VersionCode = ParseVersion(options["version-code"]),
        PlaySignerLineageSha256 = ParseLineage(options["signer-lineage"]),
        Artifacts = artifacts,
        MrXEd25519PublicKey = ParseHash(options["mrx-public-key"], "mrx-public-key"),
        Signature = new byte[64]
    };
    var unsigned = ProductionAndroidTransparencyManifestCodec.Encode(manifest);
    var signingBytes = ProductionAndroidTransparencyManifestCodec.GetSigningBytes(manifest);
    WriteNew(options["unsigned-manifest"], unsigned,
        ProductionAndroidTransparencyManifestCodec.MaximumEncodedBytes);
    WriteNew(options["signing-bytes"], signingBytes,
        ProductionAndroidTransparencyManifestCodec.MaximumEncodedBytes + 64);
    Console.WriteLine(Convert.ToHexStringLower(SHA256.HashData(signingBytes)));
    return 0;
}

static int Assemble(Dictionary<string, string> options)
{
    var unsignedBytes = ReadBounded(options["unsigned-manifest"],
        ProductionAndroidTransparencyManifestCodec.MaximumEncodedBytes);
    var unsigned = ProductionAndroidTransparencyManifestCodec.Decode(unsignedBytes);
    if (unsigned.Signature.Span.IndexOfAnyExcept((byte)0) >= 0)
        throw new InvalidDataException("ACT1 signing request already contains a signature.");
    var signature = ReadBounded(options["signature"], 64);
    if (signature.Length != 64)
        throw new InvalidDataException("ACT1 detached signature must be exactly 64 bytes.");
    var signed = new ProductionAndroidTransparencyManifest
    {
        ApplicationId = unsigned.ApplicationId,
        VersionCode = unsigned.VersionCode,
        PlaySignerLineageSha256 = unsigned.PlaySignerLineageSha256,
        Artifacts = unsigned.Artifacts,
        MrXEd25519PublicKey = unsigned.MrXEd25519PublicKey,
        Signature = signature
    };
    var signingBytes = ProductionAndroidTransparencyManifestCodec.GetSigningBytes(signed);
    if (!PublicKeyAuth.VerifyDetached(signature, signingBytes,
            signed.MrXEd25519PublicKey.ToArray()))
        throw new InvalidDataException("ACT1 detached signature is invalid.");
    var encoded = ProductionAndroidTransparencyManifestCodec.Encode(signed);
    WriteNew(options["output"], encoded,
        ProductionAndroidTransparencyManifestCodec.MaximumEncodedBytes);
    Console.WriteLine(Convert.ToHexStringLower(SHA256.HashData(encoded)));
    return 0;
}

static int SignUatSeed(Dictionary<string, string> options)
{
    var seed = ReadBounded(options["seed"], 32);
    byte[]? privateKey = null;
    byte[]? publicKey = null;
    byte[]? signingBytes = null;
    byte[]? signature = null;
    try
    {
        if (seed.Length != 32)
            throw new InvalidDataException("UAT Mr. X seed must be exactly 32 bytes.");
        var pair = PublicKeyAuth.GenerateKeyPair(seed);
        privateKey = pair.PrivateKey;
        publicKey = pair.PublicKey;
        signingBytes = ReadBounded(options["signing-bytes"],
            ProductionAndroidTransparencyManifestCodec.MaximumEncodedBytes + 64);
        signature = PublicKeyAuth.SignDetached(signingBytes, privateKey);
        if (signature.Length != 64 || publicKey.Length != 32 ||
            !PublicKeyAuth.VerifyDetached(signature, signingBytes, publicKey))
            throw new CryptographicException("UAT ACT1 signature verification failed.");
        WriteNew(options["signature"], signature, 64);
        WriteNew(options["public-key"], publicKey, 32);
        return 0;
    }
    finally
    {
        CryptographicOperations.ZeroMemory(seed);
        if (privateKey is not null) CryptographicOperations.ZeroMemory(privateKey);
        if (publicKey is not null) CryptographicOperations.ZeroMemory(publicKey);
        if (signingBytes is not null) CryptographicOperations.ZeroMemory(signingBytes);
        if (signature is not null) CryptographicOperations.ZeroMemory(signature);
    }
}

static int DuplicatePasswordSource(Dictionary<string, string> options)
{
    var source = ReadBounded(options["source"], 4096);
    byte[]? output = null;
    try
    {
        var length = source.Length;
        if (source[length - 1] == (byte)'\n') length--;
        if (length > 0 && source[length - 1] == (byte)'\r') length--;
        if (length == 0 || source.AsSpan(0, length).IndexOfAny((byte)'\r', (byte)'\n') >= 0)
            throw new InvalidDataException("Android signing password source must contain exactly one non-empty line.");
        output = new byte[checked(length * 2 + 2)];
        source.AsSpan(0, length).CopyTo(output);
        output[length] = (byte)'\n';
        source.AsSpan(0, length).CopyTo(output.AsSpan(length + 1));
        output[^1] = (byte)'\n';
        WriteNew(options["output"], output, 8194);
        return 0;
    }
    finally
    {
        CryptographicOperations.ZeroMemory(source);
        if (output is not null) CryptographicOperations.ZeroMemory(output);
    }
}

static int ReplaceAct1(Dictionary<string, string> options)
{
    const string entryName = "assets/" + ProductionAndroidCodeTransparencyVerifier.AssetPath;
    var input = Path.GetFullPath(options["apk"]);
    var output = Path.GetFullPath(options["output"]);
    if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("ACT1 replacement input and output must differ.");
    var inputInfo = new FileInfo(input);
    EnsureNoReparse(Path.GetDirectoryName(input) ?? throw new InvalidDataException());
    if (!inputInfo.Exists || inputInfo.Length is <= 0 or > 512L * 1024 * 1024 ||
        (inputInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        throw new InvalidDataException("ACT1 replacement APK is unavailable or oversized.");
    var outputParent = Path.GetDirectoryName(output) ?? throw new InvalidDataException(
        "ACT1 replacement output directory is unavailable.");
    EnsureNoReparse(outputParent);
    if (File.Exists(output))
        throw new InvalidDataException("ACT1 replacement output already exists.");
    var manifest = ReadBounded(options["manifest"],
        ProductionAndroidTransparencyManifestCodec.MaximumEncodedBytes);
    try
    {
        File.Copy(input, output, overwrite: false);
        try
        {
            using (var archive = ZipFile.Open(output, ZipArchiveMode.Update))
            {
                var matches = archive.Entries.Where(entry => string.Equals(
                    entry.FullName, entryName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1 || !string.Equals(matches[0].FullName, entryName,
                        StringComparison.Ordinal))
                    throw new InvalidDataException(
                        "APK must contain exactly one canonical ACT1 asset.");
                matches[0].Delete();
                var replacement = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                using var stream = replacement.Open();
                stream.Write(manifest);
            }
            using var verified = ZipFile.OpenRead(output);
            var exact = verified.Entries.Where(entry => string.Equals(
                entry.FullName, entryName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (exact.Length != 1 || exact[0].FullName != entryName ||
                exact[0].Length != manifest.Length)
                throw new InvalidDataException("ACT1 replacement postcondition failed.");
            using var asset = exact[0].Open();
            var frozen = new byte[manifest.Length];
            asset.ReadExactly(frozen);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(frozen, manifest) ||
                    asset.ReadByte() != -1)
                    throw new InvalidDataException("ACT1 replacement content differs.");
            }
            finally { CryptographicOperations.ZeroMemory(frozen); }
            return 0;
        }
        catch
        {
            File.Delete(output);
            throw;
        }
    }
    finally { CryptographicOperations.ZeroMemory(manifest); }
}

static int DisposePasswordSource(Dictionary<string, string> options)
{
    var path = Path.GetFullPath(options["path"]);
    var parent = Path.GetDirectoryName(path) ?? throw new InvalidDataException(
        "Android signing password lease directory is unavailable.");
    var parentInfo = new DirectoryInfo(parent);
    var temporaryRoot = Path.GetFullPath(Path.GetTempPath())
        .TrimEnd(Path.DirectorySeparatorChar);
    if (!string.Equals(Path.GetFileName(path), "password-source", StringComparison.Ordinal) ||
        !parentInfo.Name.StartsWith("deep-android-signing-", StringComparison.Ordinal) ||
        !string.Equals(parentInfo.Parent?.FullName.TrimEnd(Path.DirectorySeparatorChar),
            temporaryRoot, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Android signing password lease path is invalid.");
    EnsureNoReparse(parent);
    var info = new FileInfo(path);
    info.Refresh();
    if (!info.Exists || info.Length is <= 0 or > 8194 ||
        (info.Attributes & FileAttributes.ReparsePoint) != 0)
        throw new InvalidDataException("Android signing password lease is invalid.");
    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None,
               4096, FileOptions.WriteThrough))
    {
        var zeros = new byte[checked((int)stream.Length)];
        stream.Write(zeros);
        stream.Flush(flushToDisk: true);
        CryptographicOperations.ZeroMemory(zeros);
    }
    File.Delete(path);
    if (Directory.EnumerateFileSystemEntries(parent).Any())
        throw new InvalidDataException("Android signing password lease directory is not empty.");
    Directory.Delete(parent);
    return 0;
}

static Dictionary<string, string> Parse(
    string[] arguments, string command, IReadOnlyList<string> expected)
{
    if (arguments.Length != 1 + expected.Count * 2 ||
        !string.Equals(arguments[0], command, StringComparison.Ordinal))
        throw new ArgumentException($"Invalid {command} arguments.");
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 1; index < arguments.Length; index += 2)
    {
        if (!arguments[index].StartsWith("--", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(arguments[index + 1]) ||
            !result.TryAdd(arguments[index][2..], arguments[index + 1]))
            throw new ArgumentException($"Invalid {command} arguments.");
    }
    if (result.Count != expected.Count || expected.Any(name => !result.ContainsKey(name)))
        throw new ArgumentException($"Incomplete {command} arguments.");
    return result;
}

static byte[] ReadBounded(string path, int maximum)
{
    var fullPath = Path.GetFullPath(path);
    var parent = Path.GetDirectoryName(fullPath) ?? throw new InvalidDataException(
        "Android transparency input directory is unavailable.");
    EnsureNoReparse(parent);
    var file = new FileInfo(fullPath);
    file.Refresh();
    if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0)
        throw new InvalidDataException(
            "Android transparency input path contains a reparse point.");
    using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.None);
    if (stream.Length is <= 0 || stream.Length > maximum)
        throw new InvalidDataException("Android transparency input is empty or oversized.");
    var bytes = new byte[checked((int)stream.Length)];
    stream.ReadExactly(bytes);
    if (stream.Length != bytes.Length)
        throw new InvalidDataException("Android transparency input changed during read.");
    EnsureNoReparse(parent);
    file.Refresh();
    if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0)
        throw new InvalidDataException(
            "Android transparency input path changed to a reparse point.");
    return bytes;
}

static void WriteNew(string path, byte[] bytes, int maximum)
{
    if (bytes.Length is <= 0 || bytes.Length > maximum)
        throw new InvalidDataException("Android transparency output is outside strict bounds.");
    var fullPath = Path.GetFullPath(path);
    var parent = Path.GetDirectoryName(fullPath) ?? throw new InvalidDataException(
        "Android transparency output directory is unavailable.");
    EnsureNoReparse(parent);
    using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write,
        FileShare.None);
    stream.Write(bytes);
    stream.Flush(flushToDisk: true);
}

static IReadOnlyDictionary<string, string> ParseInventory(byte[] encoded)
{
    string text;
    try { text = new UTF8Encoding(false, true).GetString(encoded); }
    catch (DecoderFallbackException exception)
    { throw new InvalidDataException("Android transparency inventory is not strict UTF-8.", exception); }
    if (text.Contains('\r') || !text.EndsWith('\n'))
        throw new InvalidDataException("Android transparency inventory is non-canonical.");
    var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    if (lines.Length is < 1 or > ProductionAndroidTransparencyManifestCodec.MaximumArtifacts)
        throw new InvalidDataException("Android transparency inventory count is invalid.");
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    string? previous = null;
    foreach (var line in lines)
    {
        var separator = line.IndexOf('\t');
        if (separator <= 0 || separator != line.LastIndexOf('\t') || separator == line.Length - 1)
            throw new InvalidDataException("Android transparency inventory line is invalid.");
        var identity = line[..separator];
        var path = Path.GetFullPath(line[(separator + 1)..]);
        if (previous is not null && string.CompareOrdinal(previous, identity) >= 0)
            throw new InvalidDataException("Android transparency inventory is unordered or duplicated.");
        if (!result.TryAdd(identity, path))
            throw new InvalidDataException("Android transparency inventory is duplicated.");
        previous = identity;
    }
    if (!result.ContainsKey("base"))
        throw new InvalidDataException("Android transparency inventory has no base APK.");
    return result;
}

static ReadOnlyMemory<byte>[] ParseLineage(string value)
{
    var result = value.Split('|', StringSplitOptions.None)
        .Select(static value => (ReadOnlyMemory<byte>)ParseHash(value, "signer-lineage"))
        .ToArray();
    if (result.Length is < 1 or > 32 || result.Select(static hash =>
            Convert.ToHexString(hash.Span)).Distinct(StringComparer.Ordinal).Count() != result.Length)
        throw new InvalidDataException("Android transparency signer lineage is invalid.");
    return result;
}

static ulong ParseVersion(string value) =>
    ulong.TryParse(value, out var parsed) && parsed != 0 ? parsed :
        throw new InvalidDataException("Android transparency versionCode is invalid.");

static byte[] ParseHash(string value, string name)
{
    if (value.Length != 64 || value.Any(static value => value is not
            (>= '0' and <= '9' or >= 'a' and <= 'f')))
        throw new InvalidDataException($"Android transparency {name} is invalid.");
    var decoded = Convert.FromHexString(value);
    if (decoded.AsSpan().IndexOfAnyExcept((byte)0) < 0)
        throw new InvalidDataException($"Android transparency {name} is invalid.");
    return decoded;
}

static void EnsureNoReparse(string path)
{
    DirectoryInfo? cursor = new(path);
    while (cursor is not null)
    {
        cursor.Refresh();
        if (cursor.Exists && (cursor.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(
                "Android transparency output path contains a reparse point.");
        cursor = cursor.Parent;
    }
}
