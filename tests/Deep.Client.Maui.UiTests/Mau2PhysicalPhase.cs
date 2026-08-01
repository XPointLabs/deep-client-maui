using System.Text.RegularExpressions;

namespace Deep.Client.Maui.UiTests;

/// <summary>
/// Immutable contract for the opt-in physical MAU2 lane.  It deliberately has no
/// default phase: a missing or misspelled phase must never fall back to the old
/// destructive single-process acceptance test.
/// </summary>
public enum Mau2PhysicalPhase
{
    Attach,
    HappyPath,
    RestartDurability
}

internal static partial class Mau2PhysicalPhaseContract
{
    private const string PhaseEnvironmentKey = "DEEP_MAU2_E2E_PHASE";
    private const string RunStateEnvironmentKey = "DEEP_MAU2_E2E_RUN_STATE";

    internal static Mau2PhysicalPhase LoadRequired()
    {
        var raw = Environment.GetEnvironmentVariable(PhaseEnvironmentKey);
        if (string.IsNullOrWhiteSpace(raw) ||
            !Enum.TryParse<Mau2PhysicalPhase>(raw, ignoreCase: false, out var phase) ||
            !Enum.IsDefined(phase) ||
            !string.Equals(raw, phase.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Physical MAU2 E2E requires one exact DEEP_MAU2_E2E_PHASE: Attach, HappyPath, or RestartDurability.");
        }

        return phase;
    }

    internal static string GetResultFileName(Mau2PhysicalPhase phase) =>
        $"mau2-{phase.ToString().ToLowerInvariant()}-result.json";

    internal static string RequireSanitizedRunStatePath()
    {
        var raw = Environment.GetEnvironmentVariable(RunStateEnvironmentKey);
        if (string.IsNullOrWhiteSpace(raw) || !Path.IsPathFullyQualified(raw))
        {
            throw new InvalidOperationException("Physical MAU2 E2E requires an absolute DEEP_MAU2_E2E_RUN_STATE path.");
        }

        var path = Path.GetFullPath(raw);
        var parent = Directory.GetParent(path)?.Name;
        var runDirectory = Directory.GetParent(path)?.Parent;
        if (!string.Equals(Path.GetFileName(path), "run-state.json", StringComparison.Ordinal) ||
            !string.Equals(runDirectory?.Name, "e2e-runs", StringComparison.OrdinalIgnoreCase) ||
            parent is null ||
            !RunIdRegex().IsMatch(parent))
        {
            throw new InvalidOperationException("DEEP_MAU2_E2E_RUN_STATE must be the canonical e2e-runs/<32-hex>/run-state.json path.");
        }

        return path;
    }

    [GeneratedRegex("^[a-f0-9]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex RunIdRegex();
}
