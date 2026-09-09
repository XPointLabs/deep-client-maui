using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Core.ViewModels;
using System.Security.Cryptography;

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
        Assert.False(handedOff.CleanupPending);
        AssertNoOwnedPackageSnapshots(snapshotRoot);

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
        AssertNoOwnedPackageSnapshots(snapshotRoot);
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
                ? new SequencedSignerVerifier(
                    firstPackageId: "network.xpoint.deep.evil",
                    secondPackageId: "network.xpoint.deep.evil")
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
            AssertNoOwnedPackageSnapshots(snapshotRoot);
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
        AssertNoOwnedPackageSnapshots(snapshotRoot);

        _ = await PreserveAsync(fixture, handoff, sandbox.Path);
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(snapshotRoot));

        _ = new VerifiedAndroidPackageHandoffService(
            snapshotRoot,
            signer,
            new RecordingInstallerHandoff(),
            clock);
        AssertNoOwnedPackageSnapshots(snapshotRoot);
    }

    [Fact]
    public async Task TimeToLive_CleansSnapshotWithoutAnotherUserAction()
    {
        using var fixture = new UpdateTrustTestFixture();
        using var sandbox = new TemporaryDirectory();
        var snapshotRoot = Path.Combine(sandbox.Path, "handoff");
        var handoff = new VerifiedAndroidPackageHandoffService(
            snapshotRoot,
            new SequencedSignerVerifier(),
            new RecordingInstallerHandoff(),
            TimeProvider.System,
            timeToLive: TimeSpan.FromMilliseconds(100));

        _ = await PreserveAsync(fixture, handoff, sandbox.Path);
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(snapshotRoot));

        await Task.Delay(TimeSpan.FromMilliseconds(500));

        AssertNoOwnedPackageSnapshots(snapshotRoot);
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
            .RequestInstallAsync(
                new MemoryStream([0x01]),
                expectedLength: 1,
                cancellationToken: CancellationToken.None);

        Assert.False(result.IsAccepted);
        Assert.Contains("Windows", result.Failure, StringComparison.Ordinal);
        Assert.Contains("unsupported", result.Failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ViewModel_NeverRequestsInstallerBeforeExactManualConfirmation()
    {
        using var fixture = new UpdateTrustTestFixture();
        using var sandbox = new TemporaryDirectory();
        var sourcePath = Path.Combine(sandbox.Path, "source.apk");
        await File.WriteAllBytesAsync(sourcePath, fixture.ApkBytes);
        var signer = new SequencedSignerVerifier();
        var installer = new RecordingInstallerHandoff();
        var handoff = new VerifiedAndroidPackageHandoffService(
            Path.Combine(sandbox.Path, "handoff"),
            signer,
            installer,
            new ManualTimeProvider(UpdateTrustTestFixture.UpdateStart));
        var verifier = CreateVerifier(fixture, signer, handoff, sandbox.Path);
        var viewModel = new OfflineUpdateVerificationViewModel(verifier, handoff);

        Assert.False(await viewModel.RequestInstallerHandoffAsync());
        await viewModel.VerifyAsync(
            Request(fixture.BuildBundle(), sourcePath),
            CancellationToken.None);
        Assert.False(viewModel.CanRequestInstaller);
        Assert.False(await viewModel.RequestInstallerHandoffAsync());
        Assert.Equal(0, installer.Calls);

        Assert.False(viewModel.ConfirmExactDetails(
            UpdateTrustTestFixture.PackageId,
            "wrong-version",
            viewModel.SourceCommit,
            viewModel.Expiry));
        Assert.True(viewModel.ConfirmExactDetails(
            UpdateTrustTestFixture.PackageId,
            UpdateTrustTestFixture.VersionName,
            viewModel.SourceCommit,
            viewModel.Expiry));
        Assert.True(viewModel.CanRequestInstaller);

        Assert.True(await viewModel.RequestInstallerHandoffAsync());
        Assert.Equal(1, installer.Calls);
        Assert.False(viewModel.CanRequestInstaller);
        Assert.False(await viewModel.RequestInstallerHandoffAsync());
        Assert.Equal(1, installer.Calls);
    }

    [Fact]
    public async Task OwnedChildRoot_NeverDeletesUnrelatedParentSiblingsOrUnmarkedMiswire()
    {
        using var fixture = new UpdateTrustTestFixture();
        using var sandbox = new TemporaryDirectory();
        var parentRoot = Path.Combine(sandbox.Path, "app-private");
        Directory.CreateDirectory(parentRoot);
        var unrelated = Path.Combine(parentRoot, "user-data.db");
        await File.WriteAllTextAsync(unrelated, "must survive");
        var handoff = new VerifiedAndroidPackageHandoffService(
            parentRoot,
            new SequencedSignerVerifier(),
            new RecordingInstallerHandoff(),
            new ManualTimeProvider(UpdateTrustTestFixture.UpdateStart));

        _ = await PreserveAsync(fixture, handoff, sandbox.Path);
        _ = await PreserveAsync(fixture, handoff, sandbox.Path);

        Assert.True(File.Exists(unrelated));
        Assert.Equal("must survive", await File.ReadAllTextAsync(unrelated));
        Assert.True(Directory.Exists(Path.Combine(
            parentRoot,
            VerifiedAndroidPackageHandoffService.OwnedStoreDirectoryName)));

        _ = new VerifiedAndroidPackageHandoffService(
            parentRoot,
            new SequencedSignerVerifier(),
            new RecordingInstallerHandoff(),
            new ManualTimeProvider(UpdateTrustTestFixture.UpdateStart));
        Assert.True(File.Exists(unrelated));
        AssertNoOwnedPackageSnapshots(parentRoot);

        var miswiredParent = Path.Combine(sandbox.Path, "miswired");
        var unmarkedStore = Path.Combine(
            miswiredParent,
            VerifiedAndroidPackageHandoffService.OwnedStoreDirectoryName);
        Directory.CreateDirectory(unmarkedStore);
        var foreign = Path.Combine(unmarkedStore, "foreign.txt");
        await File.WriteAllTextAsync(foreign, "foreign");

        Assert.Throws<InvalidDataException>(() =>
            new VerifiedAndroidPackageHandoffService(
                miswiredParent,
                new SequencedSignerVerifier(),
                new RecordingInstallerHandoff()));
        Assert.Equal("foreign", await File.ReadAllTextAsync(foreign));
    }

    [Fact]
    public async Task PreCanceledHandoff_ConsumesHandleCleansSnapshotAndCannotReplay()
    {
        using var fixture = new UpdateTrustTestFixture();
        using var sandbox = new TemporaryDirectory();
        var installer = new RecordingInstallerHandoff();
        var handoff = new VerifiedAndroidPackageHandoffService(
            Path.Combine(sandbox.Path, "handoff"),
            new SequencedSignerVerifier(),
            installer,
            new ManualTimeProvider(UpdateTrustTestFixture.UpdateStart));
        var preserved = await PreserveAsync(fixture, handoff, sandbox.Path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handoff.HandOffAsync(
                preserved.Handle,
                userConfirmed: true,
                cancellation.Token));

        AssertNoOwnedPackageSnapshots(Path.Combine(sandbox.Path, "handoff"));
        Assert.False((await handoff.HandOffAsync(
            preserved.Handle,
            userConfirmed: true,
            CancellationToken.None)).IsHandedOff);
        Assert.Equal(0, installer.Calls);
    }

    [Fact]
    public async Task CancellationDuringSignerOrInstaller_CleansAndLeavesNoReplayableState()
    {
        using var fixture = new UpdateTrustTestFixture();
        foreach (var phase in new[] { "signer", "installer" })
        {
            using var sandbox = new TemporaryDirectory();
            var entered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            IAndroidPackageSignerVerifier signer = phase == "signer"
                ? new BlockingSignerVerifier(entered)
                : new SequencedSignerVerifier();
            IAndroidPackageInstallerHandoff installer = phase == "installer"
                ? new BlockingInstallerHandoff(entered)
                : new RecordingInstallerHandoff();
            var handoff = new VerifiedAndroidPackageHandoffService(
                Path.Combine(sandbox.Path, "handoff"),
                signer,
                installer,
                new ManualTimeProvider(UpdateTrustTestFixture.UpdateStart));
            var preserved = await PreserveAsync(fixture, handoff, sandbox.Path);
            using var cancellation = new CancellationTokenSource();
            var task = handoff.HandOffAsync(
                preserved.Handle,
                userConfirmed: true,
                cancellation.Token);

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

            AssertNoOwnedPackageSnapshots(Path.Combine(sandbox.Path, "handoff"));
            Assert.False((await handoff.HandOffAsync(
                preserved.Handle,
                userConfirmed: true,
                CancellationToken.None)).IsHandedOff);
        }
    }

    [Fact]
    public async Task ClaimedSnapshotTimeToLive_CancelsIgnoringAdapterAndCleans()
    {
        using var fixture = new UpdateTrustTestFixture();
        using var sandbox = new TemporaryDirectory();
        var handoff = new VerifiedAndroidPackageHandoffService(
            Path.Combine(sandbox.Path, "handoff"),
            new SequencedSignerVerifier(),
            new IgnoringCancellationInstallerHandoff(),
            TimeProvider.System,
            timeToLive: TimeSpan.FromMilliseconds(100));
        var preserved = await PreserveAsync(fixture, handoff, sandbox.Path);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handoff.HandOffAsync(
                preserved.Handle,
                userConfirmed: true,
                CancellationToken.None));

        AssertNoOwnedPackageSnapshots(Path.Combine(sandbox.Path, "handoff"));
    }

    [Fact]
    public async Task AdapterReceivesOnlyHeldReadStreamAndMustReturnExactCopyReceipt()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new UpdateTrustTestFixture();
        using var sandbox = new TemporaryDirectory();
        var parentRoot = Path.Combine(sandbox.Path, "handoff");
        var dishonest = new DelayedReadInstallerHandoff(returnValidReceipt: false);
        var handoff = new VerifiedAndroidPackageHandoffService(
            parentRoot,
            new SequencedSignerVerifier(),
            dishonest,
            new ManualTimeProvider(UpdateTrustTestFixture.UpdateStart));
        var preserved = await PreserveAsync(fixture, handoff, sandbox.Path);

        var rejected = await handoff.HandOffAsync(
            preserved.Handle,
            userConfirmed: true,
            CancellationToken.None);

        Assert.False(rejected.IsHandedOff);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => dishonest.ReadCapturedStreamAfterReturnAsync());

        var mutating = new MutationAttemptingInstallerHandoff(parentRoot);
        handoff = new VerifiedAndroidPackageHandoffService(
            parentRoot,
            new SequencedSignerVerifier(),
            mutating,
            new ManualTimeProvider(UpdateTrustTestFixture.UpdateStart));
        preserved = await PreserveAsync(fixture, handoff, sandbox.Path);

        var accepted = await handoff.HandOffAsync(
            preserved.Handle,
            userConfirmed: true,
            CancellationToken.None);

        Assert.True(accepted.IsHandedOff);
        Assert.True(mutating.WriterWasBlocked);
        Assert.Equal(fixture.ApkBytes, mutating.ObservedBytes);
    }

    [Fact]
    public async Task PreserveFailure_IsReturnedAsConstantPathFreeFailure()
    {
        using var fixture = new UpdateTrustTestFixture();
        using var sandbox = new TemporaryDirectory();
        var sourcePath = Path.Combine(sandbox.Path, "source.apk");
        await File.WriteAllBytesAsync(sourcePath, fixture.ApkBytes);
        const string privatePath = @"C:\Users\Mr. X\secret\candidate.apk";
        var signer = new SequencedSignerVerifier();
        var verifier = CreateVerifier(
            fixture,
            signer,
            new ThrowingHandoffService(
                new UnauthorizedAccessException($"Access denied: {privatePath}")),
            sandbox.Path);

        var result = await verifier.VerifyAsync(
            Request(fixture.BuildBundle(), sourcePath),
            CancellationToken.None);

        Assert.False(result.IsVerified);
        Assert.Equal(
            "Private verified package handoff storage is unavailable.",
            result.Failure);
        Assert.DoesNotContain(privatePath, result.Failure, StringComparison.Ordinal);
        Assert.DoesNotContain(sandbox.Path, result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CleanupFailure_IsReportedPendingAndRetriedBeforeNextPreserve()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new UpdateTrustTestFixture();
        using var sandbox = new TemporaryDirectory();
        var parentRoot = Path.Combine(sandbox.Path, "handoff");
        var handoff = new VerifiedAndroidPackageHandoffService(
            parentRoot,
            new SequencedSignerVerifier(),
            new RecordingInstallerHandoff(),
            new ManualTimeProvider(UpdateTrustTestFixture.UpdateStart));
        var preserved = await PreserveAsync(fixture, handoff, sandbox.Path);
        var ownedPackage = Directory.GetDirectories(
            Path.Combine(parentRoot, VerifiedAndroidPackageHandoffService.OwnedStoreDirectoryName),
            "pkg-*").Single();
        var lockedPath = Path.Combine(ownedPackage, "cleanup-lock");
        await File.WriteAllTextAsync(lockedPath, "locked");
        await using var locked = new FileStream(
            lockedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        var result = await handoff.HandOffAsync(
            preserved.Handle,
            userConfirmed: true,
            CancellationToken.None);

        Assert.True(result.IsHandedOff);
        Assert.True(result.CleanupPending);
        Assert.True(Directory.Exists(ownedPackage));

        await locked.DisposeAsync();
        _ = await PreserveAsync(fixture, handoff, sandbox.Path);
        Assert.False(Directory.Exists(ownedPackage));
    }

    [Fact]
    public async Task ExpiredHandle_WithLockedDeletion_ReturnsCleanupPendingAndRetriesLater()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new UpdateTrustTestFixture();
        using var sandbox = new TemporaryDirectory();
        var parentRoot = Path.Combine(sandbox.Path, "handoff");
        var clock = new ManualTimeProvider(UpdateTrustTestFixture.UpdateStart);
        var handoff = new VerifiedAndroidPackageHandoffService(
            parentRoot,
            new SequencedSignerVerifier(),
            new RecordingInstallerHandoff(),
            clock,
            timeToLive: TimeSpan.FromMinutes(5));
        var preserved = await PreserveAsync(fixture, handoff, sandbox.Path);
        var ownedPackage = Directory.GetDirectories(
            Path.Combine(parentRoot, VerifiedAndroidPackageHandoffService.OwnedStoreDirectoryName),
            "pkg-*").Single();
        var lockedPath = Path.Combine(ownedPackage, "cleanup-lock");
        await File.WriteAllTextAsync(lockedPath, "locked");
        await using var locked = new FileStream(
            lockedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        clock.Advance(TimeSpan.FromMinutes(6));

        var expired = await handoff.HandOffAsync(
            preserved.Handle,
            userConfirmed: true,
            CancellationToken.None);

        Assert.False(expired.IsHandedOff);
        Assert.Contains("expired", expired.Failure, StringComparison.OrdinalIgnoreCase);
        Assert.True(expired.CleanupPending);
        Assert.True(Directory.Exists(ownedPackage));

        await locked.DisposeAsync();
        _ = await PreserveAsync(fixture, handoff, sandbox.Path);
        Assert.False(Directory.Exists(ownedPackage));
    }

    [Fact]
    public async Task ViewModel_ConvertsHandoffCancellationToControlledRecoverableFailure()
    {
        var verified = new OfflineAndroidPackageVerification(
            true,
            "verified",
            "offline media",
            UpdateTrustTestFixture.TargetPath,
            UpdateTrustTestFixture.PackageId,
            UpdateTrustTestFixture.VersionName,
            UpdateTrustTestFixture.VersionCode,
            "2d1cefd30a1e12b657288bca704aab4b10980dce",
            new DateTimeOffset(2030, 1, 3, 0, 0, 0, TimeSpan.Zero),
            null,
            "a".PadLeft(64, 'a'),
            "opaque",
            new DateTimeOffset(2030, 1, 2, 0, 10, 0, TimeSpan.Zero));
        var viewModel = new OfflineUpdateVerificationViewModel(
            new StaticOfflineVerifier(verified),
            new CancelingHandoffService());
        var request = new OfflineAndroidPackageRequest(
            null!,
            UpdateTrustTestFixture.TargetPath,
            "offline media",
            "source.apk",
            UpdateTrustTestFixture.UpdateStart);
        await viewModel.VerifyAsync(request);
        Assert.True(viewModel.ConfirmExactDetails(
            verified.PackageId,
            verified.VersionName,
            verified.SourceCommit,
            viewModel.Expiry));

        Assert.False(await viewModel.RequestInstallerHandoffAsync());
        Assert.Contains("cancel", viewModel.Failure, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.CanRequestInstaller);

        await viewModel.VerifyAsync(request);
        Assert.True(viewModel.CanConfirm);
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
        private readonly string firstPackageId;
        private readonly string secondPackageId;

        public SequencedSignerVerifier(
            string firstPackageId = UpdateTrustTestFixture.PackageId,
            string secondPackageId = UpdateTrustTestFixture.PackageId)
        {
            this.firstPackageId = firstPackageId;
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
                Calls == 1 ? firstPackageId : secondPackageId,
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
        public byte[]? ObservedBytes { get; private set; }

        public async Task<AndroidPackageInstallerHandoffResult> RequestInstallAsync(
            Stream verifiedPackage,
            long expectedLength,
            CancellationToken cancellationToken)
        {
            Calls++;
            using var copy = new MemoryStream();
            await verifiedPackage.CopyToAsync(copy, cancellationToken);
            ObservedBytes = copy.ToArray();
            return new AndroidPackageInstallerHandoffResult(
                accept,
                ObservedBytes.LongLength,
                Convert.ToHexString(SHA256.HashData(ObservedBytes)).ToLowerInvariant(),
                accept ? null : "Synthetic platform rejection.");
        }
    }

    private sealed class BlockingSignerVerifier : IAndroidPackageSignerVerifier
    {
        private readonly TaskCompletionSource entered;

        public BlockingSignerVerifier(TaskCompletionSource entered)
        {
            this.entered = entered;
        }

        public async Task<AndroidPackageSignerResult> VerifySnapshotAsync(
            string snapshotPath,
            CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class BlockingInstallerHandoff : IAndroidPackageInstallerHandoff
    {
        private readonly TaskCompletionSource entered;

        public BlockingInstallerHandoff(TaskCompletionSource entered)
        {
            this.entered = entered;
        }

        public async Task<AndroidPackageInstallerHandoffResult> RequestInstallAsync(
            Stream verifiedPackage,
            long expectedLength,
            CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class DelayedReadInstallerHandoff : IAndroidPackageInstallerHandoff
    {
        private readonly bool returnValidReceipt;
        private Stream? captured;

        public DelayedReadInstallerHandoff(bool returnValidReceipt)
        {
            this.returnValidReceipt = returnValidReceipt;
        }

        public async Task<AndroidPackageInstallerHandoffResult> RequestInstallAsync(
            Stream verifiedPackage,
            long expectedLength,
            CancellationToken cancellationToken)
        {
            captured = verifiedPackage;
            if (!returnValidReceipt)
            {
                return new AndroidPackageInstallerHandoffResult(true, 0, null, null);
            }
            using var copy = new MemoryStream();
            await verifiedPackage.CopyToAsync(copy, cancellationToken);
            var bytes = copy.ToArray();
            return new AndroidPackageInstallerHandoffResult(
                true,
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                null);
        }

        public async Task ReadCapturedStreamAfterReturnAsync()
        {
            var buffer = new byte[1];
            _ = await captured!.ReadAsync(buffer);
        }
    }

    private sealed class IgnoringCancellationInstallerHandoff
        : IAndroidPackageInstallerHandoff
    {
        private readonly TaskCompletionSource<AndroidPackageInstallerHandoffResult>
            never = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AndroidPackageInstallerHandoffResult> RequestInstallAsync(
            Stream verifiedPackage,
            long expectedLength,
            CancellationToken cancellationToken) =>
            never.Task;
    }

    private sealed class MutationAttemptingInstallerHandoff
        : IAndroidPackageInstallerHandoff
    {
        private readonly string parentRoot;

        public MutationAttemptingInstallerHandoff(string parentRoot)
        {
            this.parentRoot = parentRoot;
        }

        public bool WriterWasBlocked { get; private set; }
        public byte[]? ObservedBytes { get; private set; }

        public async Task<AndroidPackageInstallerHandoffResult> RequestInstallAsync(
            Stream verifiedPackage,
            long expectedLength,
            CancellationToken cancellationToken)
        {
            var snapshot = Directory.EnumerateFiles(
                parentRoot,
                "*.apk",
                SearchOption.AllDirectories).Single();
            try
            {
                await File.AppendAllTextAsync(snapshot, "mutation", cancellationToken);
            }
            catch (IOException)
            {
                WriterWasBlocked = true;
            }

            using var copy = new MemoryStream();
            await verifiedPackage.CopyToAsync(copy, cancellationToken);
            ObservedBytes = copy.ToArray();
            return new AndroidPackageInstallerHandoffResult(
                true,
                ObservedBytes.LongLength,
                Convert.ToHexString(SHA256.HashData(ObservedBytes)).ToLowerInvariant(),
                null);
        }
    }

    private sealed class ThrowingHandoffService
        : IVerifiedAndroidPackageHandoffService
    {
        private readonly Exception exception;

        public ThrowingHandoffService(Exception exception)
        {
            this.exception = exception;
        }

        public Task<PreservedAndroidPackageHandle> PreserveVerifiedSnapshotAsync(
            string verifiedSnapshotPath,
            VerifiedAndroidTarget target,
            CancellationToken cancellationToken) =>
            Task.FromException<PreservedAndroidPackageHandle>(exception);

        public Task<VerifiedAndroidPackageHandoffResult> HandOffAsync(
            string handle,
            bool userConfirmed,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StaticOfflineVerifier : IOfflineAndroidUpdateVerifier
    {
        private readonly OfflineAndroidPackageVerification result;

        public StaticOfflineVerifier(OfflineAndroidPackageVerification result)
        {
            this.result = result;
        }

        public Task<OfflineAndroidPackageVerification> VerifyAsync(
            OfflineAndroidPackageRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class CancelingHandoffService
        : IVerifiedAndroidPackageHandoffService
    {
        public Task<PreservedAndroidPackageHandle> PreserveVerifiedSnapshotAsync(
            string verifiedSnapshotPath,
            VerifiedAndroidTarget target,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<VerifiedAndroidPackageHandoffResult> HandOffAsync(
            string handle,
            bool userConfirmed,
            CancellationToken cancellationToken) =>
            Task.FromCanceled<VerifiedAndroidPackageHandoffResult>(
                new CancellationToken(canceled: true));
    }

    private static void AssertNoOwnedPackageSnapshots(string parentRoot)
    {
        Assert.Empty(Directory.Exists(parentRoot)
            ? Directory.EnumerateFiles(parentRoot, "*.apk", SearchOption.AllDirectories)
            : []);
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
