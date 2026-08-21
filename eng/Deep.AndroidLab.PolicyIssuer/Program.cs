using System.Security.Cryptography;
using Sodium;

if (args.Length != 5 || args[0] != "sign")
{
    return 2;
}

byte[]? privateKey = null;
byte[]? publicKey = null;
byte[]? payload = null;
byte[]? signature = null;
try
{
    var privateKeyPath = ExactFile(args[1], 64);
    var publicKeyPath = ExactFile(args[2], 32);
    var payloadPath = ExactFile(args[3], null);
    var outputPath = Path.GetFullPath(args[4]);
    if (!Path.IsPathFullyQualified(outputPath) || File.Exists(outputPath) ||
        Directory.Exists(outputPath) || Path.GetFileName(outputPath) != "policy.signature")
    {
        return 3;
    }
    var parent = Directory.GetParent(outputPath);
    if (parent is null || !parent.Exists || IsReparse(parent.FullName))
    {
        return 3;
    }

    privateKey = File.ReadAllBytes(privateKeyPath);
    publicKey = File.ReadAllBytes(publicKeyPath);
    payload = File.ReadAllBytes(payloadPath);
    if (payload.Length is < 2 or > 65_536)
    {
        return 3;
    }
    signature = PublicKeyAuth.SignDetached(payload, privateKey);
    if (signature.Length != 64 ||
        !PublicKeyAuth.VerifyDetached(signature, payload, publicKey))
    {
        return 4;
    }

    using var output = new FileStream(outputPath, FileMode.CreateNew,
        FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
    output.Write(signature);
    output.Flush(flushToDisk: true);
    return 0;
}
catch
{
    return 5;
}
finally
{
    if (privateKey is not null) CryptographicOperations.ZeroMemory(privateKey);
    if (payload is not null) CryptographicOperations.ZeroMemory(payload);
    if (signature is not null) CryptographicOperations.ZeroMemory(signature);
    if (publicKey is not null) CryptographicOperations.ZeroMemory(publicKey);
}

static string ExactFile(string value, long? exactLength)
{
    var path = Path.GetFullPath(value);
    if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || IsReparse(path))
        throw new InvalidDataException();
    if (exactLength is not null && new FileInfo(path).Length != exactLength)
        throw new InvalidDataException();
    return path;
}

static bool IsReparse(string path) =>
    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
