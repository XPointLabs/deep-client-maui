using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Deep.Client.Maui.UiTests;

internal sealed class PhysicalChaosController
{
    internal const string DevOpsCommit = "1bc829c7b43efa20968fa846f5c0e6239ca8419d";
    internal const int TtlSeconds = 300;
    private readonly string launcher;
    private readonly string lanHost;
    private bool began;
    private bool ended;

    private PhysicalChaosController(string launcher, string lanHost)
    {
        this.launcher = launcher;
        this.lanHost = lanHost;
    }

    internal static PhysicalChaosController LoadRequired()
    {
        var repositoryRoot = RequireDirectory("DEEP_E2E_REPOSITORY_ROOT");
        var devOpsRoot = RequireDirectory("DEEP_E2E_CHAOS_DEVOPS_ROOT");
        var expectedDevOpsRoot = Path.GetFullPath(Path.Combine(repositoryRoot, "..", "deep-devops"));
        if (!string.Equals(devOpsRoot, expectedDevOpsRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Physical chaos must use the sibling deep-devops checkout.");
        StrictCrossPlatformContracts.RequireCurrentCommit(devOpsRoot, DevOpsCommit);
        var clean = StrictCrossPlatformContracts.RunBounded(
            "git", ["-c", $"safe.directory={devOpsRoot.Replace('\\', '/')}", "-C", devOpsRoot,
                "status", "--porcelain=v1", "--untracked-files=all"], TimeSpan.FromSeconds(15));
        if (clean.ExitCode != 0 || !string.IsNullOrWhiteSpace(clean.Output))
            throw new InvalidOperationException("Physical chaos requires the exact clean deep-devops commit.");

        var launcher = RequireFile("DEEP_E2E_CHAOS_SCRIPT");
        var expectedLauncher = Path.Combine(devOpsRoot, "scripts", "survival-dev.ps1");
        if (!string.Equals(launcher, Path.GetFullPath(expectedLauncher), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Physical chaos launcher is not the supported DevOps entrypoint.");
        var expectedSha = Environment.GetEnvironmentVariable("DEEP_E2E_CHAOS_SCRIPT_SHA256") ?? string.Empty;
        StrictCrossPlatformContracts.RequirePinnedFile(launcher, expectedSha, "DevOps chaos launcher");

        var originRaw = Environment.GetEnvironmentVariable("DEEP_E2E_CHAOS_HTTPS_ORIGIN");
        if (!Uri.TryCreate(originRaw, UriKind.Absolute, out var origin)
            || !string.Equals(origin.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(origin.UserInfo)
            || origin.Port != 41801
            || origin.AbsolutePath != "/"
            || !string.IsNullOrEmpty(origin.Query)
            || !string.IsNullOrEmpty(origin.Fragment)
            || !IPAddress.TryParse(origin.Host, out var address)
            || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || address.Equals(IPAddress.Any))
        {
            throw new InvalidOperationException(
                "Physical chaos requires the exact CA-trusted HTTPS IPv4 :41801 origin.");
        }
        return new PhysicalChaosController(launcher, origin.Host);
    }

    internal ChaosStatus Begin(string fault, string expectedOperation)
    {
        if (began || ended) throw new InvalidOperationException("Physical chaos is single-use.");
        var result = Invoke(["-Action", "ChaosBegin", "-LanHost", lanHost,
            "-ChaosTtlSeconds", TtlSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-ChaosFault", fault], TimeSpan.FromSeconds(180));
        var status = ChaosStatus.ParseExact(result.Output);
        status.AssertBegin(fault, expectedOperation, TtlSeconds);
        began = true;
        return status;
    }

    internal ChaosStatus Status()
    {
        if (!began || ended) throw new InvalidOperationException("Physical chaos is not active.");
        return ChaosStatus.ParseExact(
            Invoke(["-Action", "ChaosStatus"], TimeSpan.FromSeconds(15)).Output);
    }

    internal ChaosStatus WaitForConsumed(
        string expectedFault,
        string expectedOperation,
        long attempts,
        long dispatches,
        long successes,
        long postDrop,
        long preOutage,
        long ackDrop,
        TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var deadline = DateTime.UtcNow + timeout;
        ChaosStatus? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = Status();
            if (last.HasExactConsumedCounters(
                    expectedFault, expectedOperation, attempts, dispatches,
                    successes, postDrop, preOutage, ackDrop))
                return last;
            last.AssertCanStillReachConsumedCounters(
                expectedFault, expectedOperation, attempts, dispatches,
                successes, postDrop, preOutage, ackDrop);
            Thread.Sleep(250);
        }
        throw new InvalidOperationException(
            "ChaosStatus did not reach the exact expected counters within the bounded wait.");
    }

    internal string WriteVerifiedStatusEvidence(
        string artifactDirectory,
        Mau2PhysicalPhase phase,
        ChaosStatus status)
    {
        var root = Path.GetFullPath(artifactDirectory);
        var path = Path.Combine(root, $"chaos-{phase.ToString().ToLowerInvariant()}-status.v2.json");
        var digestPath = path + ".sha256";
        if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase)
            || File.Exists(path) || File.Exists(digestPath))
            throw new InvalidOperationException("Chaos status evidence path is not fresh and runner-owned.");
        File.WriteAllText(path, status.RawJson + "\n", new UTF8Encoding(false));
        var digest = StrictCrossPlatformContracts.Sha256File(path);
        File.WriteAllText(digestPath, digest + "\n", new UTF8Encoding(false));
        var rereadDigest = File.ReadAllText(digestPath, Encoding.UTF8).Trim();
        if (!Regex.IsMatch(rereadDigest, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant)
            || !string.Equals(rereadDigest, StrictCrossPlatformContracts.Sha256File(path),
                StringComparison.Ordinal)
            || !string.Equals(status.RawJson,
                File.ReadAllText(path, Encoding.UTF8).TrimEnd('\r', '\n'),
                StringComparison.Ordinal))
            throw new InvalidOperationException("Chaos status evidence SHA-256 verification failed.");
        return digest;
    }

    internal void EndAndAssertBaseline()
    {
        if (ended) return;
        try
        {
            var end = Invoke(["-Action", "ChaosEnd"], TimeSpan.FromSeconds(180));
            ChaosEnd.ParseExact(end.Output);
            var baseline = ChaosStatus.ParseExact(
                Invoke(["-Action", "ChaosStatus"], TimeSpan.FromSeconds(45)).Output);
            baseline.AssertOffBaseline();
        }
        finally
        {
            ended = true;
        }
    }

    private StrictCrossPlatformContracts.ProcessResult Invoke(
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var all = new List<string>
        {
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", launcher
        };
        all.AddRange(arguments);
        var result = StrictCrossPlatformContracts.RunBounded(powershell, all, timeout);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("Supported physical chaos command failed.");
        return result;
    }

    private static string RequireDirectory(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)
            || !Directory.Exists(value))
            throw new InvalidOperationException($"{key} must be an existing absolute directory.");
        return Path.GetFullPath(value);
    }

    private static string RequireFile(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)
            || !File.Exists(value))
            throw new InvalidOperationException($"{key} must be an existing absolute file.");
        return Path.GetFullPath(value);
    }

    internal sealed record ChaosStatus(
        string RawJson,
        bool Running,
        string? Operation,
        string? Fault,
        bool Armed,
        bool Consumed,
        long RequestCount,
        long OperationAttemptCount,
        long OperationUpstreamDispatchCount,
        long OperationUpstreamSuccessCount,
        long InjectedFaultCount,
        long PostDurableResponseDropCount,
        long PreDispatchOutageCount,
        long PostDurableAckResponseDropCount,
        long FaultWindowStartedUnixMilliseconds,
        long FaultWindowDeadlineUnixMilliseconds,
        long ExpiresInSeconds)
    {
        private static readonly HashSet<string> ExactProperties = new(StringComparer.Ordinal)
        {
            "schema", "mode", "running", "operation", "fault", "armed", "consumed",
            "requestCount", "operationAttemptCount", "operationUpstreamDispatchCount",
            "operationUpstreamSuccessCount", "injectedFaultCount",
            "postDurableResponseDropCount", "preDispatchOutageCount",
            "postDurableAckResponseDropCount", "faultWindowStartedUnixMilliseconds",
            "faultWindowDeadlineUnixMilliseconds", "expiresInSeconds",
            "identifiersIncluded", "payloadInspected"
        };

        internal static ChaosStatus ParseExact(string output)
        {
            var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.StartsWith(
                    "{\"schema\":\"deep-survival-resend-chaos-status.v2\"",
                    StringComparison.Ordinal)).ToArray();
            if (lines.Length != 1)
                throw new InvalidOperationException("Chaos command did not emit one exact v2 status.");
            using var document = JsonDocument.Parse(lines[0]);
            var root = document.RootElement;
            var properties = root.EnumerateObject().Select(static property => property.Name).ToArray();
            if (properties.Length != ExactProperties.Count
                || properties.Distinct(StringComparer.Ordinal).Count() != properties.Length
                || !properties.ToHashSet(StringComparer.Ordinal).SetEquals(ExactProperties)
                || root.GetProperty("schema").GetString() != "deep-survival-resend-chaos-status.v2"
                || root.GetProperty("mode").GetString() != "development-only"
                || root.GetProperty("identifiersIncluded").GetBoolean()
                || root.GetProperty("payloadInspected").GetBoolean())
                throw new InvalidOperationException("Chaos status schema is invalid or unsafe.");
            return new ChaosStatus(
                lines[0],
                root.GetProperty("running").GetBoolean(),
                NullableString(root, "operation"),
                NullableString(root, "fault"),
                root.GetProperty("armed").GetBoolean(),
                root.GetProperty("consumed").GetBoolean(),
                Nonnegative(root, "requestCount"),
                Nonnegative(root, "operationAttemptCount"),
                Nonnegative(root, "operationUpstreamDispatchCount"),
                Nonnegative(root, "operationUpstreamSuccessCount"),
                Nonnegative(root, "injectedFaultCount"),
                Nonnegative(root, "postDurableResponseDropCount"),
                Nonnegative(root, "preDispatchOutageCount"),
                Nonnegative(root, "postDurableAckResponseDropCount"),
                Nonnegative(root, "faultWindowStartedUnixMilliseconds"),
                Nonnegative(root, "faultWindowDeadlineUnixMilliseconds"),
                Nonnegative(root, "expiresInSeconds"));
        }

        internal void AssertBegin(string expectedFault, string expectedOperation, int ttlSeconds)
        {
            if (!Running || !Armed || Consumed || Fault != expectedFault
                || Operation != expectedOperation || RequestCount != 0
                || OperationAttemptCount != 0 || OperationUpstreamDispatchCount != 0
                || OperationUpstreamSuccessCount != 0 || InjectedFaultCount != 0
                || PostDurableResponseDropCount != 0 || PreDispatchOutageCount != 0
                || PostDurableAckResponseDropCount != 0
                || FaultWindowStartedUnixMilliseconds <= 0
                || FaultWindowDeadlineUnixMilliseconds - FaultWindowStartedUnixMilliseconds
                    != ttlSeconds * 1000L
                || ExpiresInSeconds < 1 || ExpiresInSeconds > ttlSeconds)
                throw new InvalidOperationException("ChaosBegin did not arm one exact clean fault window.");
        }

        internal void AssertConsumed(
            string expectedFault,
            string expectedOperation,
            long attempts,
            long dispatches,
            long successes,
            long postDrop,
            long preOutage,
            long ackDrop)
        {
            if (!HasExactConsumedCounters(
                    expectedFault, expectedOperation, attempts, dispatches,
                    successes, postDrop, preOutage, ackDrop))
                throw new InvalidOperationException("ChaosStatus counters do not prove the exact fault lifecycle.");
        }

        internal bool HasExactConsumedCounters(
            string expectedFault,
            string expectedOperation,
            long attempts,
            long dispatches,
            long successes,
            long postDrop,
            long preOutage,
            long ackDrop) =>
            Running && !Armed && Consumed && Fault == expectedFault
            && Operation == expectedOperation && OperationAttemptCount == attempts
            && OperationUpstreamDispatchCount == dispatches
            && OperationUpstreamSuccessCount == successes
            && InjectedFaultCount == 1
            && PostDurableResponseDropCount == postDrop
            && PreDispatchOutageCount == preOutage
            && PostDurableAckResponseDropCount == ackDrop
            && FaultWindowStartedUnixMilliseconds > 0
            && FaultWindowDeadlineUnixMilliseconds > FaultWindowStartedUnixMilliseconds;

        internal void AssertCanStillReachConsumedCounters(
            string expectedFault,
            string expectedOperation,
            long attempts,
            long dispatches,
            long successes,
            long postDrop,
            long preOutage,
            long ackDrop)
        {
            if (!Running || Armed || !Consumed || Fault != expectedFault
                || Operation != expectedOperation || InjectedFaultCount != 1
                || PostDurableResponseDropCount != postDrop
                || PreDispatchOutageCount != preOutage
                || PostDurableAckResponseDropCount != ackDrop
                || OperationAttemptCount > attempts
                || OperationUpstreamDispatchCount > dispatches
                || OperationUpstreamSuccessCount > successes
                || FaultWindowStartedUnixMilliseconds <= 0
                || FaultWindowDeadlineUnixMilliseconds <= FaultWindowStartedUnixMilliseconds)
                throw new InvalidOperationException(
                    "ChaosStatus diverged from the exact expected fault lifecycle.");
        }

        internal void AssertOffBaseline()
        {
            if (Running || Armed || Consumed || Operation is not null || Fault is not null
                || RequestCount != 0 || OperationAttemptCount != 0
                || OperationUpstreamDispatchCount != 0 || OperationUpstreamSuccessCount != 0
                || InjectedFaultCount != 0 || PostDurableResponseDropCount != 0
                || PreDispatchOutageCount != 0 || PostDurableAckResponseDropCount != 0
                || FaultWindowStartedUnixMilliseconds != 0
                || FaultWindowDeadlineUnixMilliseconds != 0 || ExpiresInSeconds != 0)
                throw new InvalidOperationException("ChaosEnd did not restore the exact off baseline.");
        }

        private static string? NullableString(JsonElement root, string name)
        {
            var value = root.GetProperty(name);
            return value.ValueKind == JsonValueKind.Null ? null
                : value.ValueKind == JsonValueKind.String ? value.GetString()
                : throw new InvalidOperationException($"Chaos status {name} is not a nullable string.");
        }

        private static long Nonnegative(JsonElement root, string name)
        {
            var value = root.GetProperty(name);
            if (!value.TryGetInt64(out var result) || result < 0)
                throw new InvalidOperationException($"Chaos status {name} is not a nonnegative integer.");
            return result;
        }
    }

    private sealed record ChaosEnd
    {
        internal static void ParseExact(string output)
        {
            var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.StartsWith(
                    "{\"schema\":\"deep-survival-resend-chaos-end.v2\"",
                    StringComparison.Ordinal)).ToArray();
            if (lines.Length != 1) throw new InvalidOperationException("ChaosEnd did not emit one exact result.");
            using var document = JsonDocument.Parse(lines[0]);
            var root = document.RootElement;
            var names = root.EnumerateObject().Select(static property => property.Name).ToHashSet(StringComparer.Ordinal);
            if (!names.SetEquals(["schema", "status", "running", "armed", "protectedTokenDeleted"])
                || root.GetProperty("schema").GetString() != "deep-survival-resend-chaos-end.v2"
                || root.GetProperty("status").GetString() != "ok"
                || root.GetProperty("running").GetBoolean()
                || root.GetProperty("armed").GetBoolean()
                || !root.GetProperty("protectedTokenDeleted").GetBoolean())
                throw new InvalidOperationException("ChaosEnd result is invalid.");
        }
    }
}
