using System.Diagnostics;

namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class StrictLanePowerShellTests
{
    [Fact]
    public async Task PathSafetyRejectsSiblingPrefixAndJunction()
    {
        await RunScriptAsync("Test-StrictLanePathSafety.ps1");
    }

    [Fact]
    public async Task AndroidRunnerExecutesAndEvidenceRejectsTamperingAndStaleness()
    {
        await RunScriptAsync("Test-AndroidRunnerContract.ps1");
    }

    [Fact]
    public async Task AndroidLabProvisioningRequiresProtectedSignedAllowlistedBundle()
    {
        await RunScriptAsync("Test-AndroidLabProvisioningContract.ps1");
    }

    [Fact]
    public async Task EvidenceIdentityRejectsCrossLaneMissingMismatchedAndDuplicateInvocations()
    {
        await RunScriptAsync("Test-StrictEvidenceIdentity.ps1");
    }

    private static async Task RunScriptAsync(string scriptName)
    {
        var root = FindWorkspaceRoot();
        var script = Path.Combine(
            root,
            "tests",
            "Deep.Client.Maui.SmokeTests",
            "Scripts",
            scriptName);
        var start = new ProcessStartInfo
        {
            FileName = FindPowerShell(),
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"{scriptName} failed ({process.ExitCode}).{Environment.NewLine}{output}{Environment.NewLine}{error}");
    }

    private static string FindWorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deep.Client.Maui.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Workspace root was not found.");
    }

    private static string FindPowerShell()
    {
        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var executable in new[] { "pwsh.exe", "powershell.exe" })
        {
            var found = pathEntries
                .Select(path => Path.Combine(path.Trim('"'), executable))
                .FirstOrDefault(File.Exists);
            if (found is not null)
            {
                return found;
            }
        }
        throw new FileNotFoundException("PowerShell was not found.");
    }
}
