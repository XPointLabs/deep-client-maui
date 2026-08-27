using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deep.Client.Shared.Services;
using Deep.Protocol.DeepExtension.PrivacyRouting;
using Sodium;

namespace Deep.Client.Maui.Services;

public sealed class ProductionMailboxPrivacyRouteBootstrapTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public void Load_AcceptsValidPinnedCanonicalBootstrap()
    {
        var fixture = Fixture();

        var routes = Load(fixture);

        Assert.Equal(new Uri("https://privacy-a.example/"), routes.Primary.EntryOrigin);
        Assert.Equal(new Uri("https://privacy-b.example/"), routes.Fallback.EntryOrigin);
        Assert.Equal(PrivacyRoutingLimits.RouteHopCount, routes.Primary.Hops.Count);
        Assert.Equal(PrivacyRoutingLimits.RouteHopCount, routes.Fallback.Hops.Count);
        Assert.Equal(Bytes(0x10, 32), routes.Primary.Hops[0].RouterId);
        Assert.Equal(Bytes(0x40, 32), routes.Fallback.Hops[0].X25519PublicKey);
    }

    [Fact]
    public void Load_RejectsBadSignature()
    {
        var fixture = Fixture();
        fixture.Signature[0] ^= 0x80;

        Assert.Throws<InvalidDataException>(() => Load(fixture));
    }

    [Fact]
    public void Load_RejectsUnpinnedSigningKey()
    {
        var fixture = Fixture();
        fixture.Anchor = fixture.Anchor with { MrXPublicKeySha256 = Bytes(0x91, 32) };

        Assert.Throws<InvalidDataException>(() => Load(fixture));
    }

    [Fact]
    public void Load_RejectsDifferentNetwork()
    {
        var fixture = Fixture(networkId: Bytes(0x72, 16));

        Assert.Throws<InvalidDataException>(() => Load(fixture));
    }

    [Theory]
    [InlineData(301, 3_600)]
    [InlineData(-3_600, -301)]
    public void Load_RejectsWindowOutsideMaximumClockSkew(
        long notBeforeOffset,
        long expiresOffset)
    {
        var now = Now.ToUnixTimeSeconds();
        var fixture = Fixture(
            notBefore: now + notBeforeOffset,
            expires: now + expiresOffset);

        Assert.Throws<InvalidDataException>(() => Load(fixture));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_AcceptsWindowAtMaximumClockSkew(bool future)
    {
        var now = Now.ToUnixTimeSeconds();
        var fixture = future
            ? Fixture(notBefore: now + 300, expires: now + 3_600)
            : Fixture(notBefore: now - 3_600, expires: now - 300);

        _ = Load(fixture);
    }

    [Fact]
    public void Load_RejectsPrimaryFallbackOverlap()
    {
        var fixture = Fixture(fallbackRouterMarker: 0x10);

        Assert.Throws<InvalidDataException>(() => Load(fixture));
    }

    [Fact]
    public void Load_RejectsDuplicateHopInsideRoute()
    {
        var fixture = Fixture(duplicatePrimaryHop: true);

        Assert.Throws<InvalidDataException>(() => Load(fixture));
    }

    [Fact]
    public void Load_RejectsDuplicateJsonFieldEvenWhenSigned()
    {
        var fixture = Fixture();
        var json = Encoding.UTF8.GetString(fixture.Json);
        var needle = "\"networkId\":\"" + Convert.ToHexStringLower(fixture.Anchor.NetworkId.Span) + "\",";
        fixture.Resign(Encoding.UTF8.GetBytes(json.Replace(needle, needle + needle,
            StringComparison.Ordinal)));

        Assert.Throws<InvalidDataException>(() => Load(fixture));
    }

    [Fact]
    public void Load_RejectsUnknownJsonFieldEvenWhenSigned()
    {
        var fixture = Fixture();
        var json = Encoding.UTF8.GetString(fixture.Json);
        fixture.Resign(Encoding.UTF8.GetBytes(json.Insert(1, "\"unknown\":1,")));

        Assert.Throws<InvalidDataException>(() => Load(fixture));
    }

    [Fact]
    public void Load_RejectsNonCanonicalJsonEvenWhenSigned()
    {
        var fixture = Fixture();
        fixture.Resign(Encoding.UTF8.GetBytes(" " + Encoding.UTF8.GetString(fixture.Json)));

        Assert.Throws<InvalidDataException>(() => Load(fixture));
    }

    [Fact]
    public void Load_RejectsMalformedJsonEvenWhenSigned()
    {
        var fixture = Fixture();
        fixture.Resign("{\"schemaVersion\":"u8.ToArray());

        Assert.Throws<InvalidDataException>(() => Load(fixture));
    }

    [Fact]
    public void Load_RejectsOversizeJsonBeforeParsing()
    {
        var fixture = Fixture();
        fixture.Json = Enumerable.Repeat((byte)'x',
            ProductionMailboxPrivacyRouteBootstrap.MaximumJsonBytes + 1).ToArray();
        fixture.Signature = PublicKeyAuth.SignDetached(fixture.Json, fixture.KeyPair.PrivateKey);

        Assert.Throws<InvalidDataException>(() => Load(fixture));
    }

    [Theory]
    [InlineData("http://privacy-a.example/")]
    [InlineData("https://user@privacy-a.example/")]
    [InlineData("https://privacy-a.example/path")]
    [InlineData("https://privacy-a.example/?query=1")]
    public void Load_RejectsUncleanEntryOrigin(string origin)
    {
        var fixture = Fixture(primaryOrigin: origin);

        Assert.Throws<InvalidDataException>(() => Load(fixture));
    }

    private static MailboxPrivacyRouteSet Load(FixtureData fixture)
    {
        using var json = new MemoryStream(fixture.Json, writable: false);
        using var signature = new MemoryStream(fixture.Signature, writable: false);
        using var publicKey = new MemoryStream(fixture.KeyPair.PublicKey, writable: false);
        return ProductionMailboxPrivacyRouteBootstrap.Load(
            json,
            signature,
            publicKey,
            fixture.Anchor,
            new FixedTimeProvider(Now));
    }

    private static FixtureData Fixture(
        byte[]? networkId = null,
        long? notBefore = null,
        long? expires = null,
        byte fallbackRouterMarker = 0x30,
        bool duplicatePrimaryHop = false,
        string primaryOrigin = "https://privacy-a.example/")
    {
        var pair = PublicKeyAuth.GenerateKeyPair(Bytes(0x61, 32));
        var anchorNetwork = Bytes(0x71, 16);
        var anchor = new ProductionMailboxTrustAnchor(
            SHA256.HashData(pair.PublicKey),
            anchorNetwork,
            1,
            Bytes(0x81, 32),
            1,
            Bytes(0x82, 32),
            Bytes(0x83, 32),
            1,
            Bytes(0x84, 32));
        var now = Now.ToUnixTimeSeconds();
        var primary = Route(
            primaryOrigin,
            duplicatePrimaryHop ? new byte[] { 0x10, 0x10, 0x12 } : [0x10, 0x11, 0x12],
            [0x20, 0x21, 0x22]);
        var fallback = Route(
            "https://privacy-b.example/",
            [fallbackRouterMarker, 0x31, 0x32],
            [0x40, 0x41, 0x42]);
        var json = Encode(
            networkId ?? anchorNetwork,
            notBefore ?? now - 3_600,
            expires ?? now + 3_600,
            primary,
            fallback);
        return new FixtureData(
            json,
            PublicKeyAuth.SignDetached(json, pair.PrivateKey),
            pair,
            anchor);
    }

    private static RouteData Route(string origin, byte[] routers, byte[] keys) =>
        new(origin, routers.Zip(keys, (router, key) =>
            new HopData(Bytes(router, 32), Bytes(key, 32))).ToArray());

    private static byte[] Encode(
        byte[] networkId,
        long notBefore,
        long expires,
        RouteData primary,
        RouteData fallback)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteBoolean("developmentOnly", false);
            writer.WriteString("networkId", Convert.ToHexStringLower(networkId));
            writer.WriteNumber("notBeforeUnixSeconds", notBefore);
            writer.WriteNumber("expiresUnixSeconds", expires);
            WriteRoute(writer, "primary", primary);
            WriteRoute(writer, "fallback", fallback);
            writer.WriteEndObject();
        }
        return output.ToArray();
    }

    private static void WriteRoute(Utf8JsonWriter writer, string name, RouteData route)
    {
        writer.WriteStartObject(name);
        writer.WriteString("entryOrigin", route.EntryOrigin);
        writer.WriteStartArray("hops");
        foreach (var hop in route.Hops)
        {
            writer.WriteStartObject();
            writer.WriteString("routerId", Convert.ToHexStringLower(hop.RouterId));
            writer.WriteString("x25519PublicKey", Convert.ToHexStringLower(hop.X25519PublicKey));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static byte[] Bytes(byte marker, int count) =>
        Enumerable.Repeat(marker, count).ToArray();

    private sealed record HopData(byte[] RouterId, byte[] X25519PublicKey);

    private sealed record RouteData(string EntryOrigin, IReadOnlyList<HopData> Hops);

    private sealed class FixtureData(
        byte[] json,
        byte[] signature,
        KeyPair keyPair,
        ProductionMailboxTrustAnchor anchor)
    {
        internal byte[] Json { get; set; } = json;
        internal byte[] Signature { get; set; } = signature;
        internal KeyPair KeyPair { get; } = keyPair;
        internal ProductionMailboxTrustAnchor Anchor { get; set; } = anchor;

        internal void Resign(byte[] json)
        {
            Json = json;
            Signature = PublicKeyAuth.SignDetached(json, KeyPair.PrivateKey);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
