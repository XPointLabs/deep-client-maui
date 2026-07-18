using Sodium;

if (args.Length != 3)
{
    return 2;
}
var pair = PublicKeyAuth.GenerateKeyPair();
File.WriteAllBytes(args[0], pair.PublicKey);
File.WriteAllBytes(args[1], PublicKeyAuth.SignDetached(File.ReadAllBytes(args[2]), pair.PrivateKey));
return 0;
