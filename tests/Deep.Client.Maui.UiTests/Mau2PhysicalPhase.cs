using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace Deep.Client.Maui.UiTests;

/// <summary>
/// Immutable contract for the opt-in physical MAU2 lane.  It deliberately has no
/// default phase: a missing or misspelled phase must never fall back to the old
/// destructive single-process acceptance test.
/// </summary>
public enum Mau2PhysicalPhase
{
    ProvisionIdentity,
    Attach,
    HappyPath,
    VoiceMessage,
    Call,
    RestartDurability,
    ManualResendAfterRestart,
    AutomaticRetryAfterRestart,
    NegativeRuntime
}

internal static partial class Mau2PhysicalPhaseContract
{
    private const string PhaseEnvironmentKey = "DEEP_MAU2_E2E_PHASE";
    private const string RunStateEnvironmentKey = "DEEP_MAU2_E2E_RUN_STATE";
    private const string RunsRootEnvironmentKey = "DEEP_MAU2_E2E_RUNS_ROOT";
    private const string ChaosEvidenceEnvironmentKey = "DEEP_MAU2_SUPPORTED_CHAOS_EVIDENCE";
    private const string ChaosProviderEnvironmentKey = "DEEP_MAU2_SUPPORTED_CHAOS_PROVIDER";
    private const string SupportedChaosProvider = "deep-devops-survival-chaos-v1";
    private const string CanonicalRunsRoot =
        @"C:\Work\DeepSession\secrets\mailbox-bootstrap\e2e-runs";

    internal static Mau2PhysicalPhase LoadRequired()
    {
        var raw = Environment.GetEnvironmentVariable(PhaseEnvironmentKey);
        if (string.IsNullOrWhiteSpace(raw) ||
            !Enum.TryParse<Mau2PhysicalPhase>(raw, ignoreCase: false, out var phase) ||
            !Enum.IsDefined(phase) ||
            !string.Equals(raw, phase.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Physical MAU2 E2E requires one exact DEEP_MAU2_E2E_PHASE: ProvisionIdentity, Attach, HappyPath, VoiceMessage, Call, RestartDurability, ManualResendAfterRestart, AutomaticRetryAfterRestart, or NegativeRuntime.");
        }

        return phase;
    }

    internal static string GetResultFileName(Mau2PhysicalPhase phase) =>
        $"mau2-{phase.ToString().ToLowerInvariant()}-result.json";

    internal static string RequireSupportedChaosEvidence(Mau2PhysicalPhase phase)
    {
        if (phase is not (Mau2PhysicalPhase.ManualResendAfterRestart
            or Mau2PhysicalPhase.AutomaticRetryAfterRestart))
        {
            throw new InvalidOperationException("This MAU2 phase does not accept chaos evidence.");
        }

        var provider = Environment.GetEnvironmentVariable(ChaosProviderEnvironmentKey);
        var rawPath = Environment.GetEnvironmentVariable(ChaosEvidenceEnvironmentKey);
        if (!string.Equals(provider, SupportedChaosProvider, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(rawPath)
            || !Path.IsPathFullyQualified(rawPath))
        {
            throw new InvalidOperationException(
                "Restart resend phases require explicit evidence from the supported Deep DevOps chaos provider.");
        }

        var path = Path.GetFullPath(rawPath);
        if (!File.Exists(path)
            || (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidOperationException("Supported chaos evidence must be an existing regular file.");
        }

        return path;
    }

    internal static string RequireSanitizedRunStatePath()
    {
        var runsRootRaw = Environment.GetEnvironmentVariable(RunsRootEnvironmentKey);
        if (string.IsNullOrWhiteSpace(runsRootRaw) ||
            !Path.IsPathFullyQualified(runsRootRaw) ||
            !string.Equals(
                Path.GetFullPath(runsRootRaw).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(CanonicalRunsRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Physical MAU2 E2E requires the canonical runner-owned DEEP_MAU2_E2E_RUNS_ROOT.");
        }
        var raw = Environment.GetEnvironmentVariable(RunStateEnvironmentKey);
        if (string.IsNullOrWhiteSpace(raw) || !Path.IsPathFullyQualified(raw))
        {
            throw new InvalidOperationException("Physical MAU2 E2E requires an absolute DEEP_MAU2_E2E_RUN_STATE path.");
        }

        return ValidateProtectedRunStatePath(raw, runsRootRaw);
    }

    internal static string ValidateProtectedRunStatePath(
        string pathValue,
        string runsRootValue)
    {
        var runsRoot = Path.GetFullPath(runsRootValue)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(pathValue);
        var runDirectory = Directory.GetParent(path);
        if (!Directory.Exists(runsRoot) || runDirectory is null ||
            !string.Equals(Path.GetFileName(path), "run-state.json", StringComparison.Ordinal) ||
            !string.Equals(runDirectory.Parent?.FullName, runsRoot,
                StringComparison.OrdinalIgnoreCase) ||
            !RunIdRegex().IsMatch(runDirectory.Name) ||
            !File.Exists(path) ||
            (File.GetAttributes(path) &
                (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidOperationException(
                "DEEP_MAU2_E2E_RUN_STATE must be the existing canonical e2e-runs/<32-hex>/run-state.json regular file.");
        }

        AssertNoReparseToAnchor(path, runsRoot);
        AssertExactProtectedAcl(runsRoot, isDirectory: true);
        AssertExactProtectedAcl(runDirectory.FullName, isDirectory: true);
        AssertExactProtectedAcl(path, isDirectory: false);
        return path;
    }

    internal static string RequireProtectedNegativeCase(
        string runStatePath,
        string caseName) => ValidateProtectedNegativeCase(
            runStatePath, caseName, CanonicalRunsRoot);

    internal static string ValidateProtectedNegativeCase(
        string runStatePath,
        string caseName,
        string runsRoot)
    {
        if (!NegativeCaseRegex().IsMatch(caseName))
            throw new InvalidOperationException("Negative runtime case is not allowlisted.");
        var protectedRunState = ValidateProtectedRunStatePath(
            runStatePath, runsRoot);
        var runRoot = Directory.GetParent(protectedRunState)?.FullName
            ?? throw new InvalidOperationException("Physical run state has no parent directory.");
        var fixtureRoot = Path.Combine(runRoot, "negative-runtime");
        var caseRoot = Path.Combine(fixtureRoot, caseName);
        var runtimeRoot = Path.Combine(caseRoot, "mailbox-runtime-v1");
        foreach (var directory in new[] { fixtureRoot, caseRoot, runtimeRoot })
        {
            if (!Directory.Exists(directory))
                throw new InvalidOperationException(
                    "A required protected negative runtime fixture is absent.");
            AssertNoReparseToAnchor(directory, runRoot);
            AssertExactProtectedAcl(directory, isDirectory: true);
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(
            caseRoot, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    "Negative runtime fixture contains a reparse point.");
            AssertExactProtectedAcl(entry,
                isDirectory: (attributes & FileAttributes.Directory) != 0);
        }
        return caseRoot;
    }

    private static void AssertNoReparseToAnchor(string path, string anchor)
    {
        var expectedAnchor = Path.GetFullPath(anchor)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var current = Path.GetFullPath(path);;
             current = Directory.GetParent(current)?.FullName ?? string.Empty)
        {
            if (string.IsNullOrEmpty(current) ||
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    "Physical run path traverses a reparse point or escapes its anchor.");
            if (string.Equals(current, expectedAnchor,
                    StringComparison.OrdinalIgnoreCase)) return;
        }
    }

    private static void AssertExactProtectedAcl(string path, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Physical MAU2 protected fixture validation requires Windows.");
        var owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Current Windows SID is unavailable.");
        FileSystemSecurity security = isDirectory
            ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();
        var actualOwner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            owner.Value, "S-1-5-18", "S-1-5-32-544"
        };
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>().ToArray();
        if (!security.AreAccessRulesProtected || actualOwner is null ||
            !actualOwner.Equals(owner) || rules.Length != expected.Count ||
            rules.Any(rule => rule.IsInherited ||
                rule.AccessControlType != AccessControlType.Allow ||
                rule.FileSystemRights != FileSystemRights.FullControl ||
                rule.InheritanceFlags != InheritanceFlags.None ||
                rule.PropagationFlags != PropagationFlags.None ||
                rule.IdentityReference is not SecurityIdentifier sid ||
                !expected.Remove(sid.Value)))
        {
            throw new InvalidOperationException(
                "Physical run path does not have the exact protected DACL.");
        }
    }

    [GeneratedRegex("^[a-f0-9]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex RunIdRegex();

    [GeneratedRegex("^(tampered-signature|missing-authority|android-runtime-on-windows)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex NegativeCaseRegex();
}
