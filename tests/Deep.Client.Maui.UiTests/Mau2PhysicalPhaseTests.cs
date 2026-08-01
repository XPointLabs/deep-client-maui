namespace Deep.Client.Maui.UiTests;

public sealed class Mau2PhysicalPhaseTests
{
    [Theory]
    [InlineData("Attach", Mau2PhysicalPhase.Attach)]
    [InlineData("HappyPath", Mau2PhysicalPhase.HappyPath)]
    [InlineData("RestartDurability", Mau2PhysicalPhase.RestartDurability)]
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
    [InlineData(null)]
    [InlineData("")]
    [InlineData("legacy")]
    [InlineData("HappyPath ")]
    [InlineData("NegativeRuntime")]
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
        try
        {
            Environment.SetEnvironmentVariable(
                "DEEP_MAU2_E2E_RUN_STATE",
                Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory)!, "secrets", "mailbox-bootstrap", "e2e-runs", new string('a', 32), "run-state.json"));
            Assert.EndsWith("run-state.json", Mau2PhysicalPhaseContract.RequireSanitizedRunStatePath(), StringComparison.Ordinal);

            Environment.SetEnvironmentVariable("DEEP_MAU2_E2E_RUN_STATE", Path.Combine(Path.GetTempPath(), "run-state.json"));
            Assert.Throws<InvalidOperationException>(Mau2PhysicalPhaseContract.RequireSanitizedRunStatePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEEP_MAU2_E2E_RUN_STATE", previous);
        }
    }
}
