using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deep.AndroidRunner;

namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class AndroidRunnerV3Tests
{
    [Fact]
    public void Options_require_exact_complete_canonical_contract()
    {
        using var fixture = RunnerFixture.Create();
        var options = RunnerOptions.Parse(fixture.Arguments);

        Assert.Equal("physical-serial:37123", options.Serial);
        Assert.Equal(RunnerOptions.E2ePackage, options.PackageId);
        Assert.Equal(fixture.ApkPath, options.ApkPath);

        var duplicate = fixture.Arguments
            .Concat(["--serial", "other"])
            .ToArray();
        Assert.Throws<RunnerConfigurationException>(() => RunnerOptions.Parse(duplicate));

        var unknown = (string[])fixture.Arguments.Clone();
        unknown[0] = "--unexpected";
        Assert.Throws<RunnerConfigurationException>(() => RunnerOptions.Parse(unknown));

        var escapedOutput = fixture.Replace(
            "--result",
            Path.Combine(fixture.Root, "runner-result.json"));
        Assert.Throws<RunnerConfigurationException>(() => RunnerOptions.Parse(escapedOutput));
    }

    [Fact]
    public void Options_require_android_9_or_newer_physical_device()
    {
        using var fixture = RunnerFixture.Create();

        Assert.Throws<RunnerConfigurationException>(() =>
            RunnerOptions.Parse(fixture.Replace("--device-sdk", "27")));
        Assert.Equal(28, RunnerOptions.Parse(
            fixture.Replace("--device-sdk", "28")).DeviceSdk);
    }

    [Fact]
    public void Ui_hierarchy_requires_one_exact_resource_and_bounded_valid_bounds()
    {
        const string resource = "network.xpoint.deep.e2e:id/Welcome.Create";
        var target = UiHierarchy.FindExactResource(
            $"noise<hierarchy><node resource-id=\"{resource}\" bounds=\"[10,20][110,220]\" /></hierarchy>",
            resource);

        Assert.Equal(60, target.X);
        Assert.Equal(120, target.Y);

        Assert.Throws<RunnerExecutionException>(() => UiHierarchy.FindExactResource(
            $"<hierarchy><node resource-id=\"{resource}\" bounds=\"[0,0][10,10]\" />" +
            $"<node resource-id=\"{resource}\" bounds=\"[20,20][30,30]\" /></hierarchy>",
            resource));
        Assert.ThrowsAny<Exception>(() => UiHierarchy.FindExactResource(
            "<!DOCTYPE hierarchy [<!ENTITY x SYSTEM \"file:///private\">]><hierarchy />",
            resource));
        Assert.Throws<RunnerExecutionException>(() => UiHierarchy.FindExactResource(
            $"<hierarchy><node resource-id=\"{resource}\" bounds=\"invalid\" /></hierarchy>",
            resource));
    }

    [Fact]
    public async Task Physical_flow_installs_clears_uses_exact_uiautomator_ids_and_removes_package()
    {
        using var fixture = RunnerFixture.Create();
        var options = RunnerOptions.Parse(fixture.Arguments);
        var adb = new FakeAdb(options);
        var run = new AndroidDeviceRun(options, adb, validateRunnerIdentity: false);

        var outcome = await run.ExecuteAsync(CancellationToken.None);

        Assert.True(outcome.Passed);
        Assert.Equal(2, outcome.Counters.Executed);
        Assert.Equal(2, outcome.Counters.Passed);
        Assert.Equal(0, outcome.Counters.Failed);
        Assert.Equal(0, outcome.Counters.Skipped);
        Assert.True(outcome.Device.AllPassed);
        Assert.Contains(adb.Calls, call => call.SequenceEqual(
            new[] { "-s", options.Serial, "install", "-r", "-t", options.ApkPath }));
        Assert.Contains(adb.Calls, call => call.SequenceEqual(
            new[] { "-s", options.Serial, "shell", "pm", "clear", RunnerOptions.E2ePackage }));
        Assert.Contains(adb.Calls, call => call.SequenceEqual(
            new[] { "-s", options.Serial, "uninstall", RunnerOptions.E2ePackage }));
        Assert.DoesNotContain(
            adb.Calls.SelectMany(call => call),
            argument => argument.Contains(';', StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failed_mutation_still_attempts_independent_package_cleanup()
    {
        using var fixture = RunnerFixture.Create();
        var options = RunnerOptions.Parse(fixture.Arguments);
        var adb = new FakeAdb(options) { FailInstall = true };
        var run = new AndroidDeviceRun(options, adb, validateRunnerIdentity: false);

        var outcome = await run.ExecuteAsync(CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.Equal(0, outcome.Counters.Executed);
        Assert.True(outcome.Device.TestPackageRemovedAfter);
        Assert.Contains(adb.Calls, call => call.SequenceEqual(
            new[] { "-s", options.Serial, "shell", "am", "force-stop", RunnerOptions.E2ePackage }));
        Assert.Contains(adb.Calls, call => call.SequenceEqual(
            new[] { "-s", options.Serial, "uninstall", RunnerOptions.E2ePackage }));
    }

    [Fact]
    public async Task Cleanup_budget_is_bounded_and_attempts_each_cleanup_step()
    {
        using var fixture = RunnerFixture.Create();
        var options = RunnerOptions.Parse(fixture.Arguments);
        var adb = new FakeAdb(options) { BlockCleanup = true };
        var run = new AndroidDeviceRun(
            options,
            adb,
            validateRunnerIdentity: false,
            cleanupBudget: TimeSpan.FromMilliseconds(50));

        var started = Stopwatch.GetTimestamp();
        var outcome = await run.ExecuteAsync(CancellationToken.None);
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.False(outcome.Passed);
        Assert.False(outcome.Device.TestPackageRemovedAfter);
        Assert.True(elapsed < TimeSpan.FromSeconds(2));
        Assert.Contains(adb.Calls, call => call.SequenceEqual(
            new[] { "-s", options.Serial, "shell", "am", "force-stop", RunnerOptions.E2ePackage }));
        Assert.Contains(adb.Calls, call => call.SequenceEqual(
            new[] { "-s", options.Serial, "shell", "pm", "clear", RunnerOptions.E2ePackage }));
        Assert.Contains(adb.Calls, call => call.SequenceEqual(
            new[] { "-s", options.Serial, "uninstall", RunnerOptions.E2ePackage }));
        Assert.Contains(adb.Calls, call => call.SequenceEqual(
            new[] { "-s", options.Serial, "shell", "pm", "list", "packages", RunnerOptions.E2ePackage }));
    }

    [Fact]
    public async Task Installed_package_metadata_requires_an_exact_version_name()
    {
        using var fixture = RunnerFixture.Create();
        var options = RunnerOptions.Parse(fixture.Arguments);
        var adb = new FakeAdb(options)
        {
            PackageVersionName = options.VersionName + "-untrusted"
        };
        var run = new AndroidDeviceRun(options, adb, validateRunnerIdentity: false);

        var outcome = await run.ExecuteAsync(CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.Equal("installed-package-metadata-mismatch", outcome.FailureCode);
        Assert.True(outcome.Device.TestPackageRemovedAfter);
    }

    [Fact]
    public void Evidence_is_v3_bound_and_junit_contains_only_counters()
    {
        using var fixture = RunnerFixture.Create();
        var options = RunnerOptions.Parse(fixture.Arguments);
        var counters = new TestCounters(2);
        counters.Begin();
        counters.Pass();
        counters.Begin();
        counters.Pass();
        var device = new DeviceAttestation
        {
            DedicatedManaged = true,
            PersonalDataAbsent = true,
            ProductionPackageAbsentBefore = true,
            TestPackageClearedBefore = true,
            TestPackageRemovedAfter = true
        };

        EvidenceWriter.Write(options, new RunnerOutcome(true, null, counters, device));

        var junit = File.ReadAllText(options.JUnitPath);
        Assert.Equal(
            "<testsuite tests=\"2\" failures=\"0\" errors=\"0\" skipped=\"0\" />",
            junit);
        Assert.DoesNotContain("system-out", junit, StringComparison.Ordinal);
        Assert.DoesNotContain("testcase", junit, StringComparison.Ordinal);

        using var result = JsonDocument.Parse(File.ReadAllText(options.ResultPath));
        Assert.Equal(RunnerOptions.Schema, result.RootElement.GetProperty("schema").GetString());
        Assert.Equal("passed", result.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            Sha256(options.JUnitPath),
            result.RootElement.GetProperty("junitSha256").GetString());
        Assert.True(
            result.RootElement.GetProperty("device").GetProperty("testPackageRemovedAfter").GetBoolean());
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private sealed class FakeAdb(RunnerOptions options) : IAdbClient
    {
        private int dumpCount;
        private int forceStopCount;
        private bool uninstalled;

        internal bool FailInstall { get; set; }
        internal bool BlockCleanup { get; set; }
        internal string? PackageVersionName { get; set; }
        internal List<string[]> Calls { get; } = [];

        public Task<CommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var args = arguments.ToArray();
            Calls.Add(args);
            cancellationToken.ThrowIfCancellationRequested();

            if (args.SequenceEqual(["-s", options.Serial, "shell", "am", "force-stop", RunnerOptions.E2ePackage]))
            {
                forceStopCount++;
                if (BlockCleanup && forceStopCount >= 2)
                {
                    return WaitForCancellationAsync(cancellationToken);
                }
            }

            if (args.SequenceEqual(["devices"]))
            {
                return Result($"List of devices attached\r\n{options.Serial}\tdevice\r\n");
            }
            if (args.SequenceEqual(["-s", options.Serial, "get-serialno"]))
            {
                return Result(options.Serial + "\n");
            }
            if (args.Length == 5 &&
                args[0] == "-s" &&
                args[1] == options.Serial &&
                args[2] == "shell" &&
                args[3] == "getprop")
            {
                return Result(GetProperty(args[4]) + "\n");
            }
            if (args.SequenceEqual(
                    ["-s", options.Serial, "shell", "pm", "list", "packages", RunnerOptions.ProductionPackage]))
            {
                return Result(string.Empty);
            }
            if (args.SequenceEqual(
                    ["-s", options.Serial, "shell", "pm", "list", "packages", "-3"]))
            {
                return Result($"package:{RunnerOptions.E2ePackage}\n");
            }
            if (args.SequenceEqual(["-s", options.Serial, "install", "-r", "-t", options.ApkPath]))
            {
                return FailInstall ? Result(string.Empty, 1) : Result("Success\n");
            }
            if (args.SequenceEqual(
                    ["-s", options.Serial, "shell", "pm", "path", RunnerOptions.E2ePackage]))
            {
                return Result($"package:/data/app/~~safe/{RunnerOptions.E2ePackage}-safe/base.apk\n");
            }
            if (args.Length == 5 &&
                args[2] == "shell" &&
                args[3] == "sha256sum")
            {
                return Result($"{options.ApkSha256}  {args[4]}\n");
            }
            if (args.SequenceEqual(
                    ["-s", options.Serial, "shell", "pm", "dump", RunnerOptions.E2ePackage]))
            {
                return Result(
                    $"versionCode={options.VersionCode} minSdk=28\n" +
                    $"versionName={PackageVersionName ?? options.VersionName}\n");
            }
            if (args.SequenceEqual(
                    ["-s", options.Serial, "shell", "pm", "clear", RunnerOptions.E2ePackage]))
            {
                return Result("Success\n");
            }
            if (args.Length >= 4 &&
                args[2] == "exec-out" &&
                args[3] == "uiautomator")
            {
                dumpCount++;
                return Result(dumpCount <= 2 ? WelcomeHierarchy() : ConversationsHierarchy());
            }
            if (args.SequenceEqual(["-s", options.Serial, "uninstall", RunnerOptions.E2ePackage]))
            {
                uninstalled = true;
                return Result("Success\n");
            }
            if (args.SequenceEqual(
                    ["-s", options.Serial, "shell", "pm", "list", "packages", RunnerOptions.E2ePackage]))
            {
                return Result(uninstalled ? string.Empty : $"package:{RunnerOptions.E2ePackage}\n");
            }

            return Result(string.Empty);
        }

        private string GetProperty(string property) => property switch
        {
            "ro.build.fingerprint" => RunnerFixture.Fingerprint,
            "ro.product.name" => RunnerFixture.Product,
            "ro.hardware" => RunnerFixture.Hardware,
            "ro.product.model" => RunnerFixture.Model,
            "ro.kernel.qemu" => "0",
            "ro.build.version.sdk" => "31",
            "ro.build.characteristics" => "phone",
            _ => throw new InvalidOperationException("Unexpected property.")
        };

        private static string WelcomeHierarchy() =>
            "<hierarchy>" +
            $"<node resource-id=\"{RunnerOptions.E2ePackage}:id/Welcome.DisplayName\" bounds=\"[10,20][310,120]\" />" +
            $"<node resource-id=\"{RunnerOptions.E2ePackage}:id/Welcome.Create\" bounds=\"[10,140][310,240]\" />" +
            "</hierarchy>";

        private static string ConversationsHierarchy() =>
            "<hierarchy>" +
            $"<node resource-id=\"{RunnerOptions.E2ePackage}:id/Conversations.Root\" bounds=\"[0,0][1080,2200]\" />" +
            "</hierarchy>";

        private static Task<CommandResult> Result(string output, int exitCode = 0) =>
            Task.FromResult(new CommandResult(exitCode, output, string.Empty));

        private static async Task<CommandResult> WaitForCancellationAsync(
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new CommandResult(0, string.Empty, string.Empty);
        }
    }

    private sealed class RunnerFixture : IDisposable
    {
        internal const string Fingerprint = "vendor/device/build:12/release-keys";
        internal const string Product = "physical_product";
        internal const string Hardware = "physical_hardware";
        internal const string Model = "Physical Model";

        private RunnerFixture(string root, string apkPath, string[] arguments)
        {
            Root = root;
            ApkPath = apkPath;
            Arguments = arguments;
        }

        internal string Root { get; }
        internal string ApkPath { get; }
        internal string[] Arguments { get; }

        internal static RunnerFixture Create()
        {
            var root = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "deep-android-runner-tests",
                Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(Path.Combine(root, "quarantine", "raw"));
            var apk = Path.Combine(root, "network.xpoint.deep.e2e-Signed.apk");
            File.WriteAllBytes(apk, [1, 2, 3, 4, 5]);
            var apkSha = Sha256(apk);
            var result = Path.Combine(root, "quarantine", "raw", "runner-result.json");
            var junit = Path.Combine(root, "quarantine", "raw", "android-device.junit.xml");
            var arguments = new[]
            {
                "--serial", "physical-serial:37123",
                "--apk", apk,
                "--artifacts", root,
                "--result", result,
                "--junit", junit,
                "--commit", new string('a', 40),
                "--release-invocation", new string('b', 32),
                "--lane-invocation", new string('c', 32),
                "--apk-sha256", apkSha,
                "--package-id", RunnerOptions.E2ePackage,
                "--version-code", "15",
                "--version-name", "0.2.9",
                "--signing-cert-sha256", new string('d', 64),
                "--runner-sha256", new string('e', 64),
                "--runner-version", RunnerOptions.Version,
                "--lab-policy-id", new string('f', 64),
                "--lab-policy-sha256", new string('1', 64),
                "--device-fingerprint-sha256", Hash(Fingerprint),
                "--device-product-sha256", Hash(Product),
                "--device-hardware-sha256", Hash(Hardware),
                "--device-model-sha256", Hash(Model),
                "--device-kernel-qemu", "0",
                "--device-sdk", "31",
                "--device-class", "physical-managed-dedicated"
            };
            return new RunnerFixture(root, apk, arguments);
        }

        internal string[] Replace(string key, string value)
        {
            var copy = (string[])Arguments.Clone();
            var index = Array.IndexOf(copy, key);
            Assert.True(index >= 0);
            copy[index + 1] = value;
            return copy;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static string Hash(string value) =>
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
