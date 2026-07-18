using System.Diagnostics;
using System.Text.Json;

namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class RoutedCompositionContractSmokeTests
{
    private const string RouterOne = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string RouterTwo = "2222222222222222222222222222222222222222222222222222222222222222";
    private const string RouterThree = "3333333333333333333333333333333333333333333333333333333333333333";
    private const string RouterFour = "4444444444444444444444444444444444444444444444444444444444444444";

    [Theory]
    [MemberData(nameof(LiveConfigurationCases))]
    public async Task StrictLivePreflight_ProcessRejectsEveryInvalidRoutedConfiguration(
        string caseName,
        string? routerUrls,
        string? storageUrl,
        bool expectedReady)
    {
        _ = caseName;
        var repositoryRoot = FindRepositoryRoot();
        var artifactDirectory = Path.Combine(
            repositoryRoot,
            "artifacts",
            "contract-tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = repositoryRoot
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(Path.Combine(repositoryRoot, "eng", "Invoke-StrictClientLane.ps1"));
            startInfo.ArgumentList.Add("-Lane");
            startInfo.ArgumentList.Add("LiveInfrastructure");
            startInfo.ArgumentList.Add("-ReleaseInvocationId");
            startInfo.ArgumentList.Add(Guid.NewGuid().ToString("N"));
            startInfo.ArgumentList.Add("-Bootstrap");
            startInfo.ArgumentList.Add("live");
            startInfo.ArgumentList.Add("-ValidateLiveConfigurationOnly");
            startInfo.ArgumentList.Add("-ArtifactDirectory");
            startInfo.ArgumentList.Add(artifactDirectory);
            SetEnvironment(startInfo, "XNODE_URLS", routerUrls);
            SetEnvironment(startInfo, "DEEP_STORAGE_URL", storageUrl);
            SetEnvironment(startInfo, "DEEP_FILE_URL", "https://files.example/");
            SetEnvironment(startInfo, "DEEP_PUSH_URL", "https://push.example/");
            SetEnvironment(startInfo, "DEEP_CALL_SIGNALING_BASE_URL", "https://calls.example/");

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process!.WaitForExitAsync(timeout.Token);
            var standardOutput = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            var standardError = await process.StandardError.ReadToEndAsync(timeout.Token);
            var expectedExitCode = expectedReady ? 0 : 2;
            Assert.True(
                process.ExitCode == expectedExitCode,
                $"{caseName}: expected exit {expectedExitCode}, got {process.ExitCode}.{Environment.NewLine}" +
                $"stdout:{Environment.NewLine}{standardOutput}{Environment.NewLine}stderr:{Environment.NewLine}{standardError}");

            var preflightPath = Path.Combine(artifactDirectory, "preflight-liveinfrastructure.json");
            Assert.True(File.Exists(preflightPath), $"{caseName}: preflight JSON was not written.");
            using var preflight = JsonDocument.Parse(await File.ReadAllTextAsync(preflightPath, timeout.Token));
            Assert.Equal(
                expectedReady ? "ready" : "failed",
                preflight.RootElement.GetProperty("status").GetString());

            var checks = preflight.RootElement
                .GetProperty("checks")
                .EnumerateArray()
                .ToDictionary(
                    static check => check.GetProperty("name").GetString()!,
                    static check => check.GetProperty("status").GetString()!,
                    StringComparer.Ordinal);
            Assert.Equal(5, checks.Count);
            Assert.Contains("routed-message-endpoint", checks.Keys);
            Assert.Contains("direct-storage-absent", checks.Keys);
            Assert.Equal("passed", checks["DEEP_FILE_URL"]);
            Assert.Equal("passed", checks["DEEP_PUSH_URL"]);
            Assert.Equal("passed", checks["DEEP_CALL_SIGNALING_BASE_URL"]);
            Assert.Equal(
                string.IsNullOrWhiteSpace(storageUrl) ? "passed" : "blocked",
                checks["direct-storage-absent"]);
            Assert.Equal(
                expectedReady || !string.IsNullOrWhiteSpace(storageUrl) ? "passed" : "blocked",
                checks["routed-message-endpoint"]);
        }
        finally
        {
            if (Directory.Exists(artifactDirectory))
            {
                Directory.Delete(artifactDirectory, recursive: true);
            }
        }
    }

    public static TheoryData<string, string?, string?, bool> LiveConfigurationCases => new()
    {
        { "exact-three-storage-absent", ValidRouters(), null, true },
        { "literal-loopback-http", Join($"{RouterOne}|http://127.0.0.1:29281/", Router(2), Router(3)), null, true },
        { "direct-storage-present", ValidRouters(), "https://storage.example/", false },
        { "zero-routers", null, null, false },
        { "two-routers", Join(Router(1), Router(2)), null, false },
        { "four-routers", Join(Router(1), Router(2), Router(3), Router(4)), null, false },
        { "uppercase-router-id", Join($"{new string('A', 64)}|https://router-one.example/", Router(2), Router(3)), null, false },
        { "duplicate-router-id", Join(Router(1), $"{RouterOne}|https://router-two.example/", Router(3)), null, false },
        { "duplicate-router-url", Join(Router(1), $"{RouterTwo}|https://router-one.example/", Router(3)), null, false },
        { "malformed-router-url", Join($"{RouterOne}|not-a-url", Router(2), Router(3)), null, false },
        { "router-userinfo", Join($"{RouterOne}|https://user@router-one.example/", Router(2), Router(3)), null, false },
        { "router-query", Join($"{RouterOne}|https://router-one.example/?query=value", Router(2), Router(3)), null, false },
        { "router-fragment", Join($"{RouterOne}|https://router-one.example/#fragment", Router(2), Router(3)), null, false },
        { "router-non-root-path", Join($"{RouterOne}|https://router-one.example/base", Router(2), Router(3)), null, false },
        { "hostname-loopback-http", Join($"{RouterOne}|http://localhost:29281/", Router(2), Router(3)), null, false },
        { "remote-cleartext-http", Join($"{RouterOne}|http://router-one.example/", Router(2), Router(3)), null, false }
    };

    private static string ValidRouters() => Join(Router(1), Router(2), Router(3));

    private static string Router(int index) => index switch
    {
        1 => $"{RouterOne}|https://router-one.example/",
        2 => $"{RouterTwo}|https://router-two.example/",
        3 => $"{RouterThree}|https://router-three.example/",
        4 => $"{RouterFour}|https://router-four.example/",
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    private static string Join(params string[] values) => string.Join(';', values);

    private static void SetEnvironment(ProcessStartInfo startInfo, string name, string? value)
    {
        if (value is null)
        {
            startInfo.Environment.Remove(name);
        }
        else
        {
            startInfo.Environment[name] = value;
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "Deep.Client.Maui.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
