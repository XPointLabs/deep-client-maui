using Deep.Client.Maui.Core.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class VerifiedAndroidPackageHandoffServiceTests
{
    [Fact]
    public async Task VerifiedPackage_ReturnsOpaqueHandleAndHandsOffExactPrivateSnapshotOnce()
    {
        using var fixture = new UpdateTrustTestFixture();
        using var sandbox = new TemporaryDirectory();
        var sourcePath = Path.Combine(sandbox.Path, "source.apk");
        var snapshotRoot = Path.Combine(sandbox.Path, "handoff");
        await File.WriteAllBytesAsync(sourcePath, fixture.ApkBytes);
        var signer = new SequencedSignerVerifier();
        var installer = new RecordingInstallerHandoff();
        var clock = new ManualTimeProvider(UpdateTrustTestFixture.UpdateStart);
        var handoff = new VerifiedAndroidPackageHandoffService(
            snapshotRoot,
            signer,
            installer,
            clock);
        var verifier = CreateVerifier(fixture, signer, handoff, sandbox.Path);

        var verified = await verifier.VerifyAsync(
            Request(fixture.BuildBundle(), sourcePath),
            CancellationToken.None);

        Assert.True(verified.IsVerified);
        Assert.False(string.IsNullOrWhiteSpace(verified.HandoffHandle));
        Assert.DoesNotContain(sourcePath, verified.HandoffHandle, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, verified.HandoffHandle);
        Assert.True(verified.HandoffExpiresAtUtc > clock.GetUtcNow());
        Assert.Single(Directory.EnumerateFiles(snapshotRoot, "*.apk", SearchOption.AllDirectories));

        var handedOff = await handoff.HandOffAsync(
            verified.HandoffHandle!,
            userConfirmed: true,
            CancellationToken.None);

        Assert.True(handedOff.IsHandedOff);
        Assert.Equal(fixture.ApkBytes, installer.ObservedBytes);
        Assert.NotEqual(sourcePath, installer.ObservedPath);
        Assert.Empty(Directory.EnumerateFileSystemEntries(snapshotRoot));

        var replay = await handoff.HandOffAsync(
            verified.HandoffHandle!,
            userConfirmed: true,
            CancellationToken.None);
        Assert.False(replay.IsHandedOff);
        Assert.Contains("invalid", replay.Failure, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, installer.Calls);
    }

    [Fact]
    public async Task HandoffWithoutExplicitConfirmation_ConsumesHandleAndNeverCallsInstaller()
    {
        using var fixture = new UpdateTrustTestFixture();
        using var sandbox = new TemporaryDirectory();
        var snapshotRoot = Path.Combine(sandbox.Path, "handoff");
        var signer = new SequencedSignerVerifier();
        var installer = new RecordingInstallerHandoff();
        var handoff = new VerifiedAndroidPackageHandoffService(
            snapshotRoot,
            signer,
            installer,
            new ManualTimeProvider(UpdateTrustTestFixture.UpdateStart));
        var preserved = await PreserveAsync(fixture, handoff, sandbox.Path);

        var result = await handoff.HandOffAsync(
            preserved.Handle,
            userConfirmed: false,
            CancellationToken.None);

        Assert.False(result.IsHandedOff);
        Assert.Contains("confirm", result.Failure, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, installer.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(snapshotRoot));
        Assert.False((await handoff.HandOffAsync(
            preserved.Handle,
            userConfirmed: true,
            CancellationToken.None)).IsHandedOff);
    }

    [Fact]
    public async Task TamperExpiryAndIdentityChange_AllFailClosedAndCleanSnapshot()
    {
        using var fixture = new UpdateTrustTestFixture();
        foreach (var scenario in new[] { "tamper", "expiry", "identity" })
        {
            using var sandbox = new TemporaryDirectory();
            var snapshotRoot = Path.Combine(sandbox.Path, "handoff");
            var clock = new ManualTimeProvider(UpdateTrustTestFixture.UpdateStart);
            var signer = scenario == "identity"
                ? new SequencedSignerVerifier(secondPackageId: "network.xpoint.deep.evil")
                : new SequencedSignerVerifier();
            var installer = new RecordingInstallerHandoff();
            var handoff = new VerifiedAndroidPackageHandoffService(
                snapshotRoot,
                signer,
                installer,
                clock,
                timeToLive: TimeSpan.FromMinutes(5));
            var preserved = await PreserveAsync(fixture, handoff, sandbox.Path);

            if (scenario == "tamper")
            {
                var path = Assert.Single(
                    Directory.EnumerateFiles(snapshotRoot, "*.apk", SearchOption.AllDirectories));
                await File.AppendAllTextAsync(path, "tamper");
            }
            else if (scenario == "expiry")
            {
                clock.Advance(TimeSpan.FromMinutes(6));
            }

            var result = await handoff.HandOffAsync(
                preserved.Handle,
                userConfirmed: true,
                CancellationToken.None);

            Assert.False(result.IsHandedOff);
            Assert.Equal(0, installer.Calls);
            Assert.Empty(Directory.EnumerateFileSystemEntries(snapshotRoot));
        }
    }

    [Fact]
    public async Task InstallerFailureAndProcessRestart_CleanPrivateSnapshots()
    {
        using var fixture = new UpdateTrustTestFixture();
        using var sandbox = new TemporaryDirectory();
        var snapshotRoot = Path.Combine(sandbox.Path, "handoff");
        var clock = new ManualTimeProvider(UpdateTrustTestFixture.UpdateStart);
        var signer = new SequencedSignerVerifier();
        var failingInstaller = new RecordingInstallerHandoff(accept: false);
        var handoff = new VerifiedAndroidPackageHandoffService(
            snapshotRoot,
            signer,
            failingInstaller,
            clock);
        var preserved = await PreserveAsync(fixture, handoff, sandbox.Path);

        var failed = await handoff.HandOffAsync(
            preserved.Handle,
            userConfirmed: true,
            CancellationToken.None);

        Assert.False(failed.IsHandedOff);
        Assert.Empty(Directory.EnumerateFileSystemEntries(snapshotRoot));

        _ = await PreserveAsync(fixture, handoff, sandbox.Path);
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(snapshotRoot));

        _ = new VerifiedAndroidPackageHandoffService(
            snapshotRoot,
            signer,
            new RecordingInstallerHandoff(),
            clock);
        Assert.Empty(Directory.EnumerateFileSystemEntries(snapshotRoot));
    }

    [Fact]
    public async Task NewPreservedPackageInvalidatesPreviousHandleAndBoundsStoreToOneSnapshot()
    {
        using var fixture = new UpdateTrustTestFixture();
        using var sandbox = new TemporaryDirectory();
        var snapshotRoot = Path.Combine(sandbox.Path, "handoff");
        var installer = new RecordingInstallerHandoff();
        var handoff = new VerifiedAndroidPackageHandoffService(
            snapshotRoot,
            new SequencedSignerVerifier(),
            installer,
            new ManualTimeProvider(UpdateTrustTestFixture.UpdateStart));

        var first = await PreserveAsync(fixture, handoff, sandbox.Path);
        var second = await PreserveAsync(fixture, handoff, sandbox.Path);

        Assert.Single(Directory.EnumerateFiles(snapshotRoot, "*.apk", SearchOption.AllDirectories));
        Assert.False((await handoff.HandOffAsync(
            first.Handle,
            userConfirmed: true,
            CancellationToken.None)).IsHandedOff);
        Assert.True((await handoff.HandOffAsync(
            second.Handle,
            userConfirmed: true,
            CancellationToken.None)).IsHandedOff);
    }

    [Fact]
    public async Task WindowsInstallerBoundary_IsExplicitlyUnsupported()
    {
        var result = await new UnsupportedAndroidPackageInstallerHandoff("Windows")
            .RequestInstallAsync("private-snapshot.apk", CancellationToken.None);

        Assert.False(result.IsAccepted);
        Assert.Contains("Windows", result.Failure, StringComparison.Ordinal);
        Assert.Contains("unsupported", result.Failure, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<PreservedAndroidPackageHandle> PreserveAsync(
        UpdateTrustTestFixture fixture,
        VerifiedAndroidPackageHandoffService handoff,
        string sandboxRoot)
    {
        var stagingDirectory = Path.Combine(sandboxRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        var stagingPath = Path.Combine(stagingDirectory, "candidate.apk");
        await File.WriteAllBytesAsync(stagingPath, fixture.ApkBytes);
        return await handoff.PreserveVerifiedSnapshotAsync(
            stagingPath,
            new VerifiedAndroidTarget(
                UpdateTrustTestFixture.TargetPath,
                fixture.ApkBytes.LongLength,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fixture.ApkBytes))
                    .ToLowerInvariant(),
                UpdateTrustTestFixture.PackageId,
                UpdateTrustTestFixture.PackageSignerSha256,
                UpdateTrustTestFixture.VersionCode,
                UpdateTrustTestFixture.VersionName,
                "2d1cefd30a1e12b657288bca704aab4b10980dce",
                new DateTimeOffset(2030, 1, 3, 0, 0, 0, TimeSpan.Zero)),
            CancellationToken.None);
    }

    private static OfflineAndroidUpdateVerifier CreateVerifier(
        UpdateTrustTestFixture fixture,
        IAndroidPackageSignerVerifier signer,
        IVerifiedAndroidPackageHandoffService handoff,
        string root) =>
        new(
            new UpdateTrustConfiguration(true, fixture.RootOne, "insecure test fixture"),
            new PortableUpdateMetadataVerifier(),
            new AtomicTrustedUpdateStateStore(Path.Combine(root, "state.json")),
            signer,
            handoff,
            Path.Combine(root, "verification-staging"));

    private static OfflineAndroidPackageRequest Request(
        UpdateMetadataBundle bundle,
        string apkPath) =>
        new(
            bundle,
            UpdateTrustTestFixture.TargetPath,
            "offline test media",
            apkPath,
            UpdateTrustTestFixture.UpdateStart);

    private sealed class SequencedSignerVerifier : IAndroidPackageSignerVerifier
    {
        private readonly string secondPackageId;

        public SequencedSignerVerifier(
            string secondPackageId = UpdateTrustTestFixture.PackageId)
        {
            this.secondPackageId = secondPackageId;
        }

        public int Calls { get; private set; }

        public Task<AndroidPackageSignerResult> VerifySnapshotAsync(
            string snapshotPath,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new AndroidPackageSignerResult(
                true,
                Calls == 1 ? UpdateTrustTestFixture.PackageId : secondPackageId,
                UpdateTrustTestFixture.PackageSignerSha256,
                UpdateTrustTestFixture.VersionCode,
                UpdateTrustTestFixture.VersionName,
                null));
        }
    }

    private sealed class RecordingInstallerHandoff : IAndroidPackageInstallerHandoff
    {
        private readonly bool accept;

        public RecordingInstallerHandoff(bool accept = true)
        {
            this.accept = accept;
        }

        public int Calls { get; private set; }
        public string? ObservedPath { get; private set; }
        public byte[]? ObservedBytes { get; private set; }

        public async Task<AndroidPackageInstallerHandoffResult> RequestInstallAsync(
            string privateVerifiedSnapshotPath,
            CancellationToken cancellationToken)
        {
            Calls++;
            ObservedPath = privateVerifiedSnapshotPath;
            ObservedBytes = await File.ReadAllBytesAsync(
                privateVerifiedSnapshotPath,
                cancellationToken);
            return new AndroidPackageInstallerHandoffResult(
                accept,
                accept ? null : "Synthetic platform rejection.");
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            this.utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration)
        {
            utcNow += duration;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"deep-p02d-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Test cleanup is best effort.
            }
        }
    }
}
