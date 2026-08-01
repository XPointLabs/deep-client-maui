using System.Globalization;
using System.Text.RegularExpressions;

namespace Deep.AndroidRunner;

internal sealed record RunnerOptions(
    string Serial,
    string ApkPath,
    string ArtifactsPath,
    string ResultPath,
    string JUnitPath,
    string SourceCommitSha,
    string ReleaseInvocationId,
    string LaneInvocationId,
    string ApkSha256,
    string PackageId,
    string VersionCode,
    string VersionName,
    string SigningCertificateSha256,
    string RunnerSha256,
    string RunnerVersion,
    string LabPolicyId,
    string LabPolicySha256,
    string DeviceFingerprintSha256,
    string DeviceProductSha256,
    string DeviceHardwareSha256,
    string DeviceModelSha256,
    string DeviceKernelQemu,
    int DeviceSdk,
    string DeviceClass)
{
    internal const string Schema = "deep.survival.android-runner-result.v3";
    internal const string Version = "Deep Android Runner 3.0.0";
    internal const string E2ePackage = "network.xpoint.deep.e2e";
    internal const string ProductionPackage = "network.xpoint.deep";
    internal const string LauncherActivity = "network.xpoint.deep.DeepLauncher";
    private const int MaxPathLength = 1024;
    private static readonly Regex SerialPattern = new(
        "^[A-Za-z0-9._:-]{1,128}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex SafeVersionPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._+ -]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static RunnerOptions Parse(string[] args)
    {
        if (args.Length == 0 || args.Length % 2 != 0)
        {
            throw new RunnerConfigurationException("invalid-argument-count");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            var key = args[index];
            var value = args[index + 1];
            if (!RequiredKeys.Contains(key) ||
                !values.TryAdd(key, value) ||
                string.IsNullOrWhiteSpace(value) ||
                value.Contains('\r', StringComparison.Ordinal) ||
                value.Contains('\n', StringComparison.Ordinal))
            {
                throw new RunnerConfigurationException("invalid-argument");
            }
        }

        if (values.Count != RequiredKeys.Count)
        {
            throw new RunnerConfigurationException("missing-argument");
        }

        RequireMatch(values["--serial"], SerialPattern, "serial");
        RequireHex(values["--commit"], 40, "commit");
        RequireHex(values["--release-invocation"], 32, "release-invocation");
        RequireHex(values["--lane-invocation"], 32, "lane-invocation");
        RequireHex(values["--apk-sha256"], 64, "apk-sha256");
        RequireHex(values["--signing-cert-sha256"], 64, "signing-cert-sha256");
        RequireHex(values["--runner-sha256"], 64, "runner-sha256");
        RequireHex(values["--lab-policy-id"], 64, "lab-policy-id");
        RequireHex(values["--lab-policy-sha256"], 64, "lab-policy-sha256");
        RequireHex(values["--device-fingerprint-sha256"], 64, "device-fingerprint-sha256");
        RequireHex(values["--device-product-sha256"], 64, "device-product-sha256");
        RequireHex(values["--device-hardware-sha256"], 64, "device-hardware-sha256");
        RequireHex(values["--device-model-sha256"], 64, "device-model-sha256");

        if (!string.Equals(values["--package-id"], E2ePackage, StringComparison.Ordinal))
        {
            throw new RunnerConfigurationException("invalid-package");
        }

        if (!values["--version-code"].All(char.IsAsciiDigit) ||
            values["--version-code"].Length is 0 or > 10 ||
            !SafeVersionPattern.IsMatch(values["--version-name"]) ||
            !string.Equals(values["--runner-version"], Version, StringComparison.Ordinal))
        {
            throw new RunnerConfigurationException("invalid-version");
        }

        if (!int.TryParse(
                values["--device-sdk"],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var sdk) ||
            sdk is < 26 or > 100 ||
            !string.Equals(values["--device-kernel-qemu"], "0", StringComparison.Ordinal) ||
            !string.Equals(
                values["--device-class"],
                "physical-managed-dedicated",
                StringComparison.Ordinal))
        {
            throw new RunnerConfigurationException("invalid-device-policy");
        }

        var apk = RequireCanonicalFile(values["--apk"], "apk");
        var artifacts = RequireCanonicalDirectory(values["--artifacts"], "artifacts");
        var result = RequireContainedOutput(
            artifacts,
            values["--result"],
            Path.Combine("quarantine", "raw", "runner-result.json"),
            "result");
        var junit = RequireContainedOutput(
            artifacts,
            values["--junit"],
            Path.Combine("quarantine", "raw", "android-device.junit.xml"),
            "junit");

        return new RunnerOptions(
            values["--serial"],
            apk,
            artifacts,
            result,
            junit,
            values["--commit"],
            values["--release-invocation"],
            values["--lane-invocation"],
            values["--apk-sha256"],
            values["--package-id"],
            values["--version-code"],
            values["--version-name"],
            values["--signing-cert-sha256"],
            values["--runner-sha256"],
            values["--runner-version"],
            values["--lab-policy-id"],
            values["--lab-policy-sha256"],
            values["--device-fingerprint-sha256"],
            values["--device-product-sha256"],
            values["--device-hardware-sha256"],
            values["--device-model-sha256"],
            values["--device-kernel-qemu"],
            sdk,
            values["--device-class"]);
    }

    private static readonly HashSet<string> RequiredKeys = new(StringComparer.Ordinal)
    {
        "--serial",
        "--apk",
        "--artifacts",
        "--result",
        "--junit",
        "--commit",
        "--release-invocation",
        "--lane-invocation",
        "--apk-sha256",
        "--package-id",
        "--version-code",
        "--version-name",
        "--signing-cert-sha256",
        "--runner-sha256",
        "--runner-version",
        "--lab-policy-id",
        "--lab-policy-sha256",
        "--device-fingerprint-sha256",
        "--device-product-sha256",
        "--device-hardware-sha256",
        "--device-model-sha256",
        "--device-kernel-qemu",
        "--device-sdk",
        "--device-class"
    };

    private static void RequireHex(string value, int length, string code)
    {
        if (value.Length != length ||
            value.Any(character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'a' and <= 'f')) ||
            value.All(character => character == '0'))
        {
            throw new RunnerConfigurationException($"invalid-{code}");
        }
    }

    private static void RequireMatch(string value, Regex pattern, string code)
    {
        if (!pattern.IsMatch(value))
        {
            throw new RunnerConfigurationException($"invalid-{code}");
        }
    }

    private static string RequireCanonicalFile(string value, string code)
    {
        var path = RequireAbsolutePath(value, code);
        if (!File.Exists(path) || HasReparsePoint(path))
        {
            throw new RunnerConfigurationException($"invalid-{code}-path");
        }

        return path;
    }

    private static string RequireCanonicalDirectory(string value, string code)
    {
        var path = RequireAbsolutePath(value, code);
        if (!Directory.Exists(path) || HasReparsePoint(path))
        {
            throw new RunnerConfigurationException($"invalid-{code}-path");
        }

        return path.TrimEnd(Path.DirectorySeparatorChar);
    }

    private static string RequireContainedOutput(
        string root,
        string value,
        string requiredRelativePath,
        string code)
    {
        var path = RequireAbsolutePath(value, code);
        var required = Path.GetFullPath(Path.Combine(root, requiredRelativePath));
        if (!string.Equals(path, required, StringComparison.OrdinalIgnoreCase) ||
            !IsStrictlyContained(root, path) ||
            HasReparsePoint(Path.GetDirectoryName(path)!))
        {
            throw new RunnerConfigurationException($"invalid-{code}-path");
        }

        return path;
    }

    private static string RequireAbsolutePath(string value, string code)
    {
        if (value.Length > MaxPathLength || !Path.IsPathFullyQualified(value))
        {
            throw new RunnerConfigurationException($"invalid-{code}-path");
        }

        var path = Path.GetFullPath(value);
        if (!string.Equals(path, value, StringComparison.OrdinalIgnoreCase))
        {
            throw new RunnerConfigurationException($"noncanonical-{code}-path");
        }

        return path;
    }

    private static bool IsStrictlyContained(string root, string candidate) =>
        candidate.StartsWith(
            root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static bool HasReparsePoint(string path)
    {
        for (var current = Path.GetFullPath(path);
             !string.IsNullOrWhiteSpace(current);
             current = Path.GetDirectoryName(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        return false;
    }
}

internal sealed class RunnerConfigurationException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}
