using System.Security.Cryptography;
using System.Text.Json;
using Sodium;

const int maximumJsonBytes = 16 * 1024;
const long maximumClockSkewSeconds = 300;
const string jsonName = "production-mailbox-privacy-routes.v2.json";
const string signatureName = "production-mailbox-privacy-routes.v2.sig";
const string publicKeyName = "production-mailbox-privacy-routes.v2.pub";

if (args.Length != 5 || args[0] != "issue")
{
    return 2;
}

byte[]? candidate = null;
byte[]? privateKey = null;
byte[]? publicKey = null;
byte[]? signature = null;
try
{
    var candidatePath = ExactFile(args[1], null);
    var privateKeyPath = ExactFile(args[2], 64);
    var publicKeyPath = ExactFile(args[3], 32);
    var outputDirectory = ExactEmptyDirectory(args[4]);

    candidate = File.ReadAllBytes(candidatePath);
    if (candidate.Length is < 2 or > maximumJsonBytes)
        throw new InvalidDataException();

    ValidateCanonicalCandidate(candidate, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    privateKey = File.ReadAllBytes(privateKeyPath);
    publicKey = File.ReadAllBytes(publicKeyPath);
    signature = PublicKeyAuth.SignDetached(candidate, privateKey);
    if (signature.Length != 64 ||
        !PublicKeyAuth.VerifyDetached(signature, candidate, publicKey))
    {
        throw new CryptographicException();
    }

    WriteExact(Path.Combine(outputDirectory, jsonName), candidate);
    WriteExact(Path.Combine(outputDirectory, signatureName), signature);
    WriteExact(Path.Combine(outputDirectory, publicKeyName), publicKey);

    var publishedCandidate = File.ReadAllBytes(Path.Combine(outputDirectory, jsonName));
    var publishedSignature = File.ReadAllBytes(Path.Combine(outputDirectory, signatureName));
    var publishedPublicKey = File.ReadAllBytes(Path.Combine(outputDirectory, publicKeyName));
    try
    {
        if (!candidate.AsSpan().SequenceEqual(publishedCandidate) ||
            !publicKey.AsSpan().SequenceEqual(publishedPublicKey) ||
            publishedSignature.Length != 64 ||
            !PublicKeyAuth.VerifyDetached(
                publishedSignature, publishedCandidate, publishedPublicKey))
        {
            throw new CryptographicException();
        }
    }
    finally
    {
        CryptographicOperations.ZeroMemory(publishedCandidate);
        CryptographicOperations.ZeroMemory(publishedSignature);
        CryptographicOperations.ZeroMemory(publishedPublicKey);
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
    if (signature is not null) CryptographicOperations.ZeroMemory(signature);
    if (publicKey is not null) CryptographicOperations.ZeroMemory(publicKey);
    if (candidate is not null) CryptographicOperations.ZeroMemory(candidate);
}

static void ValidateCanonicalCandidate(byte[] encoded, long nowUnixSeconds)
{
    using var document = JsonDocument.Parse(encoded, new JsonDocumentOptions
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 6
    });
    var root = document.RootElement;
    RequireProperties(root,
    [
        "schemaVersion", "developmentOnly", "networkId", "notBeforeUnixSeconds",
        "expiresUnixSeconds", "primary", "fallback"
    ]);
    Require(root.GetProperty("schemaVersion").GetInt32() == 2);
    Require(!root.GetProperty("developmentOnly").GetBoolean());
    var networkId = LowerHex(root.GetProperty("networkId"), 16);
    Require(networkId.AsSpan().IndexOfAnyExcept((byte)0) >= 0);

    var notBefore = root.GetProperty("notBeforeUnixSeconds").GetInt64();
    var expires = root.GetProperty("expiresUnixSeconds").GetInt64();
    Require(notBefore >= 0 && expires > notBefore);
    Require(nowUnixSeconds <= long.MaxValue - maximumClockSkewSeconds &&
            nowUnixSeconds >= long.MinValue + maximumClockSkewSeconds &&
            nowUnixSeconds + maximumClockSkewSeconds >= notBefore &&
            nowUnixSeconds - maximumClockSkewSeconds <= expires);

    var primary = ParseRoute(root.GetProperty("primary"));
    var fallback = ParseRoute(root.GetProperty("fallback"));
    Require(!string.Equals(primary.EntryOrigin, fallback.EntryOrigin,
        StringComparison.Ordinal));

    var canonical = EncodeCanonical(
        Convert.ToHexStringLower(networkId), notBefore, expires, primary, fallback);
    try
    {
        Require(encoded.AsSpan().SequenceEqual(canonical));
    }
    finally
    {
        CryptographicOperations.ZeroMemory(canonical);
        CryptographicOperations.ZeroMemory(networkId);
    }
}

static Route ParseRoute(JsonElement value)
{
    RequireProperties(value, ["entryOrigin", "hops"]);
    var originText = value.GetProperty("entryOrigin").GetString();
    Require(originText is not null &&
            Uri.TryCreate(originText, UriKind.Absolute, out var origin) &&
            string.Equals(origin.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) &&
            string.IsNullOrEmpty(origin.UserInfo) &&
            string.IsNullOrEmpty(origin.Query) &&
            string.IsNullOrEmpty(origin.Fragment) &&
            origin.AbsolutePath == "/" &&
            string.Equals(origin.AbsoluteUri, originText, StringComparison.Ordinal));

    var hopsValue = value.GetProperty("hops");
    Require(hopsValue.ValueKind == JsonValueKind.Array && hopsValue.GetArrayLength() == 3);
    var hops = new List<Hop>(3);
    foreach (var valueHop in hopsValue.EnumerateArray())
    {
        RequireProperties(valueHop, ["routerOwnerId", "keyId", "epoch", "x25519PublicKey"]);
        var routerOwnerId = LowerHex(valueHop.GetProperty("routerOwnerId"), 32);
        var keyId = LowerHex(valueHop.GetProperty("keyId"), 32);
        ulong epoch = 0;
        Require(valueHop.GetProperty("epoch").ValueKind == JsonValueKind.Number &&
                valueHop.GetProperty("epoch").TryGetUInt64(out epoch) && epoch > 0);
        var x25519 = LowerHex(valueHop.GetProperty("x25519PublicKey"), 32);
        Require(routerOwnerId.AsSpan().IndexOfAnyExcept((byte)0) >= 0);
        Require(keyId.AsSpan().IndexOfAnyExcept((byte)0) >= 0);
        Require(x25519.AsSpan().IndexOfAnyExcept((byte)0) >= 0);
        hops.Add(new Hop(
            Convert.ToHexStringLower(routerOwnerId),
            Convert.ToHexStringLower(keyId),
            epoch,
            Convert.ToHexStringLower(x25519)));
        CryptographicOperations.ZeroMemory(routerOwnerId);
        CryptographicOperations.ZeroMemory(keyId);
        CryptographicOperations.ZeroMemory(x25519);
    }
    Require(hops.Select(hop => hop.RouterOwnerId).Distinct(StringComparer.Ordinal).Count() == 3);
    Require(hops.Select(hop => hop.KeyId).Distinct(StringComparer.Ordinal).Count() == 3);
    Require(hops.Select(hop => hop.X25519PublicKey).Distinct(StringComparer.Ordinal).Count() == 3);
    return new Route(originText!, hops);
}

static byte[] EncodeCanonical(
    string networkId,
    long notBefore,
    long expires,
    Route primary,
    Route fallback)
{
    using var output = new MemoryStream();
    using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = false }))
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", 2);
        writer.WriteBoolean("developmentOnly", false);
        writer.WriteString("networkId", networkId);
        writer.WriteNumber("notBeforeUnixSeconds", notBefore);
        writer.WriteNumber("expiresUnixSeconds", expires);
        WriteRoute(writer, "primary", primary);
        WriteRoute(writer, "fallback", fallback);
        writer.WriteEndObject();
    }
    return output.ToArray();
}

static void WriteRoute(Utf8JsonWriter writer, string name, Route route)
{
    writer.WriteStartObject(name);
    writer.WriteString("entryOrigin", route.EntryOrigin);
    writer.WriteStartArray("hops");
    foreach (var hop in route.Hops)
    {
        writer.WriteStartObject();
        writer.WriteString("routerOwnerId", hop.RouterOwnerId);
        writer.WriteString("keyId", hop.KeyId);
        writer.WriteNumber("epoch", hop.Epoch);
        writer.WriteString("x25519PublicKey", hop.X25519PublicKey);
        writer.WriteEndObject();
    }
    writer.WriteEndArray();
    writer.WriteEndObject();
}

static byte[] LowerHex(JsonElement value, int bytes)
{
    Require(value.ValueKind == JsonValueKind.String);
    var text = value.GetString();
    Require(text is not null && text.Length == bytes * 2 &&
            text.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'));
    return Convert.FromHexString(text!);
}

static void RequireProperties(JsonElement value, string[] expected)
{
    Require(value.ValueKind == JsonValueKind.Object);
    var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
    Require(actual.SequenceEqual(expected, StringComparer.Ordinal));
}

static string ExactFile(string value, long? exactLength)
{
    var path = Path.GetFullPath(value);
    if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || IsReparse(path))
        throw new InvalidDataException();
    AssertNoReparseAncestors(path);
    if (exactLength is not null && new FileInfo(path).Length != exactLength)
        throw new InvalidDataException();
    return path;
}

static string ExactEmptyDirectory(string value)
{
    var path = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
    if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path) || IsReparse(path) ||
        Directory.EnumerateFileSystemEntries(path).Any())
    {
        throw new InvalidDataException();
    }
    AssertNoReparseAncestors(path);
    return path;
}

static void AssertNoReparseAncestors(string path)
{
    string? current = Path.GetFullPath(path);
    while (!string.IsNullOrEmpty(current))
    {
        if ((File.Exists(current) || Directory.Exists(current)) && IsReparse(current))
            throw new InvalidDataException();
        current = Directory.GetParent(current)?.FullName;
    }
}

static void WriteExact(string path, byte[] value)
{
    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
        FileShare.None, 4096, FileOptions.WriteThrough);
    stream.Write(value);
    stream.Flush(flushToDisk: true);
}

static bool IsReparse(string path) =>
    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

static void Require(bool condition)
{
    if (!condition) throw new InvalidDataException();
}

sealed record Hop(string RouterOwnerId, string KeyId, ulong Epoch, string X25519PublicKey);
sealed record Route(string EntryOrigin, IReadOnlyList<Hop> Hops);
