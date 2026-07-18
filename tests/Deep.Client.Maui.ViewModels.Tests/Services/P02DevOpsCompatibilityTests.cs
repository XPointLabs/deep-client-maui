using Deep.Client.Maui.Core.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class P02DevOpsCompatibilityTests
{
    [Fact]
    public async Task ReviewedPublicMetadata_VerifiesAgainstExactP02Contract_WhenSupplied()
    {
        var artifactRoot = Environment.GetEnvironmentVariable("DEEP_P02_ARTIFACT_ROOT");
        if (string.IsNullOrWhiteSpace(artifactRoot))
        {
            return;
        }

        artifactRoot = Path.GetFullPath(artifactRoot);
        var expectedHashes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["1.root.json"] = "6127e2111c2348961630ab2874b5620bc0dbb2f8339d85fb9171b4130b961ad7",
            ["2.root.json"] = "6e98c82f01626e8aab1e373a1385578c8c5c4f9342b801cea8989f1b9f608daa",
            ["timestamp.json"] = "59d28b6551521f45ef17d7fa65adae64c255f4c289c69f8dbb99f2bc91f6f7f5",
            ["snapshot.json"] = "ef862e7fa3b0735c837c678a9a3d900b91fa749ed20e951188c24fb4e0a1de16",
            ["targets.json"] = "68f40c2db0b597c52f7d0320f59570d6a654c72ae99317f55813a7707a728457",
            ["android-release.json"] = "2f315df82d72990f6ebdd2f197baeaf30c6c8bf582a10c820aeef840fb937858"
        };
        var bytes = expectedHashes.ToDictionary(
            pair => pair.Key,
            pair =>
            {
                var value = File.ReadAllBytes(Path.Combine(artifactRoot, pair.Key));
                Assert.Equal(pair.Value, Sha256(value));
                return value;
            },
            StringComparer.Ordinal);

        var initial = PortableUpdateMetadataVerifier.CreateInitialState(bytes["1.root.json"]);
        var verified = await new PortableUpdateMetadataVerifier().VerifyAsync(
            new UpdateMetadataBundle(
                bytes["1.root.json"],
                new ReadOnlyMemory<byte>[] { bytes["2.root.json"] },
                bytes["timestamp.json"],
                bytes["snapshot.json"],
                bytes["targets.json"],
                bytes["android-release.json"]),
            initial,
            new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "android/network.xpoint.deep-2.0.1-i01b.apk",
            null,
            CancellationToken.None);

        Assert.Equal(2, verified.RootVersion);
        Assert.Equal("2.0.1-i01b", verified.AndroidTarget.VersionName);
        Assert.Equal("9dc1502392ce2c1a86441df6308f2db54410eae8", verified.AndroidTarget.SourceCommit);
        Assert.Equal(
            "67c86fcab94cd7bf00185fef40bdf39e213dd82c260e0c8958e6ffcdbe438ed0",
            verified.AndroidTarget.PackageSignerSha256);
    }

    private static string Sha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(value))
            .ToLowerInvariant();
}
