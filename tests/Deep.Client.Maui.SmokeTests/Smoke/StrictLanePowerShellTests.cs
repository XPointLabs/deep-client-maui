using System.Diagnostics;
using System.Security.Cryptography;

namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class StrictLanePowerShellTests
{
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromMinutes(20);

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
        var protectedRoot = Path.Combine(
            FindWorkspaceRoot(),
            ".secrets",
            "android-lab");
        var operatorSnapshot = SnapshotFiles(protectedRoot);
        await RunScriptAsync("Test-AndroidLabProvisioningContract.ps1");
        if (operatorSnapshot.Count != 0)
        {
            Assert.Equal(operatorSnapshot, SnapshotFiles(protectedRoot));
        }
    }

    [Fact]
    public async Task EvidenceIdentityRejectsCrossLaneMissingMismatchedAndDuplicateInvocations()
    {
        await RunScriptAsync("Test-StrictEvidenceIdentity.ps1");
    }

    [Fact]
    public async Task ScriptRunnerDrainsStandardOutputAndErrorConcurrently()
    {
        await RunScriptAsync(
            "Test-StrictLanePipeDrainContract.ps1",
            TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task ScriptRunnerTerminatesHungProcessTreeAtBound()
    {
        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => RunScriptAsync(
                "Test-StrictLaneTimeoutContract.ps1",
                TimeSpan.FromSeconds(3)));
        Assert.Contains(
            "TIMEOUT_CHILD_PID=",
            exception.Message,
            StringComparison.Ordinal);

        var marker = exception.Message
            .Split(["TIMEOUT_CHILD_PID="], StringSplitOptions.None)[1]
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0];
        Assert.True(int.TryParse(marker, out var childProcessId));
        await Task.Delay(250);
        Assert.False(IsProcessAlive(childProcessId));
    }

    private static async Task RunScriptAsync(
        string scriptName,
        TimeSpan? timeout = null)
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
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeoutSource = new CancellationTokenSource(timeout ?? ScriptTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The script exited between timeout observation and termination.
            }

            await process.WaitForExitAsync();
            var timedOutOutput = await outputTask;
            var timedOutError = await errorTask;
            throw new TimeoutException(
                $"{scriptName} exceeded {(timeout ?? ScriptTimeout).TotalSeconds:F0} seconds." +
                $"{Environment.NewLine}{timedOutOutput}{Environment.NewLine}{timedOutError}");
        }

        var output = await outputTask;
        var error = await errorTask;
        Assert.True(process.ExitCode == 0, $"{scriptName} failed ({process.ExitCode}).{Environment.NewLine}{output}{Environment.NewLine}{error}");
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static SortedDictionary<string, string> SnapshotFiles(string root)
    {
        var snapshot = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(root))
        {
            return snapshot;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            snapshot.Add(
                Path.GetRelativePath(root, path).Replace('\\', '/'),
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        }

        return snapshot;
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
