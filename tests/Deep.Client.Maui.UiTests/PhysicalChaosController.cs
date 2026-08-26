using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Deep.Client.Maui.UiTests;

internal sealed class PhysicalChaosController
{
    internal const string DevOpsCommit = "3fa76e5b131cf41580bc6338f8b7e7a107a22be2";
    internal const string DependencyManifestSha256 =
        "559ad35ae9495afe9a705632bf048bf1368be085e444f46669f3a8ef653ae51f";
    internal const int TtlSeconds = 300;
    private const int CleanupAttempts = 3;
    private readonly string launcher;
    private readonly string lanHost;
    private readonly Action verifyAuthority;
    private readonly Func<IReadOnlyList<string>, TimeSpan,
        StrictCrossPlatformContracts.ProcessResult> invokeCommand;
    private bool began;
    private bool ended;

    private PhysicalChaosController(
        string launcher,
        string lanHost,
        Action verifyAuthority,
        Func<IReadOnlyList<string>, TimeSpan,
            StrictCrossPlatformContracts.ProcessResult>? invokeCommand = null)
    {
        this.launcher = launcher;
        this.lanHost = lanHost;
        this.verifyAuthority = verifyAuthority;
        this.invokeCommand = invokeCommand ?? InvokePowerShell;
    }

    internal static PhysicalChaosController LoadRequired()
    {
        var repositoryRoot = RequireDirectory("DEEP_E2E_REPOSITORY_ROOT");
        var runState = RequireFile("DEEP_MAU2_E2E_RUN_STATE");
        var runRoot = Path.GetDirectoryName(runState)
            ?? throw new InvalidOperationException("Physical run state has no parent directory.");
        var snapshotRoot = Path.Combine(runRoot, "chaos-authority");
        var expectedDevOpsRoot = Path.Combine(snapshotRoot, "deep-devops");
        var expectedManifest = Path.Combine(snapshotRoot,
            "physical-chaos-dependencies.v1.json");
        var devOpsRoot = RequireDirectory("DEEP_E2E_CHAOS_DEVOPS_ROOT");
        if (!string.Equals(devOpsRoot, expectedDevOpsRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Physical chaos must use its private run snapshot.");
        var manifest = RequireFile("DEEP_E2E_CHAOS_MANIFEST");
        if (!string.Equals(manifest, expectedManifest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Physical chaos manifest must come from its private run snapshot.");
        var authority = new DependencyAuthority(
            snapshotRoot, devOpsRoot, manifest, DependencyManifestSha256, DevOpsCommit);
        authority.Verify();
        var snapshotSha256 = RequireHashEnvironment("DEEP_E2E_CHAOS_SNAPSHOT_SHA256");
        if (!string.Equals(snapshotSha256, authority.ExecutionSnapshotSha256,
                StringComparison.Ordinal))
            throw new InvalidOperationException("Physical chaos snapshot digest is invalid.");
        RequireExactEnvironmentPath("DEEP_PHYSICAL_E2E_DOCKER_PATH",
            DependencyAuthority.DockerPath);
        RequireExactEnvironmentPath("DEEP_PHYSICAL_E2E_DOCKER_COMPOSE_PATH",
            DependencyAuthority.DockerComposePath);
        RequireExactEnvironmentPath("DEEP_PHYSICAL_E2E_HAPROXY_CONFIG_PATH",
            authority.HAProxyConfig);
        RequireExactEnvironmentHash("DEEP_PHYSICAL_E2E_DOCKER_SHA256",
            authority.DockerSha256);
        RequireExactEnvironmentHash("DEEP_PHYSICAL_E2E_DOCKER_COMPOSE_SHA256",
            authority.DockerComposeSha256);
        RequireExactEnvironmentPath("DEEP_PHYSICAL_E2E_DEVOPS_RUNTIME_ROOT",
            Path.GetFullPath(Path.Combine(repositoryRoot, "..", "deep-devops")));

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
        return new PhysicalChaosController(authority.Launcher, origin.Host, authority.Verify)
        {
            ExecutionSnapshotSha256 = snapshotSha256
        };
    }

    internal static PhysicalChaosController CreateForTests(
        Func<IReadOnlyList<string>, TimeSpan,
            StrictCrossPlatformContracts.ProcessResult> invokeCommand,
        Action? verifyAuthority = null) =>
        new("test-launcher", "127.0.0.1", verifyAuthority ?? (() => { }), invokeCommand);

    internal string ExecutionSnapshotSha256 { get; private init; } = new string('0', 64);

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
            "ChaosStatus did not reach the exact expected counters within the bounded wait; " +
            (last?.SanitizedLifecycle ?? "no status was observed"));
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
        var failures = new List<Exception>();
        for (var attempt = 1; attempt <= CleanupAttempts; attempt++)
        {
            try
            {
                var end = Invoke(["-Action", "ChaosEnd"], TimeSpan.FromSeconds(180));
                ChaosEnd.ParseExact(end.Output);
                var baseline = ChaosStatus.ParseExact(
                    Invoke(["-Action", "ChaosStatus"], TimeSpan.FromSeconds(45)).Output);
                baseline.AssertOffBaseline();
                ended = true;
                return;
            }
            catch (Exception exception)
            {
                failures.Add(new InvalidOperationException(
                    $"Physical chaos cleanup attempt {attempt} failed.", exception));
            }
        }
        throw new AggregateException(
            "Physical chaos cleanup did not restore the exact off baseline.", failures);
    }

    private StrictCrossPlatformContracts.ProcessResult Invoke(
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        verifyAuthority();
        var result = invokeCommand(arguments, timeout);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("Supported physical chaos command failed.");
        return result;
    }

    private StrictCrossPlatformContracts.ProcessResult InvokePowerShell(
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        var powershell = DependencyAuthority.PowerShellPath;
        var all = new List<string>
        {
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", launcher
        };
        all.AddRange(arguments);
        return StrictCrossPlatformContracts.RunBounded(powershell, all, timeout);
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

    private static string RequireHashEnvironment(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return value is not null && Regex.IsMatch(value, "^[a-f0-9]{64}$",
            RegexOptions.CultureInvariant)
            ? value
            : throw new InvalidOperationException($"{key} must be canonical lower-case SHA-256.");
    }

    private static void RequireExactEnvironmentPath(string key, string expected)
    {
        var actual = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(actual) || !Path.IsPathFullyQualified(actual)
            || !string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{key} does not match reviewed authority.");
    }

    private static void RequireExactEnvironmentHash(string key, string expected)
    {
        var actual = RequireHashEnvironment(key);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"{key} does not match reviewed authority.");
    }

    internal sealed class DependencyAuthority
    {
        internal const string PowerShellPath =
            "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe";
        internal const string DotNetPath = "C:\\Program Files\\dotnet\\dotnet.exe";
        internal const string DockerPath =
            "C:\\Program Files\\Docker\\Docker\\resources\\bin\\docker.exe";
        internal const string DockerComposePath =
            "C:\\Program Files\\Docker\\Docker\\resources\\bin\\docker-compose.exe";
        internal const string TaskKillPath = "C:\\Windows\\System32\\taskkill.exe";
        private const string TreeDomain = "deep.physical-chaos.dependency-tree.v1\0";
        private static readonly HashSet<string> ExactRootProperties = new(StringComparer.Ordinal)
        {
            "schema", "reviewedDevOpsCommit", "dependencyTreeSha256", "files",
            "closedDirectories", "systemExecutables"
        };
        private static readonly HashSet<string> ExactFileProperties = new(StringComparer.Ordinal)
        {
            "path", "sha256"
        };
        private static readonly HashSet<string> ExactSystemProperties = new(StringComparer.Ordinal)
        {
            "name", "path", "sha256"
        };
        private static readonly IReadOnlyDictionary<string, string> RequiredSystemExecutables =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["docker"] = DockerPath.Replace('\\', '/'),
                ["dockerCompose"] = DockerComposePath.Replace('\\', '/'),
                ["dotnet"] = DotNetPath.Replace('\\', '/'),
                ["powershell"] = PowerShellPath.Replace('\\', '/'),
                ["taskkill"] = TaskKillPath.Replace('\\', '/')
            };

        private readonly string repositoryRoot;
        private readonly string devOpsRoot;
        private readonly string manifestPath;
        private readonly string expectedManifestSha256;
        private readonly string expectedDevOpsCommit;

        internal DependencyAuthority(
            string repositoryRoot,
            string devOpsRoot,
            string manifestPath,
            string expectedManifestSha256,
            string expectedDevOpsCommit)
        {
            this.repositoryRoot = Path.GetFullPath(repositoryRoot);
            this.devOpsRoot = Path.GetFullPath(devOpsRoot);
            this.manifestPath = Path.GetFullPath(manifestPath);
            this.expectedManifestSha256 = RequireHash(expectedManifestSha256, "manifest pin");
            this.expectedDevOpsCommit = RequireCommit(expectedDevOpsCommit);
            Launcher = Path.Combine(this.devOpsRoot, "scripts", "survival-dev.ps1");
            HAProxyConfig = Path.Combine(this.devOpsRoot,
                "config", "survival-uat-tls", "haproxy.cfg");
        }

        internal string Launcher { get; }
        internal string HAProxyConfig { get; }
        internal string ExecutionSnapshotSha256 { get; private set; } = string.Empty;
        internal string DockerSha256 { get; private set; } = string.Empty;
        internal string DockerComposeSha256 { get; private set; } = string.Empty;

        internal void Verify()
        {
            RequireExactChild(repositoryRoot, manifestPath, "physical chaos dependency manifest");
            RequireRegularFile(manifestPath, "physical chaos dependency manifest");
            var manifestBytes = ReadBoundedExclusive(manifestPath, 64 * 1024,
                "physical chaos dependency manifest");
            try
            {
                if (!string.Equals(Sha256Bytes(manifestBytes), expectedManifestSha256,
                        StringComparison.Ordinal))
                    throw new InvalidOperationException("Physical chaos dependency manifest pin is invalid.");

                using var document = JsonDocument.Parse(manifestBytes,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow
                    });
                var root = document.RootElement;
                RequireExactProperties(root, ExactRootProperties, "dependency manifest");
                if (root.GetProperty("schema").GetString() != "deep.physical-chaos-dependencies.v1"
                    || root.GetProperty("reviewedDevOpsCommit").GetString() != expectedDevOpsCommit)
                    throw new InvalidOperationException("Physical chaos dependency manifest identity is invalid.");
                var expectedTreeHash = RequireHash(
                    RequireString(root, "dependencyTreeSha256"), "dependency tree pin");

                var lines = new List<string>();
                var reviewedFiles = ReadFiles(root.GetProperty("files"), lines);
                var closedDirectories = ReadClosedDirectories(
                    root.GetProperty("closedDirectories"), lines);
                ReadSystemExecutables(root.GetProperty("systemExecutables"), lines);
                lines.Sort(StringComparer.Ordinal);
                var treeMaterial = TreeDomain + string.Concat(lines.Select(static line => line + "\n"));
                var actualTreeHash = Sha256Bytes(Encoding.UTF8.GetBytes(treeMaterial));
                if (!string.Equals(actualTreeHash, expectedTreeHash, StringComparison.Ordinal))
                    throw new InvalidOperationException("Physical chaos dependency tree pin is invalid.");

                foreach (var reviewedFile in reviewedFiles)
                {
                    var fullPath = ResolveDevOpsPath(reviewedFile.Path, "reviewed dependency");
                    RequireRegularFile(fullPath, "reviewed dependency");
                    if (!string.Equals(Sha256File(fullPath), reviewedFile.Sha256, StringComparison.Ordinal))
                        throw new InvalidOperationException("A reviewed physical chaos dependency changed.");
                }
                foreach (var closedDirectory in closedDirectories)
                    VerifyClosedDirectory(closedDirectory, reviewedFiles);

                if (!reviewedFiles.Any(file => file.Path == "scripts/survival-dev.ps1")
                    || !reviewedFiles.Any(file =>
                        file.Path == "config/survival-uat-tls/haproxy.cfg")
                    || !string.Equals(Path.GetFullPath(Launcher),
                        ResolveDevOpsPath("scripts/survival-dev.ps1", "launcher"),
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(Path.GetFullPath(HAProxyConfig),
                        ResolveDevOpsPath("config/survival-uat-tls/haproxy.cfg", "HAProxy configuration"),
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "Reviewed physical chaos launcher or HAProxy configuration is missing.");
                using var snapshot = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                snapshot.AppendData("deep.physical-chaos.execution-snapshot.v1\0"u8);
                snapshot.AppendData(Encoding.ASCII.GetBytes(expectedManifestSha256));
                snapshot.AppendData("|"u8);
                snapshot.AppendData(Encoding.ASCII.GetBytes(expectedTreeHash));
                ExecutionSnapshotSha256 = Convert.ToHexStringLower(snapshot.GetHashAndReset());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(manifestBytes);
            }
        }

        private List<ReviewedFile> ReadFiles(JsonElement element, List<string> lines)
        {
            if (element.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Dependency manifest files must be an array.");
            var result = new List<ReviewedFile>();
            foreach (var item in element.EnumerateArray())
            {
                RequireExactProperties(item, ExactFileProperties, "dependency file");
                var path = RequireRelativePath(RequireString(item, "path"), "dependency file");
                var hash = RequireHash(RequireString(item, "sha256"), "dependency file hash");
                if (Path.GetExtension(path).Equals(".env", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Secrets files cannot be reviewed dependencies.");
                result.Add(new ReviewedFile(path, hash));
                lines.Add($"file:{path}={hash}");
            }
            RequireUniqueSorted(result.Select(static file => file.Path), "dependency files");
            return result;
        }

        private static List<string> ReadClosedDirectories(JsonElement element, List<string> lines)
        {
            if (element.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Closed directories must be an array.");
            var result = element.EnumerateArray().Select(item =>
            {
                if (item.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException("Closed directory path must be a string.");
                return RequireRelativePath(item.GetString()!, "closed directory");
            }).ToList();
            RequireUniqueSorted(result, "closed directories");
            lines.AddRange(result.Select(static path => $"directory:{path}"));
            return result;
        }

        private void ReadSystemExecutables(JsonElement element, List<string> lines)
        {
            if (element.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("System executables must be an array.");
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            var orderedNames = new List<string>();
            foreach (var item in element.EnumerateArray())
            {
                RequireExactProperties(item, ExactSystemProperties, "system executable");
                var name = RequireString(item, "name");
                var path = RequireString(item, "path");
                var hash = RequireHash(RequireString(item, "sha256"), "system executable hash");
                if (!RequiredSystemExecutables.TryGetValue(name, out var requiredPath)
                    || !string.Equals(path, requiredPath, StringComparison.Ordinal)
                    || !found.TryAdd(name, path))
                    throw new InvalidOperationException("System executable authority is invalid.");
                orderedNames.Add(name);
                var nativePath = path.Replace('/', Path.DirectorySeparatorChar);
                RequireRegularFile(nativePath, "system executable");
                if (!string.Equals(Sha256ImmutableExecutable(nativePath), hash,
                        StringComparison.Ordinal))
                    throw new InvalidOperationException("A pinned system executable changed.");
                if (name == "docker") DockerSha256 = hash;
                if (name == "dockerCompose") DockerComposeSha256 = hash;
                lines.Add($"system:{name}|{path}={hash}");
            }
            RequireUniqueSorted(orderedNames, "system executables");
            if (!found.Keys.ToHashSet(StringComparer.Ordinal)
                    .SetEquals(RequiredSystemExecutables.Keys))
                throw new InvalidOperationException("Required system executable authority is incomplete.");
        }

        private void VerifyClosedDirectory(
            string relativeDirectory,
            IReadOnlyCollection<ReviewedFile> reviewedFiles)
        {
            var fullDirectory = ResolveDevOpsPath(relativeDirectory, "closed directory");
            RequireDirectoryWithoutReparse(fullDirectory, "closed directory");
            var actual = new List<string>();
            var actualDirectories = new List<string>();
            var pending = new Stack<string>();
            pending.Push(fullDirectory);
            while (pending.Count != 0)
            {
                var directory = pending.Pop();
                RequireDirectoryWithoutReparse(directory, "closed-directory dependency");
                actualDirectories.Add(Path.GetRelativePath(devOpsRoot, directory).Replace('\\', '/'));
                foreach (var childDirectory in Directory.EnumerateDirectories(
                             directory, "*", SearchOption.TopDirectoryOnly))
                {
                    RequireDirectoryWithoutReparse(childDirectory, "closed-directory dependency");
                    pending.Push(childDirectory);
                }
                foreach (var path in Directory.EnumerateFiles(
                             directory, "*", SearchOption.TopDirectoryOnly))
                {
                    RequireRegularFile(path, "closed-directory dependency");
                    actual.Add(Path.GetRelativePath(devOpsRoot, path).Replace('\\', '/'));
                }
            }
            actual.Sort(StringComparer.Ordinal);
            var prefix = relativeDirectory + "/";
            var expected = reviewedFiles.Select(static file => file.Path)
                .Where(path => path.StartsWith(prefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).ToArray();
            if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
                throw new InvalidOperationException("Closed physical chaos dependency directory changed.");
            var expectedDirectories = expected.Select(path => path[..path.LastIndexOf('/')])
                .SelectMany(path => ParentDirectories(relativeDirectory, path))
                .Append(relativeDirectory).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray();
            actualDirectories.Sort(StringComparer.Ordinal);
            if (!actualDirectories.SequenceEqual(expectedDirectories, StringComparer.Ordinal))
                throw new InvalidOperationException("Closed physical chaos dependency directory tree changed.");
        }

        private static IEnumerable<string> ParentDirectories(string root, string leaf)
        {
            var current = leaf;
            while (current.Length >= root.Length
                   && (current == root || current.StartsWith(root + "/", StringComparison.Ordinal)))
            {
                yield return current;
                if (current == root) yield break;
                current = current[..current.LastIndexOf('/')];
            }
        }

        private string ResolveDevOpsPath(string relativePath, string label)
        {
            var fullPath = Path.GetFullPath(Path.Combine(
                devOpsRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            RequireExactChild(devOpsRoot, fullPath, label);
            return fullPath;
        }

        private static void RequireExactChild(string root, string path, string label)
        {
            var relative = Path.GetRelativePath(root, path);
            if (relative == "." || relative == ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Path.IsPathFullyQualified(relative))
                throw new InvalidOperationException($"{label} must be strictly inside its authority root.");
        }

        private static void RequireRegularFile(string path, string label)
        {
            if (!File.Exists(path))
                throw new InvalidOperationException($"{label} must be an existing regular file.");
            var info = new FileInfo(path);
            if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidOperationException($"{label} must not be a directory or reparse point.");
            RequireParentChainWithoutReparse(info.Directory);
        }

        private static void RequireDirectoryWithoutReparse(string path, string label)
        {
            if (!Directory.Exists(path))
                throw new InvalidOperationException($"{label} must be an existing directory.");
            var info = new DirectoryInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"{label} must not be a reparse point.");
            RequireParentChainWithoutReparse(info);
        }

        private static void RequireParentChainWithoutReparse(DirectoryInfo? directory)
        {
            while (directory is not null)
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Dependency authority path crosses a reparse point.");
                directory = directory.Parent;
            }
        }

        private static void RequireExactProperties(
            JsonElement element,
            HashSet<string> expected,
            string label)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"{label} must be an object.");
            var names = element.EnumerateObject().Select(static property => property.Name).ToArray();
            if (names.Length != expected.Count || names.Distinct(StringComparer.Ordinal).Count() != names.Length
                || !names.ToHashSet(StringComparer.Ordinal).SetEquals(expected))
                throw new InvalidOperationException($"{label} has an invalid schema.");
        }

        private static string RequireString(JsonElement element, string property)
        {
            var value = element.GetProperty(property);
            return value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!
                : throw new InvalidOperationException($"{property} must be a nonempty string.");
        }

        private static string RequireRelativePath(string path, string label)
        {
            if (!Regex.IsMatch(path, "^[a-z0-9._/-]+$", RegexOptions.CultureInvariant)
                || path.Contains('\\') || path.StartsWith("/", StringComparison.Ordinal)
                || path.EndsWith("/", StringComparison.Ordinal) || path.Contains("//", StringComparison.Ordinal)
                || path.Split('/').Any(segment => segment is "" or "." or "..")
                || path.Any(char.IsControl) || Path.IsPathFullyQualified(path))
                throw new InvalidOperationException($"{label} path is not canonical and relative.");
            return path;
        }

        private static string RequireHash(string value, string label) =>
            Regex.IsMatch(value, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant)
                ? value
                : throw new InvalidOperationException($"{label} must be canonical lower-case SHA-256.");

        private static string RequireCommit(string value) =>
            Regex.IsMatch(value, "^[a-f0-9]{40}$", RegexOptions.CultureInvariant)
                ? value
                : throw new InvalidOperationException(
                    "reviewed DevOps commit must be a canonical full Git object ID.");

        private static void RequireUniqueSorted(IEnumerable<string> values, string label)
        {
            var array = values.ToArray();
            if (array.Distinct(StringComparer.Ordinal).Count() != array.Length
                || !array.SequenceEqual(array.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                throw new InvalidOperationException($"{label} must be unique and ordinal-sorted.");
        }

        private static byte[] ReadBoundedExclusive(string path, int maximumBytes, string label)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, FileOptions.SequentialScan);
            if (stream.Length <= 0 || stream.Length > maximumBytes)
                throw new InvalidOperationException($"{label} has an invalid size.");
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
                throw new InvalidOperationException($"{label} changed while it was read.");
            return bytes;
        }

        private static string Sha256File(string path)
        {
            // The parent runner keeps read-only leases on every execution-snapshot file.
            // Sharing reads preserves that anti-mutation lease while allowing this child
            // verifier to hash the exact same bytes.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        private static string Sha256ImmutableExecutable(string path)
        {
            // A running Windows image keeps a loader handle open. Sharing reads and
            // delete is required to inspect that pinned image; omitting Write still
            // prevents mutation during the complete hash operation.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        private static string Sha256Bytes(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        private sealed record ReviewedFile(string Path, string Sha256);
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

        internal string SanitizedLifecycle =>
            $"running={Running};armed={Armed};consumed={Consumed};" +
            $"requests={RequestCount};attempts={OperationAttemptCount};" +
            $"dispatches={OperationUpstreamDispatchCount};" +
            $"successes={OperationUpstreamSuccessCount};injected={InjectedFaultCount};" +
            $"postDrop={PostDurableResponseDropCount};" +
            $"preOutage={PreDispatchOutageCount};ackDrop={PostDurableAckResponseDropCount}";

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
            var lifecycleIsReachable = Armed != Consumed
                && (Consumed ? InjectedFaultCount == 1 : InjectedFaultCount == 0);
            if (!Running || !lifecycleIsReachable || Fault != expectedFault
                || Operation != expectedOperation
                || PostDurableResponseDropCount > postDrop
                || PreDispatchOutageCount > preOutage
                || PostDurableAckResponseDropCount > ackDrop
                || OperationAttemptCount > attempts
                || OperationUpstreamDispatchCount > dispatches
                || OperationUpstreamSuccessCount > successes
                || FaultWindowStartedUnixMilliseconds <= 0
                || FaultWindowDeadlineUnixMilliseconds <= FaultWindowStartedUnixMilliseconds)
                throw new InvalidOperationException(
                    "ChaosStatus diverged from the exact expected fault lifecycle; " +
                    SanitizedLifecycle);
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
            var names = root.EnumerateObject().Select(static property => property.Name).ToArray();
            if (names.Length != 5 || names.Distinct(StringComparer.Ordinal).Count() != names.Length
                || !names.ToHashSet(StringComparer.Ordinal)
                    .SetEquals(["schema", "status", "running", "armed", "protectedTokenDeleted"])
                || root.GetProperty("schema").GetString() != "deep-survival-resend-chaos-end.v2"
                || root.GetProperty("status").GetString() != "ok"
                || root.GetProperty("running").GetBoolean()
                || root.GetProperty("armed").GetBoolean()
                || !root.GetProperty("protectedTokenDeleted").GetBoolean())
                throw new InvalidOperationException("ChaosEnd result is invalid.");
        }
    }
}
