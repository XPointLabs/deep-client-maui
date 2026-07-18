using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Core.ViewModels;
using System.Security.Cryptography;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class PortableUpdateMetadataVerifierTests
{
    [Fact]
    public async Task ValidOfflinePackage_VerifiesAndPersistsAcrossRestart()
    {
        using var fixture = new UpdateTrustTestFixture();
        using var sandbox = new TemporaryDirectory();
        var statePath = Path.Combine(sandbox.Path, "trusted-state.json");
        var apkPath = Path.Combine(sandbox.Path, "candidate.apk");
        await File.WriteAllBytesAsync(apkPath, fixture.ApkBytes);
        var store = new AtomicTrustedUpdateStateStore(statePath);
        var signer = new TestSignerVerifier(
            UpdateTrustTestFixture.PackageId,
            UpdateTrustTestFixture.PackageSignerSha256,
            _ =>
            {
                var original = File.ReadAllBytes(apkPath);
                File.WriteAllText(apkPath, "temporary carrier ABA swap");
                File.WriteAllBytes(apkPath, original);
            });
        var verifier = CreateVerifier(fixture, store, signer, sandbox.Path);

        var result = await verifier.VerifyAsync(
            Request(fixture.BuildBundle(), apkPath),
            CancellationToken.None);

        Assert.True(result.IsVerified);
        Assert.Equal(UpdateTrustTestFixture.VersionName, result.VersionName);
        Assert.Equal(UpdateTrustTestFixture.VersionCode, result.VersionCode);
        Assert.Equal(1, signer.Calls);
        Assert.NotEqual(apkPath, signer.ObservedPath);
        Assert.Equal(fixture.ApkBytes, signer.ObservedBytes);

        var reopened = new AtomicTrustedUpdateStateStore(statePath);
        var state = await reopened.LoadAsync(CancellationToken.None);
        Assert.NotNull(state);
        Assert.Equal(1, state.Versions.Timestamp);
        Assert.Equal(1, state.Versions.AndroidRelease);

        var rollback = await CreateVerifier(fixture, reopened, signer, sandbox.Path).VerifyAsync(
            Request(fixture.BuildBundle(), apkPath),
            CancellationToken.None);
        Assert.False(rollback.IsVerified);
        Assert.Contains("rollback", rollback.Failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WrongSignerHashLengthStaleAndMixAndMatch_AllFailClosed()
    {
        using var fixture = new UpdateTrustTestFixture();
        using var sandbox = new TemporaryDirectory();
        var cases = new List<(string Name, Func<string, UpdateMetadataBundle> Bundle, byte[] Apk,
            TestSignerVerifier Signer, string Expected)>
        {
            (
                "wrong signer",
                _ => fixture.BuildBundle(),
                fixture.ApkBytes,
                new TestSignerVerifier(UpdateTrustTestFixture.PackageId, "f".PadLeft(64, 'f')),
                "signer"),
            (
                "wrong package version",
                _ => fixture.BuildBundle(),
                fixture.ApkBytes,
                new TestSignerVerifier(
                    UpdateTrustTestFixture.PackageId,
                    UpdateTrustTestFixture.PackageSignerSha256,
                    versionCode: "20000"),
                "version"),
            (
                "wrong hash",
                _ => fixture.BuildBundle(),
                MutateSameLength(fixture.ApkBytes),
                new TestSignerVerifier(
                    UpdateTrustTestFixture.PackageId,
                    UpdateTrustTestFixture.PackageSignerSha256),
                "SHA-256"),
            (
                "wrong length",
                _ => fixture.BuildBundle(),
                fixture.ApkBytes.Concat(new byte[] { 0x01 }).ToArray(),
                new TestSignerVerifier(
                    UpdateTrustTestFixture.PackageId,
                    UpdateTrustTestFixture.PackageSignerSha256),
                "length"),
            (
                "stale",
                _ => fixture.BuildBundle(expiry: "2029-12-31T23:59:59Z"),
                fixture.ApkBytes,
                new TestSignerVerifier(
                    UpdateTrustTestFixture.PackageId,
                    UpdateTrustTestFixture.PackageSignerSha256),
                "expired"),
            (
                "mix",
                _ =>
                {
                    var baseBundle = fixture.BuildBundle();
                    return baseBundle with { AndroidRelease = fixture.BuildDelegatedOnly(2) };
                },
                fixture.ApkBytes,
                new TestSignerVerifier(
                    UpdateTrustTestFixture.PackageId,
                    UpdateTrustTestFixture.PackageSignerSha256),
                "parent metadata")
        };

        foreach (var item in cases)
        {
            var caseRoot = Path.Combine(sandbox.Path, item.Name.Replace(' ', '-'));
            Directory.CreateDirectory(caseRoot);
            var apkPath = Path.Combine(caseRoot, "candidate.apk");
            await File.WriteAllBytesAsync(apkPath, item.Apk);
            var store = new AtomicTrustedUpdateStateStore(Path.Combine(caseRoot, "state.json"));
            var verifier = CreateVerifier(fixture, store, item.Signer, caseRoot);
            var result = await verifier.VerifyAsync(
                Request(item.Bundle(apkPath), apkPath),
                CancellationToken.None);

            Assert.False(result.IsVerified);
            Assert.Contains(item.Expected, result.Failure, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("заблокирована", result.Status, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task RootRotation_RequiresBothThresholdsPersistsBeforeLaterFailureAndRevokesOldSigner()
    {
        using var fixture = new UpdateTrustTestFixture();
        var metadataVerifier = new PortableUpdateMetadataVerifier();
        var initial = PortableUpdateMetadataVerifier.CreateInitialState(fixture.RootOne);
        var persisted = new List<TrustedUpdateState>();

        var verified = await metadataVerifier.VerifyAsync(
            fixture.BuildBundle(rotated: true),
            initial,
            UpdateTrustTestFixture.UpdateStart,
            UpdateTrustTestFixture.TargetPath,
            (state, _) =>
            {
                persisted.Add(state);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(2, verified.RootVersion);
        Assert.Contains(persisted, state => state.TrustedRoot.Version == 2);

        await Assert.ThrowsAsync<InvalidDataException>(() => metadataVerifier.VerifyAsync(
            fixture.BuildBundle(rotated: true, oldThresholdOnlyCandidate: true),
            initial,
            UpdateTrustTestFixture.UpdateStart,
            UpdateTrustTestFixture.TargetPath,
            null,
            CancellationToken.None));

        persisted.Clear();
        var revoked = await Assert.ThrowsAsync<InvalidDataException>(() =>
            metadataVerifier.VerifyAsync(
                fixture.BuildBundle(rotated: true, revokedTimestampSigner: true),
                initial,
                UpdateTrustTestFixture.UpdateStart,
                UpdateTrustTestFixture.TargetPath,
                (state, _) =>
                {
                    persisted.Add(state);
                    return Task.CompletedTask;
                },
                CancellationToken.None));
        Assert.Contains("unknown signer", revoked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(persisted, state => state.TrustedRoot.Version == 2);
    }

    [Fact]
    public async Task AtomicState_IgnoresOrphanTempAndRejectsCorruptMainState()
    {
        using var fixture = new UpdateTrustTestFixture();
        using var sandbox = new TemporaryDirectory();
        var statePath = Path.Combine(sandbox.Path, "state.json");
        var state = PortableUpdateMetadataVerifier.CreateInitialState(fixture.RootOne);
        var store = new AtomicTrustedUpdateStateStore(statePath);
        await store.SaveAsync(state, CancellationToken.None);
        await File.WriteAllTextAsync(
            Path.Combine(sandbox.Path, ".state.json.partial.tmp"),
            "{\"partial\":",
            CancellationToken.None);

        Assert.Equal(state, await store.LoadAsync(CancellationToken.None));

        await File.WriteAllTextAsync(
            statePath,
            "{\"Schema\":\"deep.update-trust.client-state.v1\"",
            CancellationToken.None);
        await Assert.ThrowsAnyAsync<Exception>(
            () => store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AtomicState_RejectsStaleConcurrentWriterAndPreservesNewestVersions()
    {
        using var fixture = new UpdateTrustTestFixture();
        using var sandbox = new TemporaryDirectory();
        var statePath = Path.Combine(sandbox.Path, "state.json");
        var firstStore = new AtomicTrustedUpdateStateStore(statePath);
        var secondStore = new AtomicTrustedUpdateStateStore(statePath);
        var initial = PortableUpdateMetadataVerifier.CreateInitialState(fixture.RootOne);
        await firstStore.SaveAsync(initial, CancellationToken.None);

        var newest = initial with
        {
            Versions = new TrustedMetadataVersions(2, 2, 2, 2)
        };
        var stale = initial with
        {
            Versions = new TrustedMetadataVersions(1, 1, 1, 1)
        };
        await firstStore.SaveAsync(newest, CancellationToken.None);

        var rejected = await Assert.ThrowsAsync<InvalidDataException>(
            () => secondStore.SaveAsync(stale, CancellationToken.None));
        Assert.Contains("rollback", rejected.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(newest, await secondStore.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MalformedMetadata_ReturnsBlockedResultInsteadOfEscapingException()
    {
        using var fixture = new UpdateTrustTestFixture();
        using var sandbox = new TemporaryDirectory();
        var apkPath = Path.Combine(sandbox.Path, "candidate.apk");
        await File.WriteAllBytesAsync(apkPath, fixture.ApkBytes);
        var malformedRoot = PortableUpdateMetadataVerifier.Canonicalize(
            """{"signatures":[{"keyid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","sig":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}],"signed":{"_type":"root","expires":"2031-01-01T00:00:00Z","spec_version":"1.0.35","version":2}}"""u8.ToArray());
        var bundle = fixture.BuildBundle() with
        {
            CandidateRoots = new[] { new ReadOnlyMemory<byte>(malformedRoot) }
        };
        var verifier = CreateVerifier(
            fixture,
            new AtomicTrustedUpdateStateStore(Path.Combine(sandbox.Path, "state.json")),
            new TestSignerVerifier(
                UpdateTrustTestFixture.PackageId,
                UpdateTrustTestFixture.PackageSignerSha256),
            sandbox.Path);

        var result = await verifier.VerifyAsync(
            Request(bundle, apkPath),
            CancellationToken.None);

        Assert.False(result.IsVerified);
        Assert.Contains("заблокирована", result.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RawMetadata_RejectsWhitespaceBomAndDuplicateObjectKeys()
    {
        using var fixture = new UpdateTrustTestFixture();
        Assert.Throws<InvalidDataException>(() =>
            PortableUpdateMetadataVerifier.CreateInitialState(
                fixture.RootOne.Concat(new byte[] { (byte)' ' }).ToArray()));
        Assert.Throws<InvalidDataException>(() =>
            PortableUpdateMetadataVerifier.CreateInitialState(
                new byte[] { 0xef, 0xbb, 0xbf }.Concat(fixture.RootOne).ToArray()));
        Assert.Throws<InvalidDataException>(() =>
            PortableUpdateMetadataVerifier.Canonicalize(
                "{\"a\":1,\"a\":1}"u8.ToArray()));
    }

    [Fact]
    public async Task DisabledConfigurationAndIosAdapter_AreExplicitlyFailClosed()
    {
        using var fixture = new UpdateTrustTestFixture();
        using var sandbox = new TemporaryDirectory();
        var apkPath = Path.Combine(sandbox.Path, "candidate.apk");
        await File.WriteAllBytesAsync(apkPath, fixture.ApkBytes);
        var store = new AtomicTrustedUpdateStateStore(Path.Combine(sandbox.Path, "state.json"));
        var disabled = new OfflineAndroidUpdateVerifier(
            UpdateTrustConfiguration.Disabled,
            new PortableUpdateMetadataVerifier(),
            store,
            new TestSignerVerifier(
                UpdateTrustTestFixture.PackageId,
                UpdateTrustTestFixture.PackageSignerSha256),
            sandbox.Path);

        var disabledResult = await disabled.VerifyAsync(
            Request(fixture.BuildBundle(), apkPath),
            CancellationToken.None);
        Assert.False(disabledResult.IsVerified);
        Assert.Contains("не настроена", disabledResult.Failure, StringComparison.OrdinalIgnoreCase);

        var ios = await new UnsupportedPlatformPackageSignerVerifier("iOS")
            .VerifySnapshotAsync(apkPath, CancellationToken.None);
        Assert.False(ios.IsValid);
        Assert.Contains("Apple", ios.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SnapshotMutationDuringPlatformInspection_IsRejected()
    {
        using var fixture = new UpdateTrustTestFixture();
        using var sandbox = new TemporaryDirectory();
        var apkPath = Path.Combine(sandbox.Path, "candidate.apk");
        await File.WriteAllBytesAsync(apkPath, fixture.ApkBytes);
        var signer = new TestSignerVerifier(
            UpdateTrustTestFixture.PackageId,
            UpdateTrustTestFixture.PackageSignerSha256,
            snapshot => File.AppendAllText(snapshot, "mutated"));
        var verifier = CreateVerifier(
            fixture,
            new AtomicTrustedUpdateStateStore(Path.Combine(sandbox.Path, "state.json")),
            signer,
            sandbox.Path);

        var result = await verifier.VerifyAsync(
            Request(fixture.BuildBundle(), apkPath),
            CancellationToken.None);

        Assert.False(result.IsVerified);
        Assert.Contains("snapshot changed", result.Failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ViewModel_RequiresExactVisibleVersionAndOffersNoFailureOverride()
    {
        var success = new OfflineAndroidPackageVerification(
            true,
            "verified",
            "USB drive",
            UpdateTrustTestFixture.TargetPath,
            UpdateTrustTestFixture.PackageId,
            UpdateTrustTestFixture.VersionName,
            UpdateTrustTestFixture.VersionCode,
            "9dc1502392ce2c1a86441df6308f2db54410eae8",
            new DateTimeOffset(2030, 1, 3, 0, 0, 0, TimeSpan.Zero),
            null,
            "a".PadLeft(64, 'a'));
        var viewModel = new OfflineUpdateVerificationViewModel(
            new StubOfflineVerifier(success));
        await viewModel.VerifyAsync(
            new OfflineAndroidPackageRequest(
                null!,
                UpdateTrustTestFixture.TargetPath,
                "USB drive",
                "candidate.apk",
                UpdateTrustTestFixture.UpdateStart),
            CancellationToken.None);

        Assert.True(viewModel.CanConfirm);
        Assert.Equal("USB drive", viewModel.Source);
        Assert.Equal(UpdateTrustTestFixture.VersionName, viewModel.Version);
        Assert.False(viewModel.ConfirmExactDetails(
            UpdateTrustTestFixture.PackageId,
            "2.0.1",
            viewModel.SourceCommit,
            viewModel.Expiry));
        Assert.True(viewModel.ConfirmExactDetails(
            UpdateTrustTestFixture.PackageId,
            UpdateTrustTestFixture.VersionName,
            "9dc1502392ce2c1a86441df6308f2db54410eae8",
            "2030-01-03 00:00 UTC"));
        Assert.True(viewModel.IsConfirmed);
        Assert.False(viewModel.CanConfirm);

        var failureViewModel = new OfflineUpdateVerificationViewModel(
            new StubOfflineVerifier(success with
            {
                IsVerified = false,
                Status = "blocked",
                Failure = "wrong signer"
            }));
        await failureViewModel.VerifyAsync(
            new OfflineAndroidPackageRequest(
                null!,
                UpdateTrustTestFixture.TargetPath,
                "mirror",
                "candidate.apk",
                UpdateTrustTestFixture.UpdateStart),
            CancellationToken.None);
        Assert.False(failureViewModel.CanConfirm);
        Assert.False(failureViewModel.ConfirmExactDetails(
            UpdateTrustTestFixture.PackageId,
            UpdateTrustTestFixture.VersionName,
            viewModel.SourceCommit,
            viewModel.Expiry));
        Assert.Equal("wrong signer", failureViewModel.Failure);
    }

    private static OfflineAndroidUpdateVerifier CreateVerifier(
        UpdateTrustTestFixture fixture,
        ITrustedUpdateStateStore store,
        IAndroidPackageSignerVerifier signer,
        string root) =>
        new(
            new UpdateTrustConfiguration(true, fixture.RootOne, "insecure test fixture"),
            new PortableUpdateMetadataVerifier(),
            store,
            signer,
            Path.Combine(root, "snapshots"));

    private static OfflineAndroidPackageRequest Request(
        UpdateMetadataBundle bundle,
        string apkPath) =>
        new(
            bundle,
            UpdateTrustTestFixture.TargetPath,
            "offline test media",
            apkPath,
            UpdateTrustTestFixture.UpdateStart);

    private static byte[] MutateSameLength(byte[] value)
    {
        var result = value.ToArray();
        result[^1] ^= 0x01;
        return result;
    }

    private sealed class TestSignerVerifier : IAndroidPackageSignerVerifier
    {
        private readonly string packageId;
        private readonly string signer;
        private readonly Action<string>? duringVerification;
        private readonly string versionCode;
        private readonly string versionName;

        public TestSignerVerifier(
            string packageId,
            string signer,
            Action<string>? duringVerification = null,
            string versionCode = UpdateTrustTestFixture.VersionCode,
            string versionName = UpdateTrustTestFixture.VersionName)
        {
            this.packageId = packageId;
            this.signer = signer;
            this.duringVerification = duringVerification;
            this.versionCode = versionCode;
            this.versionName = versionName;
        }

        public int Calls { get; private set; }
        public string? ObservedPath { get; private set; }
        public byte[]? ObservedBytes { get; private set; }

        public async Task<AndroidPackageSignerResult> VerifySnapshotAsync(
            string snapshotPath,
            CancellationToken cancellationToken)
        {
            Calls++;
            ObservedPath = snapshotPath;
            ObservedBytes = await File.ReadAllBytesAsync(snapshotPath, cancellationToken);
            duringVerification?.Invoke(snapshotPath);
            return new AndroidPackageSignerResult(
                true,
                packageId,
                signer,
                versionCode,
                versionName,
                null);
        }
    }

    private sealed class StubOfflineVerifier : IOfflineAndroidUpdateVerifier
    {
        private readonly OfflineAndroidPackageVerification result;

        public StubOfflineVerifier(OfflineAndroidPackageVerification result)
        {
            this.result = result;
        }

        public Task<OfflineAndroidPackageVerification> VerifyAsync(
            OfflineAndroidPackageRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"deep-p02b-{Guid.NewGuid():N}");
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
