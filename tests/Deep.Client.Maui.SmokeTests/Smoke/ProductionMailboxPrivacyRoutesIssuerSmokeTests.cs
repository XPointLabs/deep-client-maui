using System.Diagnostics;

namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class ProductionMailboxPrivacyRoutesIssuerSmokeTests
{
    [Fact]
    [Trait("RequiresWindows", "true")]
    public async Task IssuerValidatesSignsAndAtomicallyRotatesSyntheticArtifacts()
    {
        var root = FindWorkspaceRoot();
        var script = Path.Combine(
            root,
            "tests",
            "Deep.Client.Maui.SmokeTests",
            "Scripts",
            "Test-ProductionMailboxPrivacyRoutesIssuerContract.ps1");
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

        using var process = Process.Start(start) ??
            throw new InvalidOperationException("PowerShell did not start.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited while the timeout path was terminating it.
            }
            await process.WaitForExitAsync();
            throw new TimeoutException("Production privacy-route issuer contract timed out.");
        }

        var output = await outputTask;
        var error = await errorTask;
        Assert.True(
            process.ExitCode == 0,
            $"Issuer contract failed ({process.ExitCode}).{Environment.NewLine}" +
            $"{output}{Environment.NewLine}{error}");
        Assert.Contains(
            "Production mailbox privacy-route issuer contract: PASS",
            output,
            StringComparison.Ordinal);
    }

    private static string FindWorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "Deep.Client.Maui.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ??
            throw new DirectoryNotFoundException("Workspace root was not found.");
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
