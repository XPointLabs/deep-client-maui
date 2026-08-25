using System.Security.AccessControl;
using System.Security.Principal;

namespace Deep.Client.Maui.UiTests;

public sealed class Mau2PhysicalPhaseTests
{
    [Theory]
    [InlineData("ProvisionIdentity", Mau2PhysicalPhase.ProvisionIdentity)]
    [InlineData("Attach", Mau2PhysicalPhase.Attach)]
    [InlineData("PayloadMatrix", Mau2PhysicalPhase.PayloadMatrix)]
    [InlineData("PrivacyFallback", Mau2PhysicalPhase.PrivacyFallback)]
    [InlineData("Call", Mau2PhysicalPhase.Call)]
    [InlineData("RestartDurability", Mau2PhysicalPhase.RestartDurability)]
    [InlineData("ManualResendAfterRestart", Mau2PhysicalPhase.ManualResendAfterRestart)]
    [InlineData("AutomaticRetryAfterRestart", Mau2PhysicalPhase.AutomaticRetryAfterRestart)]
    [InlineData("AckCrashWindow", Mau2PhysicalPhase.AckCrashWindow)]
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
    [InlineData(Mau2PhysicalPhase.PrivacyFallback)]
    [InlineData(Mau2PhysicalPhase.ManualResendAfterRestart)]
    [InlineData(Mau2PhysicalPhase.AutomaticRetryAfterRestart)]
    [InlineData(Mau2PhysicalPhase.AckCrashWindow)]
    public void Retry_and_ack_phases_are_the_only_https_chaos_phases(
        Mau2PhysicalPhase phase)
    {
        Mau2PhysicalPhaseContract.RequireChaosPhase(phase);
        Assert.Throws<InvalidOperationException>(() =>
            Mau2PhysicalPhaseContract.RequireChaosPhase(
                Mau2PhysicalPhase.PayloadMatrix));
    }

    [Fact]
    public void Windows_uat_reset_requires_exact_policy_and_invocation_binding()
    {
        const string policy =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string invocation = "0123456789abcdef0123456789abcdef";
        var previous = Environment.GetEnvironmentVariable(
            "DEEP_MAU2_E2E_UAT_RESET_BINDING");
        try
        {
            Environment.SetEnvironmentVariable(
                "DEEP_MAU2_E2E_UAT_RESET_BINDING", null);
            Assert.False(Mau2PhysicalPhaseContract.LoadWindowsUatResetAuthorization(
                Mau2PhysicalPhase.ProvisionIdentity, policy, invocation));

            Environment.SetEnvironmentVariable(
                "DEEP_MAU2_E2E_UAT_RESET_BINDING", $"{policy}:{invocation}");
            Assert.True(Mau2PhysicalPhaseContract.LoadWindowsUatResetAuthorization(
                Mau2PhysicalPhase.ProvisionIdentity, policy, invocation));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "DEEP_MAU2_E2E_UAT_RESET_BINDING", previous);
        }
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef:fedcba9876543210fedcba9876543210")]
    public void Windows_uat_reset_rejects_malformed_or_stale_binding(string binding)
    {
        const string policy =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string invocation = "0123456789abcdef0123456789abcdef";
        var previous = Environment.GetEnvironmentVariable(
            "DEEP_MAU2_E2E_UAT_RESET_BINDING");
        try
        {
            Environment.SetEnvironmentVariable(
                "DEEP_MAU2_E2E_UAT_RESET_BINDING", binding);
            Assert.Throws<InvalidOperationException>(() =>
                Mau2PhysicalPhaseContract.LoadWindowsUatResetAuthorization(
                    Mau2PhysicalPhase.ProvisionIdentity, policy, invocation));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "DEEP_MAU2_E2E_UAT_RESET_BINDING", previous);
        }
    }

    [Fact]
    public void Windows_uat_reset_is_rejected_outside_provisioning()
    {
        const string policy =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string invocation = "0123456789abcdef0123456789abcdef";
        var previous = Environment.GetEnvironmentVariable(
            "DEEP_MAU2_E2E_UAT_RESET_BINDING");
        try
        {
            Environment.SetEnvironmentVariable(
                "DEEP_MAU2_E2E_UAT_RESET_BINDING", $"{policy}:{invocation}");
            Assert.Throws<InvalidOperationException>(() =>
                Mau2PhysicalPhaseContract.LoadWindowsUatResetAuthorization(
                    Mau2PhysicalPhase.Attach, policy, invocation));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "DEEP_MAU2_E2E_UAT_RESET_BINDING", previous);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("legacy")]
    [InlineData("HappyPath ")]
    [InlineData("HappyPath")]
    [InlineData("VoiceMessage")]
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
