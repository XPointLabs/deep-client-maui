using System.IO.Compression;
using System.Security.Cryptography;
using Deep.Client.Maui.Services;
using Sodium;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class ProductionAndroidCodeTransparencyTests
{
    [Fact]
    public async Task SignedManifestRejectsDifferentArtifactWithSamePublicTupleAndBuildId()
    {
        var root = CreateRoot();
        try
        {
            var approved = Path.Combine(root, "approved.apk");
            var substituted = Path.Combine(root, "substituted.apk");
            CreateApk(approved, [1, 2, 3, 4]);
            CreateApk(substituted, [1, 2, 3, 5]);
            var signer = Fill(0x31);
            var mrX = PublicKeyAuth.GenerateKeyPair(Fill(0x61));
            var digest = await ProductionAndroidSemanticApkDigest.ComputeAsync(approved);
            var unsigned = new ProductionAndroidTransparencyManifest
            {
                ApplicationId = "network.xpoint.deep",
                VersionCode = 15,
                PlaySignerLineageSha256 = [signer],
                Artifacts = [new("base", digest)],
                MrXEd25519PublicKey = mrX.PublicKey,
                Signature = new byte[64]
            };
            var signingBytes = ProductionAndroidTransparencyManifestCodec
                .GetSigningBytes(unsigned);
            var manifest = new ProductionAndroidTransparencyManifest
            {
                ApplicationId = unsigned.ApplicationId,
                VersionCode = unsigned.VersionCode,
                PlaySignerLineageSha256 = unsigned.PlaySignerLineageSha256,
                Artifacts = unsigned.Artifacts,
                MrXEd25519PublicKey = unsigned.MrXEd25519PublicKey,
                Signature = PublicKeyAuth.SignDetached(signingBytes, mrX.PrivateKey)
            };
            var encoded = ProductionAndroidTransparencyManifestCodec.Encode(manifest);
            var buildId = SHA256.HashData(encoded);
            var mrXHash = SHA256.HashData(mrX.PublicKey);

            var verified = await ProductionAndroidCodeTransparencyVerifier.VerifyRuntimeAsync(
                encoded, mrXHash, "network.xpoint.deep", 15,
                [signer], new Dictionary<string, string> { ["base"] = approved });
            Assert.Equal(buildId, verified);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ProductionAndroidCodeTransparencyVerifier.VerifyRuntimeAsync(
                    encoded, mrXHash, "network.xpoint.deep", 15,
                    [signer], new Dictionary<string, string> { ["base"] = substituted }));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SemanticDigestIgnoresOnlySigningMetadataAndTransparencyPayload()
    {
        var root = CreateRoot();
        try
        {
            var first = Path.Combine(root, "first.apk");
            var second = Path.Combine(root, "second.apk");
            CreateApk(first, [9, 8, 7], signature: [1], transparency: [2]);
            CreateApk(second, [9, 8, 7], signature: [3], transparency: [4]);
            Assert.Equal(
                await ProductionAndroidSemanticApkDigest.ComputeAsync(first),
                await ProductionAndroidSemanticApkDigest.ComputeAsync(second));

            using (var archive = ZipFile.Open(second, ZipArchiveMode.Update))
                Write(archive, "META-INF/runtime-resource.bin", [5]);
            Assert.NotEqual(
                await ProductionAndroidSemanticApkDigest.ComputeAsync(first),
                await ProductionAndroidSemanticApkDigest.ComputeAsync(second));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("META-INF/nested/CERT.RSA")]
    [InlineData("META-INF/nested/DEEP.SF")]
    [InlineData("META-INF/nested/DEEP.DSA")]
    [InlineData("META-INF/nested/DEEP.EC")]
    [InlineData("META-INF/nested/SIG-DEEP")]
    [InlineData("meta-inf/CERT.RSA")]
    [InlineData("META-INF/cert.RSA")]
    [InlineData("META-INF/sig-DEEP")]
    public async Task SemanticDigestRejectsNestedOrCaseAmbiguousSigningMetadata(
        string entryName)
    {
        var root = CreateRoot();
        try
        {
            var apk = Path.Combine(root, "ambiguous.apk");
            CreateApk(apk, [1]);
            using (var archive = ZipFile.Open(apk, ZipArchiveMode.Update))
                Write(archive, entryName, [7]);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ProductionAndroidSemanticApkDigest.ComputeAsync(apk));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SemanticDigestIgnoresCanonicalDirectSignerLeaves()
    {
        var root = CreateRoot();
        try
        {
            var first = Path.Combine(root, "first.apk");
            var second = Path.Combine(root, "second.apk");
            CreateApk(first, [1]);
            CreateApk(second, [1]);
            using (var archive = ZipFile.Open(first, ZipArchiveMode.Update))
            {
                Write(archive, "META-INF/DEEP.SF", [1]);
                Write(archive, "META-INF/DEEP.EC", [2]);
                Write(archive, "META-INF/SIG-DEEP", [3]);
            }
            using (var archive = ZipFile.Open(second, ZipArchiveMode.Update))
            {
                Write(archive, "META-INF/DEEP.SF", [4]);
                Write(archive, "META-INF/DEEP.EC", [5]);
                Write(archive, "META-INF/SIG-DEEP", [6]);
            }
            Assert.Equal(
                await ProductionAndroidSemanticApkDigest.ComputeAsync(first),
                await ProductionAndroidSemanticApkDigest.ComputeAsync(second));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ManifestRejectsDuplicateOrUnorderedSplitIdentities()
    {
        var value = new ProductionAndroidTransparencyManifest
        {
            ApplicationId = "network.xpoint.deep",
            VersionCode = 1,
            PlaySignerLineageSha256 = [Fill(1)],
            Artifacts = [new("feature", Fill(2)), new("base", Fill(3))],
            MrXEd25519PublicKey = Fill(4),
            Signature = new byte[64]
        };
        Assert.Throws<InvalidDataException>(() =>
            ProductionAndroidTransparencyManifestCodec.Encode(value));
        Assert.Throws<InvalidDataException>(() =>
            ProductionAndroidTransparencyManifestCodec.Encode(WithArtifacts(value,
                [new("base", Fill(2)), new("base", Fill(3))])));
    }

    [Fact]
    public async Task RuntimeAcceptsApprovedInstalledSubsetButBuildRequiresCompleteSet()
    {
        var root = CreateRoot();
        try
        {
            var baseApk = Path.Combine(root, "base.apk");
            var featureApk = Path.Combine(root, "feature.apk");
            CreateApk(baseApk, [1]);
            CreateApk(featureApk, [2]);
            var signed = CreateSignedManifest([
                new("base", await ProductionAndroidSemanticApkDigest.ComputeAsync(baseApk)),
                new("feature", await ProductionAndroidSemanticApkDigest.ComputeAsync(featureApk))]);
            var baseOnly = new Dictionary<string, string> { ["base"] = baseApk };

            await ProductionAndroidCodeTransparencyVerifier.VerifyRuntimeAsync(
                signed.Encoded, signed.MrXHash,
                "network.xpoint.deep", 15, [signed.Signer], baseOnly);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ProductionAndroidCodeTransparencyVerifier.VerifyBuildAsync(
                    signed.Encoded, signed.ManifestHash, signed.MrXHash,
                    "network.xpoint.deep", 15, [signed.Signer], baseOnly,
                    CancellationToken.None));
            await ProductionAndroidCodeTransparencyVerifier.VerifyBuildAsync(
                signed.Encoded, signed.ManifestHash, signed.MrXHash,
                "network.xpoint.deep", 15, [signed.Signer],
                new Dictionary<string, string>
                {
                    ["base"] = baseApk,
                    ["feature"] = featureApk
                });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task VerificationUsesOneOwnedCollectionSnapshotBeforeFirstAwait()
    {
        var root = CreateRoot();
        try
        {
            var apk = Path.Combine(root, "base.apk");
            CreateApk(apk, new byte[2 * 1024 * 1024]);
            var signed = CreateSignedManifest([
                new("base", await ProductionAndroidSemanticApkDigest.ComputeAsync(apk))]);
            var lineage = new EnumerationOnlyList<ReadOnlyMemory<byte>>([signed.Signer]);
            var artifacts = new EnumerationOnlyDictionary(
                new Dictionary<string, string> { ["base"] = apk });

            var verification = ProductionAndroidCodeTransparencyVerifier.VerifyRuntimeAsync(
                signed.Encoded, signed.MrXHash,
                "network.xpoint.deep", 15, lineage, artifacts);
            artifacts.ReplaceWith("feature", Path.Combine(root, "missing.apk"));
            signed.Signer[0] ^= 1;

            Assert.Equal(signed.ManifestHash, await verification);
            Assert.True(artifacts.EnumerationStarted);
            Assert.True(artifacts.MutatedDuringMoveNext);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task WrongManifestHashOrMrXPinIsRejected()
    {
        var root = CreateRoot();
        try
        {
            var apk = Path.Combine(root, "base.apk");
            CreateApk(apk, [1]);
            var signed = CreateSignedManifest([
                new("base", await ProductionAndroidSemanticApkDigest.ComputeAsync(apk))]);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ProductionAndroidCodeTransparencyVerifier.VerifyBuildAsync(
                    signed.Encoded, Fill(0x71), signed.MrXHash,
                    "network.xpoint.deep", 15, [signed.Signer],
                    new Dictionary<string, string> { ["base"] = apk }));
            await Assert.ThrowsAsync<InvalidDataException>(() => VerifyRuntime(
                signed, apk, mrXHash: Fill(0x72)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task WrongApplicationVersionOrSignerLineageIsRejected()
    {
        var root = CreateRoot();
        try
        {
            var apk = Path.Combine(root, "base.apk");
            CreateApk(apk, [1]);
            var signed = CreateSignedManifest([
                new("base", await ProductionAndroidSemanticApkDigest.ComputeAsync(apk))]);
            await Assert.ThrowsAsync<InvalidDataException>(() => VerifyRuntime(
                signed, apk, applicationId: "network.xpoint.other"));
            await Assert.ThrowsAsync<InvalidDataException>(() => VerifyRuntime(
                signed, apk, versionCode: 16));
            await Assert.ThrowsAsync<InvalidDataException>(() => VerifyRuntime(
                signed, apk, signer: Fill(0x73)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CorruptedMrXSignatureIsRejectedEvenWhenManifestHashMatches()
    {
        var root = CreateRoot();
        try
        {
            var apk = Path.Combine(root, "base.apk");
            CreateApk(apk, [1]);
            var signed = CreateSignedManifest([
                new("base", await ProductionAndroidSemanticApkDigest.ComputeAsync(apk))]);
            var corrupted = signed.Encoded.ToArray();
            corrupted[^1] ^= 1;
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ProductionAndroidCodeTransparencyVerifier.VerifyRuntimeAsync(
                    corrupted, signed.MrXHash,
                    "network.xpoint.deep", 15, [signed.Signer],
                    new Dictionary<string, string> { ["base"] = apk }));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task MissingBaseOrUnapprovedSplitIsRejected()
    {
        var root = CreateRoot();
        try
        {
            var apk = Path.Combine(root, "base.apk");
            CreateApk(apk, [1]);
            var signed = CreateSignedManifest([
                new("base", await ProductionAndroidSemanticApkDigest.ComputeAsync(apk))]);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ProductionAndroidCodeTransparencyVerifier.VerifyRuntimeAsync(
                    signed.Encoded, signed.MrXHash,
                    "network.xpoint.deep", 15, [signed.Signer],
                    new Dictionary<string, string> { ["feature"] = apk }));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ProductionAndroidCodeTransparencyVerifier.VerifyRuntimeAsync(
                    signed.Encoded, signed.MrXHash,
                    "network.xpoint.deep", 15, [signed.Signer],
                    new Dictionary<string, string>
                    {
                        ["base"] = apk,
                        ["feature"] = apk
                    }));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OversizedOrTrailingManifestIsRejectedBeforeIdentityReturn()
    {
        var root = CreateRoot();
        try
        {
            var apk = Path.Combine(root, "base.apk");
            CreateApk(apk, [1]);
            var oversized = new byte[
                ProductionAndroidTransparencyManifestCodec.MaximumEncodedBytes + 1];
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ProductionAndroidCodeTransparencyVerifier.VerifyRuntimeAsync(
                    oversized, Fill(2), "network.xpoint.deep", 15,
                    [Fill(3)], new Dictionary<string, string> { ["base"] = apk }));
            var signed = CreateSignedManifest([
                new("base", await ProductionAndroidSemanticApkDigest.ComputeAsync(apk))]);
            var trailing = signed.Encoded.Concat(new byte[] { 0 }).ToArray();
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ProductionAndroidCodeTransparencyVerifier.VerifyRuntimeAsync(
                    trailing, signed.MrXHash,
                    "network.xpoint.deep", 15, [signed.Signer],
                    new Dictionary<string, string> { ["base"] = apk }));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SemanticDigestRejectsDuplicateNamesAndCompressionBomb()
    {
        var root = CreateRoot();
        try
        {
            var duplicate = Path.Combine(root, "duplicate.apk");
            using (var archive = ZipFile.Open(duplicate, ZipArchiveMode.Create))
            {
                Write(archive, "classes.dex", [1]);
                Write(archive, "classes.dex", [2]);
            }
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ProductionAndroidSemanticApkDigest.ComputeAsync(duplicate));

            var bomb = Path.Combine(root, "bomb.apk");
            using (var archive = ZipFile.Open(bomb, ZipArchiveMode.Create))
                Write(archive, "assets/bomb.bin", new byte[8 * 1024 * 1024]);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ProductionAndroidSemanticApkDigest.ComputeAsync(bomb));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static ProductionAndroidTransparencyManifest WithArtifacts(
        ProductionAndroidTransparencyManifest value,
        IReadOnlyList<ProductionAndroidTransparencyArtifact> artifacts) => new()
    {
        ApplicationId = value.ApplicationId,
        VersionCode = value.VersionCode,
        PlaySignerLineageSha256 = value.PlaySignerLineageSha256,
        Artifacts = artifacts,
        MrXEd25519PublicKey = value.MrXEd25519PublicKey,
        Signature = value.Signature
    };

    private static SignedManifest CreateSignedManifest(
        IReadOnlyList<ProductionAndroidTransparencyArtifact> artifacts)
    {
        var signer = Fill(0x31);
        var mrX = PublicKeyAuth.GenerateKeyPair(Fill(0x61));
        var unsigned = new ProductionAndroidTransparencyManifest
        {
            ApplicationId = "network.xpoint.deep",
            VersionCode = 15,
            PlaySignerLineageSha256 = [signer],
            Artifacts = artifacts,
            MrXEd25519PublicKey = mrX.PublicKey,
            Signature = new byte[64]
        };
        var manifest = new ProductionAndroidTransparencyManifest
        {
            ApplicationId = unsigned.ApplicationId,
            VersionCode = unsigned.VersionCode,
            PlaySignerLineageSha256 = unsigned.PlaySignerLineageSha256,
            Artifacts = unsigned.Artifacts,
            MrXEd25519PublicKey = unsigned.MrXEd25519PublicKey,
            Signature = PublicKeyAuth.SignDetached(
                ProductionAndroidTransparencyManifestCodec.GetSigningBytes(unsigned),
                mrX.PrivateKey)
        };
        var encoded = ProductionAndroidTransparencyManifestCodec.Encode(manifest);
        return new SignedManifest(
            encoded, SHA256.HashData(encoded), SHA256.HashData(mrX.PublicKey), signer);
    }

    private static Task<byte[]> VerifyRuntime(
        SignedManifest signed,
        string apk,
        byte[]? mrXHash = null,
        string applicationId = "network.xpoint.deep",
        ulong versionCode = 15,
        byte[]? signer = null) =>
        ProductionAndroidCodeTransparencyVerifier.VerifyRuntimeAsync(
            signed.Encoded, mrXHash ?? signed.MrXHash, applicationId, versionCode,
            [signer ?? signed.Signer],
            new Dictionary<string, string> { ["base"] = apk });

    private static void CreateApk(
        string path, byte[] dex, byte[]? signature = null, byte[]? transparency = null)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(archive, "AndroidManifest.xml", [1, 2]);
        Write(archive, "classes.dex", dex);
        Write(archive, "META-INF/CERT.RSA", signature ?? [1]);
        Write(archive,
            "assets/" + ProductionAndroidCodeTransparencyVerifier.AssetPath,
            transparency ?? [1]);
    }

    private static void Write(ZipArchive archive, string name, byte[] bytes)
    {
        using var stream = archive.CreateEntry(name, CompressionLevel.SmallestSize).Open();
        stream.Write(bytes);
    }

    private static string CreateRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"deep-act1-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path); return path;
    }

    private static byte[] Fill(byte value) => Enumerable.Repeat(value, 32).ToArray();

    private sealed record SignedManifest(
        byte[] Encoded,
        byte[] ManifestHash,
        byte[] MrXHash,
        byte[] Signer);

    private sealed class EnumerationOnlyList<T>(IReadOnlyList<T> source) : IReadOnlyList<T>
    {
        public int Count => throw new InvalidOperationException("Count callback is forbidden.");
        public T this[int index] => throw new InvalidOperationException("Index callback is forbidden.");
        public IEnumerator<T> GetEnumerator() => source.ToArray().AsEnumerable().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private sealed class EnumerationOnlyDictionary(
        IReadOnlyDictionary<string, string> source) : IReadOnlyDictionary<string, string>
    {
        private Dictionary<string, string> values = new(source, StringComparer.Ordinal);
        public bool EnumerationStarted { get; private set; }
        public bool MutatedDuringMoveNext { get; private set; }
        public int Count => throw new InvalidOperationException("Count callback is forbidden.");
        public IEnumerable<string> Keys => throw new InvalidOperationException("Keys callback is forbidden.");
        public IEnumerable<string> Values => throw new InvalidOperationException("Values callback is forbidden.");
        public string this[string key] => throw new InvalidOperationException("Indexer callback is forbidden.");
        public bool ContainsKey(string key) =>
            throw new InvalidOperationException("ContainsKey callback is forbidden.");
        public bool TryGetValue(string key, out string value) =>
            throw new InvalidOperationException("TryGetValue callback is forbidden.");
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            EnumerationStarted = true;
            var snapshot = values.ToArray();
            return EnumerateSnapshot(snapshot).GetEnumerator();
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
        public void ReplaceWith(string key, string value) =>
            values = new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value };
        private IEnumerable<KeyValuePair<string, string>> EnumerateSnapshot(
            IReadOnlyList<KeyValuePair<string, string>> snapshot)
        {
            for (var index = 0; index < snapshot.Count; index++)
            {
                if (index == 0)
                {
                    MutatedDuringMoveNext = true;
                    ReplaceWith("feature", "move-next-mutation.apk");
                }
                yield return snapshot[index];
            }
        }
    }
}
