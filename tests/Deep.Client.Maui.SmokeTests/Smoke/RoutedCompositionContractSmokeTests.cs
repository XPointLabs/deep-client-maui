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
        var result = await RunPreflightAsync(new Dictionary<string, string?>
        {
            ["XNODE_URLS"] = routerUrls,
            ["DEEP_STORAGE_URL"] = storageUrl
        });
        var expectedExitCode = expectedReady ? 0 : 2;
        Assert.True(
            result.ExitCode == expectedExitCode,
            $"{caseName}: expected exit {expectedExitCode}, got {result.ExitCode}.{Environment.NewLine}" +
            $"stdout:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}" +
            $"stderr:{Environment.NewLine}{result.StandardError}");

        var expectedChecks = PassedChecks();
        expectedChecks["direct-storage-absent"] =
            string.IsNullOrWhiteSpace(storageUrl) ? "passed" : "blocked";
        expectedChecks["routed-message-endpoint"] =
            expectedReady || !string.IsNullOrWhiteSpace(storageUrl) ? "passed" : "blocked";
        AssertMachineReadableContract(result, expectedReady ? "ready" : "failed", expectedChecks);
    }

    [Theory]
    [MemberData(nameof(InvalidServiceUrlCases))]
    public async Task StrictLivePreflight_ProcessRejectsAdversarialServiceUrls(
        string settingName,
        string invalidValue)
    {
        var result = await RunPreflightAsync(new Dictionary<string, string?>
        {
            [settingName] = invalidValue
        });

        Assert.True(
            result.ExitCode == 2,
            $"{settingName}={invalidValue}: expected exit 2, got {result.ExitCode}.{Environment.NewLine}" +
            $"stdout:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}" +
            $"stderr:{Environment.NewLine}{result.StandardError}");
        var expectedChecks = PassedChecks();
        expectedChecks[settingName] = "blocked";
        AssertMachineReadableContract(result, "failed", expectedChecks);
    }

    [Fact]
    public async Task StrictLivePreflight_AlwaysRejectsLanHttp()
    {
        var lanRouters = string.Join(';',
            $"{RouterOne}|http://192.168.1.44:41801/",
            $"{RouterTwo}|http://192.168.1.44:41802/",
            $"{RouterThree}|http://192.168.1.44:41803/");
        const string lanService = "http://192.168.1.44:41810/api";
        var lanConfiguration = new Dictionary<string, string?>
        {
            ["XNODE_URLS"] = lanRouters,
            ["DEEP_FILE_URL"] = lanService,
            ["DEEP_PUSH_URL"] = lanService,
            ["DEEP_CALL_SIGNALING_BASE_URL"] = lanService
        };

        var defaultResult = await RunPreflightAsync(lanConfiguration);
        Assert.Equal(2, defaultResult.ExitCode);
        var blockedChecks = PassedChecks();
        blockedChecks["routed-message-endpoint"] = "blocked";
        blockedChecks["DEEP_FILE_URL"] = "blocked";
        blockedChecks["DEEP_PUSH_URL"] = "blocked";
        blockedChecks["DEEP_CALL_SIGNALING_BASE_URL"] = "blocked";
        AssertMachineReadableContract(defaultResult, "failed", blockedChecks);
        Assert.Equal(
            "XNODE_URLS must contain between three and sixteen distinct <64-lowerhex-routerId>|<url> entries; " +
            "required and must use HTTPS or explicit loopback HTTP",
            GetPreflightCheckDetail(defaultResult, "routed-message-endpoint"));
        Assert.Equal(
            "required and must use HTTPS or explicit loopback HTTP",
            GetPreflightCheckDetail(defaultResult, "DEEP_FILE_URL"));

        lanConfiguration["DEEP_STRICT_LIVE_PHYSICAL_E2E"] = "1";
        var physicalResult = await RunPreflightAsync(lanConfiguration);
        Assert.Equal(2, physicalResult.ExitCode);
        AssertMachineReadableContract(physicalResult, "failed", blockedChecks);
    }

    [Fact]
    public void StrictWorkflow_RestoresAndBuildsReleaseTestsBeforeNoRestoreLiveLane()
    {
        var workflow = ReadWorkspaceFile(".github", "workflows", "strict-release-evidence.yml");
        var prepare = workflow.IndexOf(
            "- name: Prepare clean-runner Release live acceptance",
            StringComparison.Ordinal);
        var live = workflow.IndexOf(
            "- name: Strict pinned-router live acceptance",
            StringComparison.Ordinal);

        Assert.True(prepare >= 0 && live > prepare);
        var preparationStep = workflow[prepare..live];
        Assert.Contains(
            "dotnet restore tests/Deep.Client.Maui.ViewModels.Tests/Deep.Client.Maui.ViewModels.Tests.csproj",
            preparationStep,
            StringComparison.Ordinal);
        Assert.Contains("-p:Configuration=Release", preparationStep, StringComparison.Ordinal);
        Assert.Contains(
            "dotnet build tests/Deep.Client.Maui.ViewModels.Tests/Deep.Client.Maui.ViewModels.Tests.csproj",
            preparationStep,
            StringComparison.Ordinal);
        Assert.Contains("--configuration Release --no-restore", preparationStep, StringComparison.Ordinal);

        var lane = ReadWorkspaceFile("eng", "Invoke-StrictClientLane.ps1");
        var interactiveDesktop = lane.IndexOf(
            "Add-Check 'interactive-desktop'",
            StringComparison.Ordinal);
        var windowsRunRoot = lane.IndexOf(
            "$runRoot = Join-Path $ArtifactDirectory",
            StringComparison.Ordinal);
        Assert.True(interactiveDesktop >= 0 && windowsRunRoot > interactiveDesktop);
        Assert.Contains(
            "WindowsInteractiveSessionProbe]::IsCurrentSessionUnlocked()",
            lane,
            StringComparison.Ordinal);

        var liveTest = lane.IndexOf(
            "$env:DEEP_STRICT_LIVE = '1'",
            StringComparison.Ordinal);
        Assert.True(liveTest >= 0);
        var liveTestCommand = lane[liveTest..];
        Assert.Contains("--configuration Release", liveTestCommand, StringComparison.Ordinal);
        Assert.Contains("--no-restore", liveTestCommand, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false, "failed", "passed")]
    [InlineData(false, true, "passed", "failed")]
    public async Task ReleaseGuardEvidence_PreservesIndependentPartialFailureStatuses(
        bool verifierFailure,
        bool factoryFailure,
        string expectedVerifierStatus,
        string expectedFactoryStatus)
    {
        var failureSwitch = (verifierFailure, factoryFailure) switch
        {
            (true, false) => "-ContractOnlyVerifierFailure",
            (false, true) => "-ContractOnlyFactoryFailure",
            _ => throw new InvalidOperationException("Exactly one partial failure is required.")
        };
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
            foreach (var argument in new[]
                     {
                         "-NoLogo",
                         "-NoProfile",
                         "-File",
                         Path.Combine(repositoryRoot, "eng", "Test-ReleaseRoutedComposition.ps1"),
                         "-ArtifactDirectory",
                         artifactDirectory,
                         failureSwitch
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }
            startInfo.Environment["DEEP_RELEASE_GUARD_CONTRACT_TEST"] = "1";

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process!.WaitForExitAsync(timeout.Token);
            var standardOutput = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            var standardError = await process.StandardError.ReadToEndAsync(timeout.Token);
            Assert.True(
                process.ExitCode == 1,
                $"Expected partial failure exit 1, got {process.ExitCode}.{Environment.NewLine}" +
                $"stdout:{Environment.NewLine}{standardOutput}{Environment.NewLine}" +
                $"stderr:{Environment.NewLine}{standardError}");

            var evidencePath = Path.Combine(
                artifactDirectory,
                "release-routed-composition.json");
            Assert.True(File.Exists(evidencePath));
            using var evidence = JsonDocument.Parse(
                await File.ReadAllTextAsync(evidencePath, timeout.Token));
            Assert.Equal("failed", evidence.RootElement.GetProperty("status").GetString());
            var checks = evidence.RootElement
                .GetProperty("checks")
                .EnumerateArray()
                .ToDictionary(
                    static check => check.GetProperty("name").GetString()!,
                    static check => check.GetProperty("status").GetString()!,
                    StringComparer.Ordinal);
            Assert.Equal(expectedVerifierStatus, checks["compiled-maui-program-routed-di"]);
            Assert.Equal(expectedFactoryStatus, checks["release-production-factory-behavior"]);
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
        { "six-pinned-routers-storage-absent", ValidRouters(), null, true },
        { "literal-loopback-http", Join($"{RouterOne}|http://127.0.0.1:29281/", Router(2), Router(3), Router(4), Router(5), Router(6)), null, true },
        { "direct-storage-present", ValidRouters(), "https://storage.example/", false },
        { "zero-routers", null, null, false },
        { "two-routers", Join(Router(1), Router(2)), null, false },
        { "four-routers", Join(Router(1), Router(2), Router(3), Router(4)), null, true },
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

    public static TheoryData<string, string> InvalidServiceUrlCases
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var name in new[]
                     {
                         "DEEP_FILE_URL",
                         "DEEP_PUSH_URL",
                         "DEEP_CALL_SIGNALING_BASE_URL"
                     })
            {
                data.Add(name, "http://remote.example/");
                data.Add(name, "https://user@service.example/");
                data.Add(name, "https://service.example/?query=value");
                data.Add(name, "https://service.example/#fragment");
                data.Add(name, "http://localhost:18103/");
            }

            return data;
        }
    }

    private static string ValidRouters() => Join(Router(1), Router(2), Router(3), Router(4), Router(5), Router(6));

    private static string Router(int index) => index switch
    {
        1 => $"{RouterOne}|https://router-one.example/",
        2 => $"{RouterTwo}|https://router-two.example/",
        3 => $"{RouterThree}|https://router-three.example/",
        4 => $"{RouterFour}|https://router-four.example/",
        5 => $"{new string('5', 64)}|https://router-five.example/",
        6 => $"{new string('6', 64)}|https://router-six.example/",
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

    private static async Task<PreflightProcessResult> RunPreflightAsync(
        IReadOnlyDictionary<string, string?> overrides)
    {
        var repositoryRoot = FindRepositoryRoot();
        var artifactDirectory = Path.Combine(
            repositoryRoot,
            "artifacts",
            "contract-tests",
            Guid.NewGuid().ToString("N"));
        var releaseInvocationId = Guid.NewGuid().ToString("N");
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
            foreach (var argument in new[]
                     {
                         "-NoLogo",
                         "-NoProfile",
                         "-File",
                         Path.Combine(repositoryRoot, "eng", "Invoke-StrictClientLane.ps1"),
                         "-Lane",
                         "LiveInfrastructure",
                         "-ReleaseInvocationId",
                         releaseInvocationId,
                         "-Bootstrap",
                         "live",
                         "-ValidateLiveConfigurationOnly",
                         "-ArtifactDirectory",
                         artifactDirectory
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["XNODE_URLS"] = ValidRouters(),
                ["DEEP_STORAGE_URL"] = null,
                ["DEEP_FILE_URL"] = "https://files.example/",
                ["DEEP_PUSH_URL"] = "https://push.example/",
                ["DEEP_CALL_SIGNALING_BASE_URL"] = "https://calls.example/",
                ["DEEP_STRICT_LIVE_PHYSICAL_E2E"] = null
            };
            foreach (var (name, value) in overrides)
            {
                environment[name] = value;
            }
            foreach (var (name, value) in environment)
            {
                SetEnvironment(startInfo, name, value);
            }

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process!.WaitForExitAsync(timeout.Token);
            var standardOutput = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            var standardError = await process.StandardError.ReadToEndAsync(timeout.Token);
            var preflightPath = Path.Combine(artifactDirectory, "preflight-liveinfrastructure.json");
            var resultPath = Path.Combine(artifactDirectory, "result-liveinfrastructure.json");
            Assert.True(File.Exists(preflightPath), "Preflight JSON was not written.");
            Assert.True(File.Exists(resultPath), "Lane result JSON was not written.");
            return new PreflightProcessResult(
                process.ExitCode,
                standardOutput,
                standardError,
                releaseInvocationId,
                await File.ReadAllTextAsync(preflightPath, timeout.Token),
                await File.ReadAllTextAsync(resultPath, timeout.Token));
        }
        finally
        {
            if (Directory.Exists(artifactDirectory))
            {
                Directory.Delete(artifactDirectory, recursive: true);
            }
        }
    }

    private static Dictionary<string, string> PassedChecks() => new(StringComparer.Ordinal)
    {
        ["routed-message-endpoint"] = "passed",
        ["direct-storage-absent"] = "passed",
        ["DEEP_FILE_URL"] = "passed",
        ["DEEP_PUSH_URL"] = "passed",
        ["DEEP_CALL_SIGNALING_BASE_URL"] = "passed"
    };

    private static string GetPreflightCheckDetail(
        PreflightProcessResult result,
        string checkName)
    {
        using var preflight = JsonDocument.Parse(result.PreflightJson);
        return preflight.RootElement
            .GetProperty("checks")
            .EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == checkName)
            .GetProperty("detail")
            .GetString()!;
    }

    private static void AssertMachineReadableContract(
        PreflightProcessResult result,
        string expectedPreflightStatus,
        IReadOnlyDictionary<string, string> expectedChecks)
    {
        using var preflight = JsonDocument.Parse(result.PreflightJson);
        var root = preflight.RootElement;
        Assert.Equal(
            new[]
            {
                "checks",
                "generatedAtUtc",
                "lane",
                "laneInvocationId",
                "programRevisionSha",
                "releaseInvocationId",
                "schema",
                "sourceCommitSha",
                "status"
            },
            root.EnumerateObject().Select(static property => property.Name).Order().ToArray());
        Assert.Equal("deep.survival.strict-preflight.v1", root.GetProperty("schema").GetString());
        Assert.Matches("^[0-9a-f]{40}$", root.GetProperty("sourceCommitSha").GetString()!);
        Assert.Equal(result.ReleaseInvocationId, root.GetProperty("releaseInvocationId").GetString());
        Assert.Matches("^[0-9a-f]{32}$", root.GetProperty("laneInvocationId").GetString()!);
        Assert.Equal("LiveInfrastructure", root.GetProperty("lane").GetString());
        Assert.Equal(expectedPreflightStatus, root.GetProperty("status").GetString());
        Assert.True(DateTimeOffset.TryParse(root.GetProperty("generatedAtUtc").GetString(), out _));

        var checks = root.GetProperty("checks").EnumerateArray().ToArray();
        Assert.Equal(expectedChecks.Count, checks.Length);
        foreach (var check in checks)
        {
            Assert.Equal(
                new[] { "detail", "name", "status" },
                check.EnumerateObject().Select(static property => property.Name).Order().ToArray());
            var name = check.GetProperty("name").GetString()!;
            Assert.True(expectedChecks.ContainsKey(name), $"Unexpected preflight check: {name}");
            Assert.Equal(expectedChecks[name], check.GetProperty("status").GetString());
            Assert.False(string.IsNullOrWhiteSpace(check.GetProperty("detail").GetString()));
        }

        using var laneResult = JsonDocument.Parse(result.LaneResultJson);
        var laneRoot = laneResult.RootElement;
        Assert.Equal(
            new[]
            {
                "counters",
                "evidence",
                "evidenceSha256",
                "generatedAtUtc",
                "lane",
                "laneInvocationId",
                "programRevisionSha",
                "releaseInvocationId",
                "schema",
                "sourceCommitSha",
                "status"
            },
            laneRoot.EnumerateObject().Select(static property => property.Name).Order().ToArray());
        Assert.Equal("deep.survival.strict-lane-result.v1", laneRoot.GetProperty("schema").GetString());
        Assert.Equal(root.GetProperty("sourceCommitSha").GetString(), laneRoot.GetProperty("sourceCommitSha").GetString());
        Assert.Equal(result.ReleaseInvocationId, laneRoot.GetProperty("releaseInvocationId").GetString());
        Assert.Equal(root.GetProperty("laneInvocationId").GetString(), laneRoot.GetProperty("laneInvocationId").GetString());
        Assert.Equal("LiveInfrastructure", laneRoot.GetProperty("lane").GetString());
        Assert.Equal(expectedPreflightStatus == "ready" ? "passed" : "blocked", laneRoot.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, laneRoot.GetProperty("counters").ValueKind);
        Assert.Equal(
            expectedPreflightStatus == "ready" ? "live configuration contract" : "machine-readable preflight",
            laneRoot.GetProperty("evidence").GetString());
        Assert.Equal(string.Empty, laneRoot.GetProperty("evidenceSha256").GetString());
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

    private static string ReadWorkspaceFile(params string[] parts) =>
        File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. parts]));

    private sealed record PreflightProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        string ReleaseInvocationId,
        string PreflightJson,
        string LaneResultJson);
}
