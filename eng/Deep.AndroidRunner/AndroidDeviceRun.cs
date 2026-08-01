using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Deep.AndroidRunner;

internal sealed class AndroidDeviceRun
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultCleanupBudget = TimeSpan.FromSeconds(60);
    private readonly RunnerOptions options;
    private readonly IAdbClient adb;
    private readonly bool validateRunnerIdentity;
    private readonly TimeSpan cleanupBudget;
    private readonly TestCounters counters = new(total: 2);

    internal AndroidDeviceRun(
        RunnerOptions options,
        IAdbClient adb,
        bool validateRunnerIdentity = true,
        TimeSpan? cleanupBudget = null)
    {
        this.options = options;
        this.adb = adb;
        this.validateRunnerIdentity = validateRunnerIdentity;
        this.cleanupBudget = cleanupBudget ?? DefaultCleanupBudget;
        if (this.cleanupBudget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cleanupBudget));
        }
    }

    internal async Task<RunnerOutcome> ExecuteAsync(CancellationToken cancellationToken)
    {
        var state = new DeviceAttestation();
        var mutationAttempted = false;
        string? failureCode = null;
        try
        {
            ValidateLocalInputs();
            await ValidateDeviceAsync(state, cancellationToken).ConfigureAwait(false);

            mutationAttempted = true;
            await RequireSuccessAsync(
                ["-s", options.Serial, "install", "-r", "-t", options.ApkPath],
                InstallTimeout,
                "install-failed",
                cancellationToken).ConfigureAwait(false);
            await ValidateInstalledPackageAsync(cancellationToken).ConfigureAwait(false);

            await RequireSuccessOutputAsync(
                ["-s", options.Serial, "shell", "pm", "clear", RunnerOptions.E2ePackage],
                "Success",
                ShortTimeout,
                "clear-before-failed",
                cancellationToken).ConfigureAwait(false);
            state.TestPackageClearedBefore = true;

            counters.Begin();
            await StartApplicationAsync(cancellationToken).ConfigureAwait(false);
            var displayName = await WaitForResourceAsync(
                Resource("Welcome.DisplayName"),
                UiTimeout,
                cancellationToken).ConfigureAwait(false);
            await TapAsync(displayName, cancellationToken).ConfigureAwait(false);
            await RequireSuccessAsync(
                ["-s", options.Serial, "shell", "input", "text", "DeepE2E"],
                ShortTimeout,
                "type-display-name-failed",
                cancellationToken).ConfigureAwait(false);
            var create = await WaitForResourceAsync(
                Resource("Welcome.Create"),
                UiTimeout,
                cancellationToken).ConfigureAwait(false);
            await TapAsync(create, cancellationToken).ConfigureAwait(false);
            _ = await WaitForResourceAsync(
                Resource("Conversations.Root"),
                UiTimeout,
                cancellationToken).ConfigureAwait(false);
            counters.Pass();

            counters.Begin();
            await RequireSuccessAsync(
                ["-s", options.Serial, "shell", "am", "force-stop", RunnerOptions.E2ePackage],
                ShortTimeout,
                "force-stop-failed",
                cancellationToken).ConfigureAwait(false);
            await StartApplicationAsync(cancellationToken).ConfigureAwait(false);
            _ = await WaitForResourceAsync(
                Resource("Conversations.Root"),
                UiTimeout,
                cancellationToken).ConfigureAwait(false);
            var hierarchy = await DumpHierarchyAsync(cancellationToken).ConfigureAwait(false);
            if (UiHierarchy.ContainsExactResource(hierarchy, Resource("Welcome.Create")))
            {
                throw new RunnerExecutionException("cold-restart-auth-state-lost");
            }
            counters.Pass();
        }
        catch (RunnerExecutionException exception)
        {
            counters.FailCurrent();
            failureCode = exception.Code;
        }
        catch (OperationCanceledException)
        {
            counters.FailCurrent();
            failureCode = "runner-timeout";
        }
        catch
        {
            counters.FailCurrent();
            failureCode = "runner-internal-failure";
        }
        finally
        {
            if (mutationAttempted)
            {
                state.TestPackageRemovedAfter =
                    await CleanupPackageAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        var passed = failureCode is null &&
            state.AllPassed &&
            counters.Executed == counters.Total &&
            counters.Failed == 0;
        return new RunnerOutcome(
            passed,
            failureCode,
            counters,
            state);
    }

    private void ValidateLocalInputs()
    {
        if (!string.Equals(Sha256File(options.ApkPath), options.ApkSha256, StringComparison.Ordinal))
        {
            throw new RunnerExecutionException("apk-hash-mismatch");
        }

        if (!validateRunnerIdentity)
        {
            return;
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) ||
            !string.Equals(
                Path.GetFileName(processPath),
                "deep-android-runner.exe",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Sha256File(processPath), options.RunnerSha256, StringComparison.Ordinal))
        {
            throw new RunnerExecutionException("runner-self-hash-mismatch");
        }
    }

    private async Task ValidateInstalledPackageAsync(CancellationToken cancellationToken)
    {
        var pathResult = await RequireSuccessAsync(
            ["-s", options.Serial, "shell", "pm", "path", RunnerOptions.E2ePackage],
            ShortTimeout,
            "installed-package-path-failed",
            cancellationToken).ConfigureAwait(false);
        var pathLines = pathResult.StandardOutput.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pathLines.Length != 1 ||
            !pathLines[0].StartsWith("package:/data/app/", StringComparison.Ordinal) ||
            !pathLines[0].EndsWith("/base.apk", StringComparison.Ordinal))
        {
            throw new RunnerExecutionException("installed-package-path-invalid");
        }

        var installedPath = pathLines[0][8..];
        if (installedPath.Length > 512 ||
            installedPath.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '/' and not '.' and not '_' and not '-' and not '=' and not '~'))
        {
            throw new RunnerExecutionException("installed-package-path-invalid");
        }

        var hashOutput = await SingleValueAsync(
            ["-s", options.Serial, "shell", "sha256sum", installedPath],
            "installed-package-hash-failed",
            cancellationToken).ConfigureAwait(false);
        var hashParts = hashOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (hashParts.Length < 1 ||
            !string.Equals(hashParts[0], options.ApkSha256, StringComparison.Ordinal))
        {
            throw new RunnerExecutionException("installed-package-hash-mismatch");
        }

        var packageDump = await RequireSuccessAsync(
            ["-s", options.Serial, "shell", "pm", "dump", RunnerOptions.E2ePackage],
            ShortTimeout,
            "installed-package-metadata-failed",
            cancellationToken).ConfigureAwait(false);
        var metadataLines = packageDump.StandardOutput.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var expectedVersionCode = $"versionCode={options.VersionCode}";
        if (!metadataLines.Any(line =>
                string.Equals(line, expectedVersionCode, StringComparison.Ordinal) ||
                line.StartsWith(expectedVersionCode + " ", StringComparison.Ordinal)) ||
            !metadataLines.Contains(
                $"versionName={options.VersionName}",
                StringComparer.Ordinal))
        {
            throw new RunnerExecutionException("installed-package-metadata-mismatch");
        }
    }

    private async Task ValidateDeviceAsync(
        DeviceAttestation state,
        CancellationToken cancellationToken)
    {
        var devices = await RequireSuccessAsync(
            ["devices"],
            ShortTimeout,
            "devices-query-failed",
            cancellationToken).ConfigureAwait(false);
        var exactDevices = devices.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(line => string.Equals(line, $"{options.Serial}\tdevice", StringComparison.Ordinal) ||
                           string.Equals(line, $"{options.Serial} device", StringComparison.Ordinal));
        if (exactDevices != 1)
        {
            throw new RunnerExecutionException("device-not-unique");
        }

        var serial = await SingleValueAsync(
            ["-s", options.Serial, "get-serialno"],
            "serial-query-failed",
            cancellationToken).ConfigureAwait(false);
        var fingerprint = await GetPropAsync("ro.build.fingerprint", cancellationToken).ConfigureAwait(false);
        var product = await GetPropAsync("ro.product.name", cancellationToken).ConfigureAwait(false);
        var hardware = await GetPropAsync("ro.hardware", cancellationToken).ConfigureAwait(false);
        var model = await GetPropAsync("ro.product.model", cancellationToken).ConfigureAwait(false);
        var qemu = await GetPropAsync("ro.kernel.qemu", cancellationToken).ConfigureAwait(false);
        var sdk = await GetPropAsync("ro.build.version.sdk", cancellationToken).ConfigureAwait(false);
        var characteristics = await GetPropAsync("ro.build.characteristics", cancellationToken)
            .ConfigureAwait(false);

        if (!string.Equals(serial, options.Serial, StringComparison.Ordinal) ||
            !HashMatches(fingerprint, options.DeviceFingerprintSha256) ||
            !HashMatches(product, options.DeviceProductSha256) ||
            !HashMatches(hardware, options.DeviceHardwareSha256) ||
            !HashMatches(model, options.DeviceModelSha256) ||
            !string.Equals(qemu, options.DeviceKernelQemu, StringComparison.Ordinal) ||
            !string.Equals(
                sdk,
                options.DeviceSdk.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal) ||
            ContainsEmulatorMarker(characteristics) ||
            ContainsEmulatorMarker(product) ||
            ContainsEmulatorMarker(hardware) ||
            ContainsEmulatorMarker(model))
        {
            throw new RunnerExecutionException("device-binding-mismatch");
        }

        state.DedicatedManaged = true;

        var productionPackages = await RequireSuccessAsync(
            ["-s", options.Serial, "shell", "pm", "list", "packages", RunnerOptions.ProductionPackage],
            ShortTimeout,
            "production-package-query-failed",
            cancellationToken).ConfigureAwait(false);
        state.ProductionPackageAbsentBefore = !ParsePackages(productionPackages.StandardOutput)
            .Contains(RunnerOptions.ProductionPackage, StringComparer.Ordinal);
        if (!state.ProductionPackageAbsentBefore)
        {
            throw new RunnerExecutionException("production-package-present");
        }

        var thirdPartyPackages = await RequireSuccessAsync(
            ["-s", options.Serial, "shell", "pm", "list", "packages", "-3"],
            ShortTimeout,
            "personal-package-query-failed",
            cancellationToken).ConfigureAwait(false);
        var unexpected = ParsePackages(thirdPartyPackages.StandardOutput)
            .Where(package => !string.Equals(package, RunnerOptions.E2ePackage, StringComparison.Ordinal))
            .Take(1)
            .Any();
        state.PersonalDataAbsent = !unexpected;
        if (!state.PersonalDataAbsent)
        {
            throw new RunnerExecutionException("personal-packages-present");
        }
    }

    private async Task StartApplicationAsync(CancellationToken cancellationToken)
    {
        await RequireSuccessAsync(
            [
                "-s", options.Serial, "shell", "am", "start", "-W", "-n",
                $"{RunnerOptions.E2ePackage}/{RunnerOptions.LauncherActivity}"
            ],
            ShortTimeout,
            "application-start-failed",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<UiTarget> WaitForResourceAsync(
        string resourceId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hierarchy = await DumpHierarchyAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return UiHierarchy.FindExactResource(hierarchy, resourceId);
            }
            catch (RunnerExecutionException exception)
                when (exception.Code == "uiautomator-selector-missing")
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new RunnerExecutionException("uiautomator-wait-timeout");
    }

    private async Task<string> DumpHierarchyAsync(CancellationToken cancellationToken)
    {
        var result = await RequireSuccessAsync(
            ["-s", options.Serial, "exec-out", "uiautomator", "dump", "/dev/tty"],
            ShortTimeout,
            "uiautomator-dump-failed",
            cancellationToken).ConfigureAwait(false);
        return result.StandardOutput;
    }

    private async Task TapAsync(UiTarget target, CancellationToken cancellationToken)
    {
        await RequireSuccessAsync(
            [
                "-s", options.Serial, "shell", "input", "tap",
                target.X.ToString(CultureInfo.InvariantCulture),
                target.Y.ToString(CultureInfo.InvariantCulture)
            ],
            ShortTimeout,
            "uiautomator-tap-failed",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> CleanupPackageAsync(CancellationToken cancellationToken)
    {
        using var budgetSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetSource.CancelAfter(cleanupBudget);
        var budgetToken = budgetSource.Token;

        var forceStop = await TryCleanupCommandAsync(
            ["-s", options.Serial, "shell", "am", "force-stop", RunnerOptions.E2ePackage],
            ShortTimeout,
            budgetToken).ConfigureAwait(false);
        var clear = await TryCleanupCommandAsync(
            ["-s", options.Serial, "shell", "pm", "clear", RunnerOptions.E2ePackage],
            ShortTimeout,
            budgetToken).ConfigureAwait(false);
        var uninstall = await TryCleanupCommandAsync(
            ["-s", options.Serial, "uninstall", RunnerOptions.E2ePackage],
            InstallTimeout,
            budgetToken).ConfigureAwait(false);
        var query = await TryCleanupCommandAsync(
            ["-s", options.Serial, "shell", "pm", "list", "packages", RunnerOptions.E2ePackage],
            ShortTimeout,
            budgetToken).ConfigureAwait(false);

        try
        {
            return forceStop is not null &&
                forceStop.ExitCode == 0 &&
                clear is not null &&
                clear.ExitCode == 0 &&
                string.Equals(clear.StandardOutput.Trim(), "Success", StringComparison.Ordinal) &&
                uninstall is not null &&
                uninstall.ExitCode == 0 &&
                string.Equals(uninstall.StandardOutput.Trim(), "Success", StringComparison.Ordinal) &&
                query is not null &&
                query.ExitCode == 0 &&
                !ParsePackages(query.StandardOutput).Contains(
                    RunnerOptions.E2ePackage,
                    StringComparer.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private async Task<CommandResult?> TryCleanupCommandAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            return await adb.RunAsync(arguments, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Continue through every cleanup step. The final attestation remains false
            // unless all commands and the absence query completed successfully.
            return null;
        }
    }

    private async Task<string> GetPropAsync(
        string property,
        CancellationToken cancellationToken) =>
        await SingleValueAsync(
            ["-s", options.Serial, "shell", "getprop", property],
            $"getprop-{property.Replace('.', '-')}-failed",
            cancellationToken).ConfigureAwait(false);

    private async Task<string> SingleValueAsync(
        IReadOnlyList<string> arguments,
        string failureCode,
        CancellationToken cancellationToken)
    {
        var result = await RequireSuccessAsync(
            arguments,
            ShortTimeout,
            failureCode,
            cancellationToken).ConfigureAwait(false);
        var lines = result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 1 || lines[0].Length is 0 or > 1024)
        {
            throw new RunnerExecutionException(failureCode);
        }

        return lines[0];
    }

    private async Task<CommandResult> RequireSuccessAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string failureCode,
        CancellationToken cancellationToken)
    {
        var result = await adb.RunAsync(arguments, timeout, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new RunnerExecutionException(failureCode);
        }

        return result;
    }

    private async Task RequireSuccessOutputAsync(
        IReadOnlyList<string> arguments,
        string expected,
        TimeSpan timeout,
        string failureCode,
        CancellationToken cancellationToken)
    {
        var result = await RequireSuccessAsync(
            arguments,
            timeout,
            failureCode,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(result.StandardOutput.Trim(), expected, StringComparison.Ordinal))
        {
            throw new RunnerExecutionException(failureCode);
        }
    }

    private static string Resource(string automationId) =>
        $"{RunnerOptions.E2ePackage}:id/{automationId}";

    private static string Sha256File(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static bool HashMatches(string value, string expected) =>
        string.Equals(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value))),
            expected,
            StringComparison.Ordinal);

    private static bool ContainsEmulatorMarker(string value)
    {
        var normalized = value.ToLowerInvariant();
        return new[]
        {
            "emulator", "sdk_gphone", "sdk-gphone", "generic", "goldfish",
            "ranchu", "vbox", "qemu", "simulator"
        }.Any(normalized.Contains);
    }

    private static IReadOnlyList<string> ParsePackages(string output)
    {
        var lines = output.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > 512)
        {
            throw new RunnerExecutionException("package-inventory-limit");
        }

        var packages = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (!line.StartsWith("package:", StringComparison.Ordinal) ||
                line.Length is <= 8 or > 264)
            {
                throw new RunnerExecutionException("package-inventory-invalid");
            }

            var package = line[8..];
            if (package.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) &&
                    character is not '.' and not '_'))
            {
                throw new RunnerExecutionException("package-inventory-invalid");
            }

            packages.Add(package);
        }

        return packages;
    }
}

internal sealed class DeviceAttestation
{
    internal bool DedicatedManaged { get; set; }
    internal bool PersonalDataAbsent { get; set; }
    internal bool ProductionPackageAbsentBefore { get; set; }
    internal bool TestPackageClearedBefore { get; set; }
    internal bool TestPackageRemovedAfter { get; set; }

    internal bool AllPassed =>
        DedicatedManaged &&
        PersonalDataAbsent &&
        ProductionPackageAbsentBefore &&
        TestPackageClearedBefore &&
        TestPackageRemovedAfter;
}

internal sealed class TestCounters(int total)
{
    internal int Total { get; } = total;
    internal int Executed { get; private set; }
    internal int Passed { get; private set; }
    internal int Failed { get; private set; }
    internal int Skipped => Total - Executed;
    private bool current;

    internal void Begin()
    {
        if (current || Executed >= Total)
        {
            throw new RunnerExecutionException("test-counter-invalid");
        }
        current = true;
        Executed++;
    }

    internal void Pass()
    {
        if (!current)
        {
            throw new RunnerExecutionException("test-counter-invalid");
        }
        current = false;
        Passed++;
    }

    internal void FailCurrent()
    {
        if (current)
        {
            current = false;
            Failed++;
        }
    }
}

internal sealed record RunnerOutcome(
    bool Passed,
    string? FailureCode,
    TestCounters Counters,
    DeviceAttestation Device);
