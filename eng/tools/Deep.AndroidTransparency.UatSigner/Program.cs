using System.Security.Cryptography;
using Deep.Client.Maui.Services;
using Sodium;

try
{
    var options = Parse(args, "sign-uat-seed",
        ["seed", "signing-bytes", "signature", "public-key"]);
    return SignUatSeed(options);
}
catch (Exception exception) when (exception is ArgumentException or IOException or
    InvalidDataException or CryptographicException or OverflowException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
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
