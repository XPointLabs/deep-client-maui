using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Deep.Client.Maui.Services;

const int MaximumInventoryEntries = 16_384;
const int MaximumSignatureBytes = 1024 * 1024;

var values = Parse(args);
var root = RequiredPath(values, "--installed-root", directory: true);
var packageName = Required(values, "--package-name");
var packageFullName = Required(values, "--package-full-name");
var packageFamilyName = Required(values, "--package-family-name");
var publisher = Required(values, "--publisher");
var expectedSigner = Hex(Required(values, "--signer-sha256"));
var output = Path.GetFullPath(Required(values, "--output"));

if (!string.Equals(packageName, "network.xpoint.deep.e2e", StringComparison.Ordinal) ||
    string.IsNullOrWhiteSpace(packageFullName) ||
    string.IsNullOrWhiteSpace(packageFamilyName) ||
    string.IsNullOrWhiteSpace(publisher))
    throw new InvalidDataException("Installed Windows UAT package identity is invalid.");

var signaturePath = Path.Combine(root, "AppxSignature.p7x");
var signature = await ReadBoundedAsync(signaturePath, MaximumSignatureBytes);
byte[] actualSigner;
try
{
    if (signature.Length <= 4 || !signature.AsSpan(0, 4).SequenceEqual("PKCX"u8))
        throw new InvalidDataException("MSIX signature framing is invalid.");
    var signed = new SignedCms();
    signed.Decode(signature.AsSpan(4));
    signed.CheckSignature(verifySignatureOnly: true);
    if (signed.SignerInfos.Count != 1 || signed.SignerInfos[0].Certificate is null)
        throw new InvalidDataException("MSIX signature has an ambiguous signer set.");
    using var certificate = X509CertificateLoader.LoadCertificate(
        signed.SignerInfos[0].Certificate!.RawData);
    actualSigner = SHA256.HashData(certificate.RawData);
}
finally
{
    CryptographicOperations.ZeroMemory(signature);
}

if (!CryptographicOperations.FixedTimeEquals(actualSigner, expectedSigner))
    throw new InvalidDataException("Installed Windows UAT signer differs from the build pin.");

var inventory = CaptureInventory(root);
var executable = inventory.SingleOrDefault(static file =>
    string.Equals(file.LogicalName, "windows/Deep.Client.Maui.exe",
        StringComparison.OrdinalIgnoreCase));
if (executable is null)
    throw new InvalidDataException("Installed Windows UAT executable identity is invalid.");

var artifactHash = await ProductionMailboxArtifactSetDigest.ComputeAsync(
    packageFullName,
    [actualSigner],
    inventory);
Directory.CreateDirectory(Path.GetDirectoryName(output) ?? throw new InvalidDataException(
    "Windows UAT approval output directory is invalid."));
var document = new
{
    schemaVersion = 1,
    platform = "windows",
    applicationIdentity = "Deep.Client.Maui.exe",
    installedPackageName = packageName,
    installedPackageFullName = packageFullName,
    installedPackageFamilyName = packageFamilyName,
    publisher,
    signingCertificateSha256 = Convert.ToHexStringLower(actualSigner),
    buildArtifactSha256 = Convert.ToHexStringLower(artifactHash)
};
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(document,
    new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(output);

static Dictionary<string, string> Parse(string[] arguments)
{
    var allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        "--installed-root", "--package-name", "--package-full-name",
        "--package-family-name", "--publisher", "--signer-sha256", "--output"
    };
    if (arguments.Length != allowed.Count * 2)
        throw new ArgumentException("Windows UAT attestation arguments are incomplete.");
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < arguments.Length; index += 2)
    {
        if (!allowed.Contains(arguments[index]) ||
            !result.TryAdd(arguments[index], arguments[index + 1]))
            throw new ArgumentException("Windows UAT attestation argument is unknown or duplicated.");
    }
    return result;
}

static string Required(IReadOnlyDictionary<string, string> values, string name) =>
    values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"{name} is required.");

static string RequiredPath(
    IReadOnlyDictionary<string, string> values,
    string name,
    bool directory)
{
    var path = Path.GetFullPath(Required(values, name));
    if (directory ? !Directory.Exists(path) : !File.Exists(path))
        throw new FileNotFoundException($"{name} does not exist.", path);
    EnsureNoReparse(path);
    return path;
}

static byte[] Hex(string value)
{
    if (value.Length != 64 || value.Any(static character =>
            character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        throw new InvalidDataException("Windows UAT signer pin is invalid.");
    var result = Convert.FromHexString(value);
    if (result.AsSpan().IndexOfAnyExcept((byte)0) < 0)
        throw new InvalidDataException("Windows UAT signer pin is invalid.");
    return result;
}

static ProductionMailboxArtifactFile[] CaptureInventory(string root)
{
    var pending = new Stack<DirectoryInfo>();
    pending.Push(new DirectoryInfo(root));
    var artifacts = new List<ProductionMailboxArtifactFile>();
    var entries = 0;
    while (pending.Count != 0)
    {
        var directory = pending.Pop();
        directory.Refresh();
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Windows UAT package tree is unsafe.");
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (++entries > MaximumInventoryEntries)
                throw new InvalidDataException("Windows UAT package tree is oversized.");
            entry.Refresh();
            if (!entry.Exists || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Windows UAT package tree contains a reparse point.");
            if (entry is DirectoryInfo child)
                pending.Push(child);
            else if (entry is FileInfo file)
                artifacts.Add(new ProductionMailboxArtifactFile(
                    "windows/" + Path.GetRelativePath(root, file.FullName).Replace('\\', '/'),
                    file.FullName));
            else
                throw new InvalidDataException("Windows UAT package tree entry is unsupported.");
        }
    }
    return artifacts.OrderBy(static file => file.LogicalName, StringComparer.Ordinal).ToArray();
}

static async Task<byte[]> ReadBoundedAsync(string path, int maximumBytes)
{
    EnsureNoReparse(path);
    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
        FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    if (stream.Length is <= 0 || stream.Length > maximumBytes)
        throw new InvalidDataException("Windows UAT signature is empty or oversized.");
    var result = new byte[checked((int)stream.Length)];
    await stream.ReadExactlyAsync(result);
    return result;
}

static void EnsureNoReparse(string path)
{
    FileSystemInfo? current = Directory.Exists(path)
        ? new DirectoryInfo(path)
        : new FileInfo(path);
    while (current is not null)
    {
        current.Refresh();
        if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Windows UAT path contains a reparse point.");
        current = current switch
        {
            FileInfo file => file.Directory,
            DirectoryInfo directory => directory.Parent,
            _ => null
        };
    }
}
