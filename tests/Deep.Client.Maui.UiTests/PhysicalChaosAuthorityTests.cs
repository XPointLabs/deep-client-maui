using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Deep.Client.Maui.UiTests;

public sealed class PhysicalChaosAuthorityTests
{
    [Fact]
    public void DependencyAuthority_FailsClosedAfterReviewedFileMutation()
    {
        using var fixture = AuthorityFixture.Create();
        fixture.Authority.Verify();

        File.AppendAllText(fixture.Launcher, "# mutation", Encoding.UTF8);

        Assert.Throws<InvalidOperationException>(fixture.Authority.Verify);
    }

    [Fact]
    public void DependencyAuthority_RejectsAnUnreviewedFileInClosedDirectory()
    {
        using var fixture = AuthorityFixture.Create();
        fixture.Authority.Verify();

        File.WriteAllText(Path.Combine(fixture.ClosedDirectory, "unreviewed.mjs"), "export {};",
            new UTF8Encoding(false));

        Assert.Throws<InvalidOperationException>(fixture.Authority.Verify);
    }

    [Fact]
    public void DependencyAuthority_FailsClosedAfterPinnedManifestMutation()
    {
        using var fixture = AuthorityFixture.Create();
        fixture.Authority.Verify();

        File.AppendAllText(fixture.Manifest, " ", Encoding.UTF8);

        Assert.Throws<InvalidOperationException>(fixture.Authority.Verify);
    }

    [Fact]
    public void DependencyAuthority_VerifiesManifestAndReviewedFilesUnderParentReadLease()
    {
        using var fixture = AuthorityFixture.Create();
        using var manifestLease = File.Open(
            fixture.Manifest, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reviewedFileLease = File.Open(
            fixture.Launcher, FileMode.Open, FileAccess.Read, FileShare.Read);

        fixture.Authority.Verify();
    }

    [Fact]
    public void EndAndAssertBaseline_RetriesAndDoesNotMarkEndedAfterFailure()
    {
        var calls = 0;
        var authorityChecks = 0;
        var fail = true;
        var controller = PhysicalChaosController.CreateForTests((arguments, _) =>
        {
            calls++;
            if (fail)
                return new StrictCrossPlatformContracts.ProcessResult(1, string.Empty, string.Empty);
            return arguments.Contains("ChaosEnd", StringComparer.Ordinal)
                ? new StrictCrossPlatformContracts.ProcessResult(0, EndJson, string.Empty)
                : new StrictCrossPlatformContracts.ProcessResult(0, OffStatusJson, string.Empty);
        }, () => authorityChecks++);

        var aggregate = Assert.Throws<AggregateException>(controller.EndAndAssertBaseline);
        Assert.Equal(3, aggregate.InnerExceptions.Count);
        Assert.Equal(3, calls);
        Assert.Equal(3, authorityChecks);

        fail = false;
        controller.EndAndAssertBaseline();
        Assert.Equal(5, calls);
        Assert.Equal(5, authorityChecks);

        controller.EndAndAssertBaseline();
        Assert.Equal(5, calls);
    }

    private const string EndJson =
        "{\"schema\":\"deep-survival-resend-chaos-end.v2\",\"status\":\"ok\",\"running\":false,\"armed\":false,\"protectedTokenDeleted\":true}";

    private const string OffStatusJson =
        "{\"schema\":\"deep-survival-resend-chaos-status.v2\",\"mode\":\"development-only\",\"running\":false,\"operation\":null,\"fault\":null,\"armed\":false,\"consumed\":false,\"requestCount\":0,\"operationAttemptCount\":0,\"operationUpstreamDispatchCount\":0,\"operationUpstreamSuccessCount\":0,\"injectedFaultCount\":0,\"postDurableResponseDropCount\":0,\"preDispatchOutageCount\":0,\"postDurableAckResponseDropCount\":0,\"faultWindowStartedUnixMilliseconds\":0,\"faultWindowDeadlineUnixMilliseconds\":0,\"expiresInSeconds\":0,\"identifiersIncluded\":false,\"payloadInspected\":false}";

    private sealed class AuthorityFixture : IDisposable
    {
        private AuthorityFixture(
            string root,
            string launcher,
            string manifest,
            string closedDirectory,
            PhysicalChaosController.DependencyAuthority authority)
        {
            Root = root;
            Launcher = launcher;
            Manifest = manifest;
            ClosedDirectory = closedDirectory;
            Authority = authority;
        }

        internal string Root { get; }
        internal string Launcher { get; }
        internal string Manifest { get; }
        internal string ClosedDirectory { get; }
        internal PhysicalChaosController.DependencyAuthority Authority { get; }

        internal static AuthorityFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "deep-chaos-authority-" + Guid.NewGuid().ToString("N"));
            var repository = Path.Combine(root, "deep-client-maui");
            var devOps = Path.Combine(root, "deep-devops");
            var eng = Path.Combine(repository, "eng");
            var scripts = Path.Combine(devOps, "scripts");
            var haproxyDirectory = Path.Combine(devOps, "config", "survival-uat-tls");
            var closedDirectory = Path.Combine(devOps, "tools", "survival-resend-chaos");
            Directory.CreateDirectory(eng);
            Directory.CreateDirectory(scripts);
            Directory.CreateDirectory(haproxyDirectory);
            Directory.CreateDirectory(closedDirectory);

            var launcher = Path.Combine(scripts, "survival-dev.ps1");
            var haproxy = Path.Combine(haproxyDirectory, "haproxy.cfg");
            var controlClient = Path.Combine(closedDirectory, "control-client.mjs");
            File.WriteAllText(launcher, "param()", new UTF8Encoding(false));
            File.WriteAllText(haproxy, "global\n", new UTF8Encoding(false));
            File.WriteAllText(controlClient, "export {};", new UTF8Encoding(false));

            var launcherHash = Sha256File(launcher);
            var haproxyHash = Sha256File(haproxy);
            var controlHash = Sha256File(controlClient);
            var powershellHash = Sha256File(
                PhysicalChaosController.DependencyAuthority.PowerShellPath);
            var dotnetHash = Sha256File(
                PhysicalChaosController.DependencyAuthority.DotNetPath);
            var dockerHash = Sha256File(
                PhysicalChaosController.DependencyAuthority.DockerPath);
            var dockerComposeHash = Sha256File(
                PhysicalChaosController.DependencyAuthority.DockerComposePath);
            var taskKillHash = Sha256File(
                PhysicalChaosController.DependencyAuthority.TaskKillPath);
            var lines = new[]
            {
                $"file:config/survival-uat-tls/haproxy.cfg={haproxyHash}",
                $"file:scripts/survival-dev.ps1={launcherHash}",
                $"file:tools/survival-resend-chaos/control-client.mjs={controlHash}",
                "directory:tools/survival-resend-chaos",
                $"system:docker|C:/Program Files/Docker/Docker/resources/bin/docker.exe={dockerHash}",
                $"system:dockerCompose|C:/Program Files/Docker/Docker/resources/bin/docker-compose.exe={dockerComposeHash}",
                $"system:dotnet|C:/Program Files/dotnet/dotnet.exe={dotnetHash}",
                $"system:powershell|C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe={powershellHash}",
                $"system:taskkill|C:/Windows/System32/taskkill.exe={taskKillHash}"
            };
            var treeMaterial = "deep.physical-chaos.dependency-tree.v1\0"
                + string.Concat(lines.Order(StringComparer.Ordinal).Select(static line => line + "\n"));
            var treeHash = Sha256Bytes(Encoding.UTF8.GetBytes(treeMaterial));
            var manifest = new
            {
                schema = "deep.physical-chaos-dependencies.v1",
                reviewedDevOpsCommit = PhysicalChaosController.DevOpsCommit,
                dependencyTreeSha256 = treeHash,
                files = new[]
                {
                    new { path = "config/survival-uat-tls/haproxy.cfg", sha256 = haproxyHash },
                    new { path = "scripts/survival-dev.ps1", sha256 = launcherHash },
                    new { path = "tools/survival-resend-chaos/control-client.mjs", sha256 = controlHash }
                },
                closedDirectories = new[] { "tools/survival-resend-chaos" },
                systemExecutables = new[]
                {
                    new
                    {
                        name = "docker",
                        path = "C:/Program Files/Docker/Docker/resources/bin/docker.exe",
                        sha256 = dockerHash
                    },
                    new
                    {
                        name = "dockerCompose",
                        path = "C:/Program Files/Docker/Docker/resources/bin/docker-compose.exe",
                        sha256 = dockerComposeHash
                    },
                    new
                    {
                        name = "dotnet", path = "C:/Program Files/dotnet/dotnet.exe",
                        sha256 = dotnetHash
                    },
                    new
                    {
                        name = "powershell",
                        path = "C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe",
                        sha256 = powershellHash
                    },
                    new
                    {
                        name = "taskkill", path = "C:/Windows/System32/taskkill.exe",
                        sha256 = taskKillHash
                    }
                }
            };
            var manifestPath = Path.Combine(eng, "physical-chaos-dependencies.v1.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest), new UTF8Encoding(false));
            var manifestHash = Sha256File(manifestPath);
            var authority = new PhysicalChaosController.DependencyAuthority(
                repository, devOps, manifestPath, manifestHash, PhysicalChaosController.DevOpsCommit);
            return new AuthorityFixture(root, launcher, manifestPath, closedDirectory, authority);
        }

        public void Dispose()
        {
            var fullRoot = Path.GetFullPath(Root);
            var tempRoot = Path.GetFullPath(Path.GetTempPath());
            if (!fullRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(fullRoot).StartsWith("deep-chaos-authority-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to delete an unexpected test directory.");
            Directory.Delete(fullRoot, recursive: true);
        }

        private static string Sha256File(string path) => Sha256Bytes(File.ReadAllBytes(path));

        private static string Sha256Bytes(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
