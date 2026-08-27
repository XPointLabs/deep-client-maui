using System.Security.Cryptography;
using Sodium;

if (args.Length != 3 || args[0] != "generate")
    return 2;

byte[]? privateKey = null;
byte[]? publicKey = null;
try
{
    var privatePath = Path.GetFullPath(args[1]);
    var publicPath = Path.GetFullPath(args[2]);
    if (File.Exists(privatePath) || File.Exists(publicPath) ||
        Directory.Exists(privatePath) || Directory.Exists(publicPath))
    {
        return 3;
    }
    var pair = PublicKeyAuth.GenerateKeyPair();
    privateKey = pair.PrivateKey;
    publicKey = pair.PublicKey;
    using (var output = new FileStream(privatePath, FileMode.CreateNew, FileAccess.Write,
        FileShare.None, 4096, FileOptions.WriteThrough))
    {
        output.Write(privateKey);
        output.Flush(flushToDisk: true);
    }
    using (var output = new FileStream(publicPath, FileMode.CreateNew, FileAccess.Write,
        FileShare.None, 4096, FileOptions.WriteThrough))
    {
        output.Write(publicKey);
        output.Flush(flushToDisk: true);
    }
    return 0;
}
catch
{
    return 5;
}
finally
{
    if (privateKey is not null) CryptographicOperations.ZeroMemory(privateKey);
    if (publicKey is not null) CryptographicOperations.ZeroMemory(publicKey);
}
