using Deep.Client.Maui.Services;
using System.Reflection;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class ProductionMailboxBuildTrustFloorTests
{
    [Fact]
    public void EmptyBuildMetadataMeansCredentialsUnavailable()
    {
        Assert.False(ProductionMailboxBuildTrustFloor.TryParse(
            new Dictionary<string, string?>(), out var anchor));
        Assert.Null(anchor);
    }

    [Fact]
    public void CompleteCanonicalBuildMetadataProducesExactAnchor()
    {
        var values = Values();

        Assert.True(ProductionMailboxBuildTrustFloor.TryParse(values, out var anchor));

        Assert.NotNull(anchor);
        Assert.Equal(2UL, anchor!.AuthorityGeneration);
        Assert.Equal(3UL, anchor.RevocationGeneration);
        Assert.Equal(4UL, anchor.TopologyGeneration);
        Assert.Equal(FillHex(32, 0x11), Convert.ToHexStringLower(
            anchor.MrXPublicKeySha256.Span));
    }

    [Fact]
    public void PartialUppercaseZeroOrNonCanonicalMetadataFailsClosed()
    {
        foreach (var values in new[]
        {
            new Dictionary<string, string?>
            {
                [ProductionMailboxBuildTrustFloor.MrXKey] = FillHex(32, 0x11)
            },
            Changed(ProductionMailboxBuildTrustFloor.AuthorityHashKey,
                FillHex(32, 0xab).ToUpperInvariant()),
            Changed(ProductionMailboxBuildTrustFloor.TopologyHashKey,
                new string('0', 64)),
            Changed(ProductionMailboxBuildTrustFloor.AuthorityGenerationKey, "02")
        })
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                ProductionMailboxBuildTrustFloor.TryParse(values, out _));
            Assert.Equal("production-credentials-unavailable", exception.Message);
        }
    }

    [Fact]
    public void AndroidBuildIdentityBindsPackageVersionAndPlayLineageWithoutBuildIdMetadata()
    {
        var values = AndroidValues();

        Assert.True(ProductionMailboxBuildTrustFloor.TryParseAndroidIdentity(
            values, out var identity));

        Assert.NotNull(identity);
        Assert.Equal("network.xpoint.deep", identity!.ApplicationId);
        Assert.Equal(15UL, identity.VersionCode);
        Assert.Equal(2, identity.SignerLineageSha256.Count);
        Assert.DoesNotContain(
            "DeepProductionAndroidBuildIdSha256",
            typeof(ProductionMailboxBuildTrustFloor).Assembly
                .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
                .Select(static attribute => attribute.Key));
    }

    [Fact]
    public void AndroidBuildIdentityRejectsPartialDuplicateOrUploadKeyMetadata()
    {
        var partial = new Dictionary<string, string?>
        {
            [ProductionMailboxBuildTrustFloor.AndroidApplicationIdKey] =
                "network.xpoint.deep"
        };
        var duplicate = AndroidValues();
        duplicate[ProductionMailboxBuildTrustFloor.AndroidSignerLineageKey] =
            FillHex(32, 0x81) + "|" + FillHex(32, 0x81);
        var wrongPackage = AndroidValues();
        wrongPackage[ProductionMailboxBuildTrustFloor.AndroidApplicationIdKey] =
            "network.xpoint.deep.upload";

        foreach (var values in new[] { partial, duplicate, wrongPackage })
            Assert.Equal("production-credentials-unavailable",
                Assert.Throws<InvalidOperationException>(() =>
                    ProductionMailboxBuildTrustFloor.TryParseAndroidIdentity(
                        values, out _)).Message);
    }

    [Fact]
    public void InstalledAndroidTupleRejectsWrongVersionOrLineage()
    {
        Assert.True(ProductionMailboxBuildTrustFloor.TryParseAndroidIdentity(
            AndroidValues(), out var identity));
        Assert.NotNull(identity);
        ReadOnlyMemory<byte>[] exact = [Fill(32, 0x81), Fill(32, 0x91)];

        ProductionMailboxBuildTrustFloor.VerifyInstalledAndroidTuple(
            identity!, "network.xpoint.deep", 15, exact);
        Assert.Throws<InvalidDataException>(() =>
            ProductionMailboxBuildTrustFloor.VerifyInstalledAndroidTuple(
                identity!, "network.xpoint.deep", 16, exact));
        Assert.Throws<InvalidDataException>(() =>
            ProductionMailboxBuildTrustFloor.VerifyInstalledAndroidTuple(
                identity!, "network.xpoint.deep", 15,
                [Fill(32, 0x81), Fill(32, 0x92)]));
        Assert.Throws<InvalidDataException>(() =>
            ProductionMailboxBuildTrustFloor.VerifyInstalledAndroidTuple(
                identity!, "network.xpoint.deep.copy", 15, exact));
    }

    private static Dictionary<string, string?> Changed(string key, string value)
    {
        var values = Values();
        values[key] = value;
        return values;
    }

    private static Dictionary<string, string?> Values() => new(StringComparer.Ordinal)
    {
        [ProductionMailboxBuildTrustFloor.MrXKey] = FillHex(32, 0x11),
        [ProductionMailboxBuildTrustFloor.NetworkKey] = FillHex(16, 0x22),
        [ProductionMailboxBuildTrustFloor.AuthorityGenerationKey] = "2",
        [ProductionMailboxBuildTrustFloor.AuthorityHashKey] = FillHex(32, 0x33),
        [ProductionMailboxBuildTrustFloor.RevocationGenerationKey] = "3",
        [ProductionMailboxBuildTrustFloor.RevocationHeadHashKey] = FillHex(32, 0x44),
        [ProductionMailboxBuildTrustFloor.RevocationSnapshotHashKey] = FillHex(32, 0x55),
        [ProductionMailboxBuildTrustFloor.TopologyGenerationKey] = "4",
        [ProductionMailboxBuildTrustFloor.TopologyHashKey] = FillHex(32, 0x66)
    };

    private static Dictionary<string, string?> AndroidValues() => new(StringComparer.Ordinal)
    {
        [ProductionMailboxBuildTrustFloor.AndroidApplicationIdKey] =
            "network.xpoint.deep",
        [ProductionMailboxBuildTrustFloor.AndroidVersionCodeKey] = "15",
        [ProductionMailboxBuildTrustFloor.AndroidSignerLineageKey] =
            FillHex(32, 0x81) + "|" + FillHex(32, 0x91)
    };

    private static string FillHex(int bytes, byte value) =>
        Convert.ToHexStringLower(Enumerable.Repeat(value, bytes).ToArray());

    private static byte[] Fill(int bytes, byte value) =>
        Enumerable.Repeat(value, bytes).ToArray();
}
