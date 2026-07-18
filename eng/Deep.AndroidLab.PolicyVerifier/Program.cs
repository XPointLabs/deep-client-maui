using Sodium;

if (args.Length != 4 || args[0] != "verify")
{
    Console.Error.WriteLine("Usage: verify <public-key> <signature> <signed-payload>");
    return 2;
}

try
{
    var publicKey = File.ReadAllBytes(args[1]);
    var signature = File.ReadAllBytes(args[2]);
    var payload = File.ReadAllBytes(args[3]);
    if (publicKey.Length != 32 || signature.Length != 64 ||
        !PublicKeyAuth.VerifyDetached(signature, payload, publicKey))
    {
        return 3;
    }
    return 0;
}
catch
{
    return 4;
}
