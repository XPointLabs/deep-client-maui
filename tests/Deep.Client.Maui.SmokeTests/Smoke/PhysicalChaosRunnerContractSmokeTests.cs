using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class PhysicalChaosRunnerContractSmokeTests
{
    [Fact]
    public async Task Bounded_runner_kills_immediate_descendants_and_output_floods()
    {
        var root = FindRepositoryRoot();
        var script = Path.Combine(root, "tests", "Deep.Client.Maui.SmokeTests",
            "Scripts", "Test-PhysicalChaosRunnerSafety.ps1");
        var start = new ProcessStartInfo
        {
            FileName = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("PowerShell did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Physical chaos bounded-runner contract hung.");
        }
        Assert.True(process.ExitCode == 0,
            $"Bounded runner failed.{Environment.NewLine}{await stdout}{Environment.NewLine}{await stderr}");
    }

    [Fact]
    public void Runner_uses_closed_authority_and_outer_aggregating_cleanup()
    {
        var root = FindRepositoryRoot();
        var runner = File.ReadAllText(Path.Combine(
            root, "eng", "Invoke-PhysicalMau2CrossPlatform.ps1"));
        var manifestPath = Path.Combine(root, "eng", "physical-chaos-dependencies.v1.json");
        var manifestHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(manifestPath)));

        Assert.Contains($"$chaosManifestSha256 = '{manifestHash}'", runner, StringComparison.Ordinal);
        Assert.Contains("CreateSuspended | CreateNoWindow", runner, StringComparison.Ordinal);
        Assert.True(runner.IndexOf("AssignProcessToJobObject", StringComparison.Ordinal)
            < runner.IndexOf("ResumeThread(information.ThreadHandle)", StringComparison.Ordinal));
        Assert.Contains("MaximumOutputBytes = 1024 * 1024", runner, StringComparison.Ordinal);
        Assert.Contains("foreach ($attempt in 1..3)", runner, StringComparison.Ordinal);
        Assert.Contains("Physical MAU2 phase failed with audited cleanup results.", runner,
            StringComparison.Ordinal);
        Assert.Contains("DEEP_E2E_CHAOS_MANIFEST", runner, StringComparison.Ordinal);
        Assert.Contains(
            "$env:DEEP_PHYSICAL_E2E_HAPROXY_CONFIG_PATH = $script:chaosExecutionAuthority.HAProxyConfig",
            runner, StringComparison.Ordinal);
        Assert.Contains("$env:DEEP_PHYSICAL_E2E_HAPROXY_CONFIG_PATH = $null", runner,
            StringComparison.Ordinal);
        Assert.DoesNotContain("DEEP_E2E_CHAOS_SCRIPT_SHA256", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("DEEP_E2E_CHAOS_SCRIPT =", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("& dotnet test", runner, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"git\s+-C\s+\$devOpsRoot", RegexOptions.CultureInvariant), runner);

        var compose = File.ReadAllText(Path.Combine(
            root, "..", "deep-devops", "docker-compose.survival-uat-tls.dev.yml"));
        Assert.Contains(
            "source: ${DEEP_PHYSICAL_E2E_HAPROXY_CONFIG_PATH:-./config/survival-uat-tls/haproxy.cfg}",
            compose, StringComparison.Ordinal);

        var snapshotIndex = runner.IndexOf(
            "$script:chaosExecutionAuthority = New-ChaosDependencySnapshot",
            StringComparison.Ordinal);
        var chaosOnlyIndex = runner.IndexOf("if ($chaosPhase) {", snapshotIndex,
            StringComparison.Ordinal);
        var dockerHealthIndex = runner.IndexOf("Assert-DockerHealthy", chaosOnlyIndex,
            StringComparison.Ordinal);
        Assert.True(snapshotIndex >= 0 && chaosOnlyIndex > snapshotIndex
            && dockerHealthIndex > chaosOnlyIndex,
            "Every physical phase must initialize the pinned execution snapshot before Docker health validation; only mutation remains chaos-only.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "Deep.Client.Maui.slnx")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("MAUI repository root was not found.");
    }
}
