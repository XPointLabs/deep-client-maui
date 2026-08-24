using System.Security.AccessControl;
using System.Security.Principal;

namespace Deep.Client.Maui.UiTests;

public sealed class Mau2PhysicalPhaseTests
{
    [Theory]
    [InlineData("ProvisionIdentity", Mau2PhysicalPhase.ProvisionIdentity)]
    [InlineData("Attach", Mau2PhysicalPhase.Attach)]
    [InlineData("HappyPath", Mau2PhysicalPhase.HappyPath)]
    [InlineData("VoiceMessage", Mau2PhysicalPhase.VoiceMessage)]
    [InlineData("RestartDurability", Mau2PhysicalPhase.RestartDurability)]
    [InlineData("ManualResendAfterRestart", Mau2PhysicalPhase.ManualResendAfterRestart)]
    [InlineData("AutomaticRetryAfterRestart", Mau2PhysicalPhase.AutomaticRetryAfterRestart)]
    [InlineData("NegativeRuntime", Mau2PhysicalPhase.NegativeRuntime)]
    public void Exact_phase_names_are_accepted_without_destructive_default(string value, Mau2PhysicalPhase expected)
    {
        var previous = Environment.GetEnvironmentVariable("DEEP_MAU2_E2E_PHASE");
        try
        {
            Environment.SetEnvironmentVariable("DEEP_MAU2_E2E_PHASE", value);
            Assert.Equal(expected, Mau2PhysicalPhaseContract.LoadRequired());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEEP_MAU2_E2E_PHASE", previous);
        }
    }

    [Theory]
    [InlineData(Mau2PhysicalPhase.ManualResendAfterRestart)]
    [InlineData(Mau2PhysicalPhase.AutomaticRetryAfterRestart)]
    public void Restart_resend_phases_fail_closed_without_supported_chaos_evidence(
        Mau2PhysicalPhase phase)
    {
        var previousEvidence = Environment.GetEnvironmentVariable("DEEP_MAU2_SUPPORTED_CHAOS_EVIDENCE");
        var previousProvider = Environment.GetEnvironmentVariable("DEEP_MAU2_SUPPORTED_CHAOS_PROVIDER");
        try
        {
            Environment.SetEnvironmentVariable("DEEP_MAU2_SUPPORTED_CHAOS_EVIDENCE", null);
            Environment.SetEnvironmentVariable("DEEP_MAU2_SUPPORTED_CHAOS_PROVIDER", null);

            Assert.Throws<InvalidOperationException>(() =>
                Mau2PhysicalPhaseContract.RequireSupportedChaosEvidence(phase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEEP_MAU2_SUPPORTED_CHAOS_EVIDENCE", previousEvidence);
            Environment.SetEnvironmentVariable("DEEP_MAU2_SUPPORTED_CHAOS_PROVIDER", previousProvider);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("legacy")]
    [InlineData("HappyPath ")]
    [InlineData("negativeRuntime")]
    public void Missing_or_noncanonical_phase_fails_closed(string? value)
    {
        var previous = Environment.GetEnvironmentVariable("DEEP_MAU2_E2E_PHASE");
        try
        {
            Environment.SetEnvironmentVariable("DEEP_MAU2_E2E_PHASE", value);
            Assert.Throws<InvalidOperationException>(() => Mau2PhysicalPhaseContract.LoadRequired());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEEP_MAU2_E2E_PHASE", previous);
        }
    }

    [Fact]
    public void Run_state_must_be_a_canonical_sanitized_e2e_runs_path()
    {
        var previous = Environment.GetEnvironmentVariable("DEEP_MAU2_E2E_RUN_STATE");
        var previousRoot = Environment.GetEnvironmentVariable("DEEP_MAU2_E2E_RUNS_ROOT");
        var root = Path.Combine(Path.GetTempPath(), $"deep-mau2-run-{Guid.NewGuid():N}");
        try
        {
            var runsRoot = Path.Combine(root, "e2e-runs");
            var runRoot = Path.Combine(runsRoot, new string('a', 32));
            Directory.CreateDirectory(runRoot);
            var runState = Path.Combine(runRoot, "run-state.json");
            File.WriteAllText(runState, "{}");
            SetExactAcl(runsRoot, isDirectory: true);
            SetExactAcl(runRoot, isDirectory: true);
            SetExactAcl(runState, isDirectory: false);
            Assert.EndsWith(
                "run-state.json",
                Mau2PhysicalPhaseContract.ValidateProtectedRunStatePath(
                    runState, runsRoot),
                StringComparison.Ordinal);

            Environment.SetEnvironmentVariable("DEEP_MAU2_E2E_RUNS_ROOT", runsRoot);
            Environment.SetEnvironmentVariable("DEEP_MAU2_E2E_RUN_STATE", Path.Combine(Path.GetTempPath(), "run-state.json"));
            Assert.Throws<InvalidOperationException>(Mau2PhysicalPhaseContract.RequireSanitizedRunStatePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEEP_MAU2_E2E_RUN_STATE", previous);
            Environment.SetEnvironmentVariable("DEEP_MAU2_E2E_RUNS_ROOT", previousRoot);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Run_state_cannot_escape_the_runner_owned_root()
    {
        var root = Path.Combine(Path.GetTempPath(), $"deep-mau2-root-{Guid.NewGuid():N}");
        try
        {
            var actualRoot = Path.Combine(root, "actual", "e2e-runs");
            var wrongRoot = Path.Combine(root, "wrong", "e2e-runs");
            var runRoot = Path.Combine(actualRoot, new string('b', 32));
            Directory.CreateDirectory(runRoot);
            Directory.CreateDirectory(wrongRoot);
            var state = Path.Combine(runRoot, "run-state.json");
            File.WriteAllText(state, "{}");
            SetExactAcl(actualRoot, isDirectory: true);
            SetExactAcl(wrongRoot, isDirectory: true);
            SetExactAcl(runRoot, isDirectory: true);
            SetExactAcl(state, isDirectory: false);

            Assert.Throws<InvalidOperationException>(() =>
                Mau2PhysicalPhaseContract.ValidateProtectedRunStatePath(
                    state, wrongRoot));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Run_state_rejects_a_reparse_ancestor()
    {
        var root = Path.Combine(Path.GetTempPath(), $"deep-mau2-link-{Guid.NewGuid():N}");
        try
        {
            var runsRoot = Path.Combine(root, "e2e-runs");
            var target = Path.Combine(root, "target");
            Directory.CreateDirectory(runsRoot);
            Directory.CreateDirectory(target);
            SetExactAcl(runsRoot, isDirectory: true);
            var state = Path.Combine(target, "run-state.json");
            File.WriteAllText(state, "{}");
            var linkedRun = Path.Combine(runsRoot, new string('c', 32));
            Directory.CreateSymbolicLink(linkedRun, target);

            Assert.Throws<InvalidOperationException>(() =>
                Mau2PhysicalPhaseContract.ValidateProtectedRunStatePath(
                    Path.Combine(linkedRun, "run-state.json"), runsRoot));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Run_state_rejects_an_unprotected_runs_root_anchor()
    {
        var root = Path.Combine(Path.GetTempPath(), $"deep-mau2-anchor-{Guid.NewGuid():N}");
        try
        {
            var runsRoot = Path.Combine(root, "e2e-runs");
            var runRoot = Path.Combine(runsRoot, new string('d', 32));
            Directory.CreateDirectory(runRoot);
            var state = Path.Combine(runRoot, "run-state.json");
            File.WriteAllText(state, "{}");
            SetExactAcl(runRoot, isDirectory: true);
            SetExactAcl(state, isDirectory: false);

            Assert.Throws<InvalidOperationException>(() =>
                Mau2PhysicalPhaseContract.ValidateProtectedRunStatePath(
                    state, runsRoot));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Negative_case_revalidates_the_full_chain_before_each_launch()
    {
        var root = Path.Combine(Path.GetTempPath(), $"deep-mau2-case-{Guid.NewGuid():N}");
        try
        {
            var runsRoot = Path.Combine(root, "e2e-runs");
            var runRoot = Path.Combine(runsRoot, new string('e', 32));
            var fixtureRoot = Path.Combine(runRoot, "negative-runtime");
            var caseRoot = Path.Combine(fixtureRoot, "tampered-signature");
            var runtimeRoot = Path.Combine(caseRoot, "mailbox-runtime-v1");
            Directory.CreateDirectory(runtimeRoot);
            var state = Path.Combine(runRoot, "run-state.json");
            File.WriteAllText(state, "{}");
            foreach (var directory in new[] { runsRoot, runRoot, fixtureRoot, caseRoot, runtimeRoot })
                SetExactAcl(directory, isDirectory: true);
            SetExactAcl(state, isDirectory: false);

            Assert.Equal(
                caseRoot,
                Mau2PhysicalPhaseContract.ValidateProtectedNegativeCase(
                    state, "tampered-signature", runsRoot));

            AddUnexpectedAcl(runsRoot);

            Assert.Throws<InvalidOperationException>(() =>
                Mau2PhysicalPhaseContract.ValidateProtectedNegativeCase(
                    state, "tampered-signature", runsRoot));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void SetExactAcl(string path, bool isDirectory)
    {
        var owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Current Windows SID is unavailable.");
        FileSystemSecurity security = isDirectory
            ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
            includeExplicit: true, includeInherited: true,
            targetType: typeof(SecurityIdentifier)))
        {
            security.RemoveAccessRuleAll(rule);
        }
        foreach (var sid in new[]
        {
            owner,
            new SecurityIdentifier("S-1-5-18"),
            new SecurityIdentifier("S-1-5-32-544")
        })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid, FileSystemRights.FullControl, InheritanceFlags.None,
                PropagationFlags.None, AccessControlType.Allow));
        }
        if (isDirectory)
            new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        else
            new FileInfo(path).SetAccessControl((FileSecurity)security);
    }

    private static void AddUnexpectedAcl(string path)
    {
        var security = new DirectoryInfo(path).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier("S-1-1-0"), FileSystemRights.Read,
            InheritanceFlags.None, PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
}
