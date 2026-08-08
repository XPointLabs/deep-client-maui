using System.Security.Cryptography;
using System.Text.Json;
using Deep.Client.Maui.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class ProductionMailboxRegistryJsonCodecTests
{
    [Fact]
    public void ExactLocalOwnerResponse_ParsesFrozenBinaryClosure()
    {
        var encoded = Response();

        var parsed = ProductionMailboxRegistryJsonCodec.ParseLocalOwnerBundle(encoded);

        Assert.Equal(2, parsed.Selections.Count);
        Assert.Equal(2, parsed.Grants.Count);
        Assert.Equal(9UL, parsed.Selections[0].Epoch);
        Assert.Equal(10UL, parsed.Selections[1].Epoch);
        Assert.Equal(272, parsed.Grants[0].CanonicalGrant.Length);
        Assert.Equal(304, parsed.CanonicalRouteCertificate.Length);
        Assert.Equal(400, parsed.CanonicalRouteAdvertisement.Length);
        Assert.True(parsed.CanonicalSelectionSuccessor.IsEmpty);
    }

    [Fact]
    public void UnknownField_IsRejected()
    {
        using var document = JsonDocument.Parse(Response());
        var dictionary = document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone());
        dictionary["legacy"] = JsonDocument.Parse("true").RootElement.Clone();
        var encoded = JsonSerializer.SerializeToUtf8Bytes(dictionary);

        Assert.Throws<InvalidDataException>(() =>
            ProductionMailboxRegistryJsonCodec.ParseLocalOwnerBundle(encoded));
    }

    [Fact]
    public void ArtifactHashTamper_IsRejected()
    {
        var encoded = Response(tamperAuthorityHash: true);

        Assert.Throws<InvalidDataException>(() =>
            ProductionMailboxRegistryJsonCodec.ParseLocalOwnerBundle(encoded));
    }

    [Fact]
    public void MissingRouteOrNonNullSelectionSuccessor_IsRejected()
    {
        using var document = JsonDocument.Parse(Response());
        var values = document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone());
        var missingRoute = values
            .Where(pair => pair.Key != "routeCertificate")
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Throws<InvalidDataException>(() =>
            ProductionMailboxRegistryJsonCodec.ParseLocalOwnerBundle(
                JsonSerializer.SerializeToUtf8Bytes(missingRoute)));

        values["selectionSuccessor"] = values["routeCertificate"];
        Assert.Throws<InvalidDataException>(() =>
            ProductionMailboxRegistryJsonCodec.ParseLocalOwnerBundle(
                JsonSerializer.SerializeToUtf8Bytes(values)));
    }

    private static byte[] Response(bool tamperAuthorityHash = false)
    {
        var authority = Bytes(0x11, 320);
        var revocation = Bytes(0x21, 256);
        var topology = Bytes(0x31, 640);
        var currentSelection = Bytes(0x41, 512);
        var nextSelection = Bytes(0x51, 512);
        var currentGrant = Bytes(0x61, 272);
        var nextGrant = Bytes(0x71, 272);
        var routeCertificate = Bytes(0x82, 304);
        var routeAdvertisement = Bytes(0x83, 400);
        var authorityHash = SHA256.HashData(authority);
        if (tamperAuthorityHash) authorityHash[0] ^= 1;
        var payload = new
        {
            schema = "production-mailbox-credential-bundle.v1",
            idempotencyKey = B64(Bytes(0x81, 32)),
            holderEd25519PublicKey = B64(Bytes(0x91, 32)),
            mailboxOwnerEd25519PublicKey = B64(Bytes(0xa1, 32)),
            intent = 1,
            blindedMailboxId = B64(Bytes(0xb1, 32)),
            blindedPlacementId = B64(Bytes(0xc1, 32)),
            selectionInputCommitment = B64(Bytes(0xd1, 32)),
            authority = Artifact(
                "authority.pma1",
                "application/vnd.deep.production-mailbox-authority",
                authority,
                authorityHash),
            revocation = Artifact(
                "revocations.pmr1",
                "application/vnd.deep.production-mailbox-revocation-snapshot",
                revocation),
            topology = Artifact(
                "topology.pmt1",
                "application/vnd.deep.production-mailbox-topology",
                topology),
            selections = new[]
            {
                Selection(9, 70, "selection-current.pms1", currentSelection, 0xe1),
                Selection(10, 71, "selection-next.pms1", nextSelection, 0xf1)
            },
            grants = new[]
            {
                Grant(9, 70, "retrieve-current.mcg2", currentGrant),
                Grant(10, 71, "retrieve-next.mcg2", nextGrant)
            },
            limits = new
            {
                storedBytes = 67_108_864UL,
                storedMessages = 10_000U,
                retentionSeconds = 1_209_600U,
                requestsPerHour = 1_000U,
                entitled = false
            },
            issuedAtUnixSeconds = 2_100_000_000UL,
            expiresAtUnixSeconds = 2_100_001_000UL,
            routeCertificate = CanonicalEnvelope(
                "route-certificate.prc1",
                "application/vnd.deep.production-mailbox-route-certificate",
                routeCertificate),
            routeAdvertisement = CanonicalEnvelope(
                "route-advertisement.pra1",
                "application/vnd.deep.production-mailbox-route-advertisement",
                routeAdvertisement),
            selectionSuccessor = (object?)null
        };
        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    private static object Artifact(
        string file,
        string media,
        byte[] canonical,
        byte[]? forcedHash = null)
    {
        var hash = Convert.ToHexStringLower(forcedHash ?? SHA256.HashData(canonical));
        return new
        {
            fileName = file,
            mediaType = media,
            sha256 = hash,
            eTag = $"\"{hash}\"",
            contentPath = $"/api/production-mailbox/artifacts/{hash}/{file}",
            canonicalBase64Url = B64(canonical)
        };
    }

    private static object CanonicalEnvelope(
        string file,
        string media,
        byte[] canonical) => new
        {
            fileName = file,
            mediaType = media,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(canonical)),
            canonicalBase64Url = B64(canonical)
        };

    private static object Selection(
        ulong epoch,
        ulong generation,
        string file,
        byte[] canonical,
        byte seed) => new
        {
            epoch,
            generation,
            fileName = file,
            mediaType = "application/vnd.deep.production-mailbox-selection",
            sha256 = Convert.ToHexStringLower(SHA256.HashData(canonical)),
            canonicalBase64Url = B64(canonical),
            replicas = new[]
            {
                Replica(seed, $"https://node-{epoch}-1.example.net/"),
                Replica(unchecked((byte)(seed + 1)),
                    $"https://node-{epoch}-2.example.net/")
            }
        };

    private static object Replica(byte seed, string endpoint) => new
    {
        replicaId = B64(Bytes(seed, 32)),
        httpsEndpoint = endpoint,
        currentSpkiSha256 = Convert.ToHexStringLower(Bytes((byte)(seed + 1), 32)),
        nextSpkiSha256 = Convert.ToHexStringLower(Bytes((byte)(seed + 2), 32))
    };

    private static object Grant(
        ulong epoch,
        ulong generation,
        string file,
        byte[] canonical) => new
        {
            domain = "retrieve",
            epoch,
            generation,
            fileName = file,
            mediaType = "application/vnd.deep.mailbox-authenticated-grant",
            sha256 = Convert.ToHexStringLower(SHA256.HashData(canonical)),
            canonicalBase64Url = B64(canonical)
        };

    private static string B64(byte[] value) => Convert.ToBase64String(value)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Bytes(byte seed, int length) => Enumerable.Range(0, length)
        .Select(index => unchecked((byte)(seed + index))).ToArray();
}
