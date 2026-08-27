using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deep.Client.Maui.Services;

namespace Deep.Client.Maui.UiTests;

internal sealed record WindowsUatPackageApproval(
    string SourcePath,
    string InstalledPackageName,
    string InstalledPackageFullName,
    string InstalledPackageFamilyName,
    string Publisher,
    string SigningCertificateSha256,
    string BuildArtifactSha256)
{
    internal const string EnvironmentKey = "DEEP_E2E_WINDOWS_UAT_APPROVAL";
    internal const string InstallRootEnvironmentKey = "DEEP_E2E_WINDOWS_UAT_INSTALL_ROOT";
    private const string ApplicationIdentity = "Deep.Client.Maui.exe";
    private const int MaximumInventoryEntries = 16_384;
    private const int MaximumSignatureBytes = 1024 * 1024;
    private static readonly Regex PackageToken = new(
        "^[A-Za-z0-9._-]{1,256}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Hex64 = new(
        "^[a-f0-9]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static WindowsUatPackageApproval? Current { get; private set; }

    internal static WindowsUatPackageApproval Load(string path)
    {
        var fullPath = RequireExactFile(path, "Windows UAT approval tuple");
        using var document = JsonDocument.Parse(File.ReadAllText(fullPath),
            new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 9 ||
            root.GetProperty("schemaVersion").GetInt32() != 1 ||
            root.GetProperty("platform").GetString() != "windows" ||
            root.GetProperty("applicationIdentity").GetString() != ApplicationIdentity)
            throw new InvalidDataException("Windows UAT approval tuple shape is invalid.");

        var packageName = Required(root, "installedPackageName");
        var packageFullName = Required(root, "installedPackageFullName");
        var packageFamilyName = Required(root, "installedPackageFamilyName");
        var publisher = Required(root, "publisher");
        var signer = Required(root, "signingCertificateSha256");
        var artifact = Required(root, "buildArtifactSha256");
        if (packageName != StrictCrossPlatformContracts.AndroidPackage ||
            !PackageToken.IsMatch(packageFullName) ||
            !PackageToken.IsMatch(packageFamilyName) ||
            publisher.Length > 512 ||
            !Hex64.IsMatch(signer) || signer.All(static value => value == '0') ||
            !Hex64.IsMatch(artifact) || artifact.All(static value => value == '0'))
            throw new InvalidDataException("Windows UAT approval tuple values are invalid.");

        return new WindowsUatPackageApproval(fullPath, packageName, packageFullName,
            packageFamilyName, publisher, signer, artifact);
    }

    internal static WindowsUatPackageApproval LoadAndVerify(
        string approvalPath,
        string installRoot,
        string executablePath)
    {
        var approval = Load(approvalPath);
        approval.VerifyInstalledPackage(installRoot, executablePath);
        Current = approval;
        return approval;
    }

    internal void VerifyInstalledPackage(string installRoot, string executablePath)
    {
        var root = RequireExactDirectory(installRoot, "Windows UAT install root");
        var executable = RequireExactFile(executablePath, "Windows UAT executable");
        if (!string.Equals(Path.GetDirectoryName(executable), root,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(executable), ApplicationIdentity,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "Windows UAT executable is not the approved installed package entry point.");

        var signaturePath = RequireExactFile(Path.Combine(root, "AppxSignature.p7x"),
            "Windows UAT package signature");
        var signature = File.ReadAllBytes(signaturePath);
        if (signature.Length is <= 4 or > MaximumSignatureBytes)
            throw new InvalidDataException("Windows UAT package signature is empty or oversized.");
        byte[] actualSigner;
        try
        {
            if (!signature.AsSpan(0, 4).SequenceEqual("PKCX"u8))
                throw new InvalidDataException("Windows UAT package signature framing is invalid.");
            var signed = new SignedCms();
            signed.Decode(signature.AsSpan(4));
            signed.CheckSignature(verifySignatureOnly: true);
            if (signed.SignerInfos.Count != 1 || signed.SignerInfos[0].Certificate is null)
                throw new InvalidDataException("Windows UAT package signer set is ambiguous.");
            using var certificate = X509CertificateLoader.LoadCertificate(
                signed.SignerInfos[0].Certificate!.RawData);
            actualSigner = SHA256.HashData(certificate.RawData);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }

        try
        {
            var expectedSigner = Convert.FromHexString(SigningCertificateSha256);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(actualSigner, expectedSigner))
                    throw new InvalidDataException(
                        "Installed Windows UAT signer differs from its signed approval tuple.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expectedSigner);
            }

            var inventory = CaptureInventory(root);
            if (inventory.Count(static file => string.Equals(
                    file.LogicalName, "windows/" + ApplicationIdentity,
                    StringComparison.OrdinalIgnoreCase)) != 1)
                throw new InvalidDataException(
                    "Installed Windows UAT executable identity is ambiguous.");
            var artifact = ProductionMailboxArtifactSetDigest.ComputeAsync(
                InstalledPackageFullName,
                [actualSigner],
                inventory).GetAwaiter().GetResult();
            try
            {
                var expectedArtifact = Convert.FromHexString(BuildArtifactSha256);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(artifact, expectedArtifact))
                        throw new InvalidDataException(
                            "Installed Windows UAT artifact set differs from its signed approval tuple.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(expectedArtifact);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(artifact);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualSigner);
        }
    }

    private static ProductionMailboxArtifactFile[] CaptureInventory(string root)
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
                    throw new InvalidDataException(
                        "Windows UAT package tree contains a reparse point.");
                if (entry is DirectoryInfo child)
                    pending.Push(child);
                else if (entry is FileInfo file)
                    artifacts.Add(new ProductionMailboxArtifactFile(
                        "windows/" + Path.GetRelativePath(root, file.FullName).Replace('\\', '/'),
                        file.FullName));
                else
                    throw new InvalidDataException(
                        "Windows UAT package tree entry is unsupported.");
            }
        }
        return artifacts.OrderBy(static file => file.LogicalName,
            StringComparer.Ordinal).ToArray();
    }

    private static string Required(JsonElement root, string name)
    {
        var property = root.GetProperty(name);
        return property.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!
            : throw new InvalidDataException($"Windows UAT approval tuple {name} is invalid.");
    }

    private static string RequireExactFile(string path, string role)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"{role} is unavailable.", fullPath);
        EnsureNoReparse(fullPath);
        return fullPath;
    }

    private static string RequireExactDirectory(string path, string role)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"{role} is unavailable.");
        EnsureNoReparse(fullPath);
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void EnsureNoReparse(string path)
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
}
