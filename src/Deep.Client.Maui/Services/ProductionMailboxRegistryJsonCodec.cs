using System.Security.Cryptography;
using System.Text.Json;
using Deep.Client.Shared.Services;
using Deep.Protocol.DeepExtension.MailboxAuthority;
using Deep.Protocol.DeepExtension.MailboxCapabilities;

namespace Deep.Client.Maui.Services;

/// <summary>Strict, no-extension JSON decoder for the frozen Registry LocalOwner response.</summary>
internal static class ProductionMailboxRegistryJsonCodec
{
    internal const int MaximumCredentialResponseBytes = 256 * 1024;
    private const string BundleSchema = "production-mailbox-credential-bundle.v1";
    private const string AuthorityMedia =
        "application/vnd.deep.production-mailbox-authority";
    private const string RevocationMedia =
        "application/vnd.deep.production-mailbox-revocation-snapshot";
    private const string TopologyMedia =
        "application/vnd.deep.production-mailbox-topology";
    private const string SelectionMedia =
        "application/vnd.deep.production-mailbox-selection";
    private const string GrantMedia =
        "application/vnd.deep.mailbox-authenticated-grant";
    private const string RouteCertificateMedia =
        "application/vnd.deep.production-mailbox-route-certificate";
    private const string RouteAdvertisementMedia =
        "application/vnd.deep.production-mailbox-route-advertisement";
    private const int RouteCertificateLength = 304;
    private const int RouteAdvertisementLength = 400;

    public static ProductionMailboxRegistryChallenge ParseChallenge(
        ReadOnlySpan<byte> utf8)
    {
        using var document = ParseDocument(utf8, "challenge");
        var root = document.RootElement;
        Exact(root, "challengeId", "challenge", "leadingZeroBits",
            "expiresAtUnixSeconds", "authority", "revocation", "topology");
        var difficulty = Number(root, "leadingZeroBits");
        Require(difficulty is >= 8 and <= 22,
            "Registry proof-of-work difficulty is outside the supported range.");
        var authority = Artifact(
            root.GetProperty("authority"), "authority.pma1", AuthorityMedia);
        var revocation = Artifact(
            root.GetProperty("revocation"), "revocations.pmr1", RevocationMedia);
        var topology = Artifact(
            root.GetProperty("topology"), "topology.pmt1", TopologyMedia);
        var expires = Number(root, "expiresAtUnixSeconds");
        Require(expires > 0, "Registry challenge expiry is invalid.");
        return new ProductionMailboxRegistryChallenge(
            B64(root, "challengeId", 16, nonzero: true),
            B64(root, "challenge", 32, nonzero: true),
            checked((int)difficulty),
            expires,
            authority.ToRegistryArtifact(),
            revocation.ToRegistryArtifact(),
            topology.ToRegistryArtifact());
    }

    public static ProductionMailboxRegistryRouteEnrollment ParseRouteEnrollment(
        ReadOnlySpan<byte> utf8)
    {
        using var document = ParseDocument(utf8, "route-enrollment");
        var root = document.RootElement;
        Exact(root, "schema", "enrollmentHandle", "idempotencyKey",
            "holderEd25519PublicKey", "mailboxOwnerEd25519PublicKey",
            "blindedMailboxId", "blindedPlacementId", "selectionInputCommitment",
            "authority", "revocation", "topology", "routeCertificate",
            "issuedAtUnixSeconds", "expiresAtUnixSeconds");
        Require(String(root, "schema") == "production-mailbox-route-enrollment.v1",
            "Registry route-enrollment schema is unsupported.");
        var authority = Artifact(
            root.GetProperty("authority"), "authority.pma1", AuthorityMedia);
        var revocation = Artifact(
            root.GetProperty("revocation"), "revocations.pmr1", RevocationMedia);
        var topology = Artifact(
            root.GetProperty("topology"), "topology.pmt1", TopologyMedia);
        var certificate = CanonicalEnvelope(
            root.GetProperty("routeCertificate"),
            "route-certificate.prc1", RouteCertificateMedia, RouteCertificateLength);
        var issued = Number(root, "issuedAtUnixSeconds");
        var expires = Number(root, "expiresAtUnixSeconds");
        Require(issued > 0 && issued < expires,
            "Registry route-enrollment validity window is invalid.");
        return new ProductionMailboxRegistryRouteEnrollment(
            B64(root, "enrollmentHandle", 32, nonzero: true),
            B64(root, "idempotencyKey", 32, nonzero: true),
            B64(root, "holderEd25519PublicKey", 32, nonzero: true),
            B64(root, "mailboxOwnerEd25519PublicKey", 32, nonzero: true),
            B64(root, "blindedMailboxId", 32, nonzero: true),
            B64(root, "blindedPlacementId", 32, nonzero: true),
            B64(root, "selectionInputCommitment", 32, nonzero: true),
            new ProductionMailboxControlPlaneArtifacts(
                authority.Canonical, revocation.Canonical, topology.Canonical,
                ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty),
            certificate.Canonical,
            certificate.Sha256,
            issued,
            expires);
    }

    public static ProductionMailboxLocalOwnerBundle ParseLocalOwnerBundle(
        ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length is 0 or > MaximumCredentialResponseBytes)
            throw Invalid("Registry credential response length is invalid.");
        try
        {
            using var document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 12
            });
            var root = document.RootElement;
            Exact(root,
                "schema", "idempotencyKey", "holderEd25519PublicKey",
                "mailboxOwnerEd25519PublicKey", "intent", "blindedMailboxId",
                "blindedPlacementId", "selectionInputCommitment", "authority",
                "revocation", "topology", "selections", "grants", "limits",
                "issuedAtUnixSeconds", "expiresAtUnixSeconds", "routeCertificate",
                "routeAdvertisement", "selectionSuccessor");
            Require(String(root, "schema") == BundleSchema,
                "Registry credential schema is unsupported.");
            Require(Number(root, "intent") ==
                    (ulong)ProductionMailboxIssuanceIntent.LocalOwner,
                "Registry response is not LocalOwner.");
            var authority = Artifact(
                root.GetProperty("authority"), "authority.pma1", AuthorityMedia);
            var revocation = Artifact(
                root.GetProperty("revocation"), "revocations.pmr1", RevocationMedia);
            var topology = Artifact(
                root.GetProperty("topology"), "topology.pmt1", TopologyMedia);
            var selectionsNode = root.GetProperty("selections");
            Require(selectionsNode.ValueKind == JsonValueKind.Array &&
                    selectionsNode.GetArrayLength() == 2,
                "Registry response requires exact current/next PMS1.");
            var selections = selectionsNode.EnumerateArray()
                .Select((value, index) => Selection(value, index)).ToArray();
            Require(selections[1].Epoch == checked(selections[0].Epoch + 1),
                "Registry PMS1 epochs are not exact E/E+1.");
            var grantsNode = root.GetProperty("grants");
            Require(grantsNode.ValueKind == JsonValueKind.Array &&
                    grantsNode.GetArrayLength() == 2,
                "Registry LocalOwner requires exact current/next MCG2.");
            var grants = grantsNode.EnumerateArray()
                .Select((value, index) => Grant(
                    value, index, MailboxCapabilityDomain.Retrieve)).ToArray();
            Require(grants[0].Epoch == selections[0].Epoch &&
                    grants[0].Generation == selections[0].Generation &&
                    grants[1].Epoch == selections[1].Epoch &&
                    grants[1].Generation == selections[1].Generation,
                "Registry MCG2 and PMS1 epochs differ.");
            Limits(root.GetProperty("limits"));
            var routeCertificate = CanonicalEnvelope(
                root.GetProperty("routeCertificate"),
                "route-certificate.prc1",
                RouteCertificateMedia,
                RouteCertificateLength);
            var routeAdvertisement = CanonicalEnvelope(
                root.GetProperty("routeAdvertisement"),
                "route-advertisement.pra1",
                RouteAdvertisementMedia,
                RouteAdvertisementLength);
            Require(root.GetProperty("selectionSuccessor").ValueKind == JsonValueKind.Null,
                "Registry selection-successor activation is not supported yet.");
            var issued = Number(root, "issuedAtUnixSeconds");
            var expires = Number(root, "expiresAtUnixSeconds");
            Require(issued > 0 && issued < expires,
                "Registry credential validity window is invalid.");
            return new ProductionMailboxLocalOwnerBundle(
                B64(root, "holderEd25519PublicKey", 32, nonzero: true),
                B64(root, "mailboxOwnerEd25519PublicKey", 32, nonzero: true),
                B64(root, "idempotencyKey", 32, nonzero: true),
                B64(root, "blindedMailboxId", 32, nonzero: true),
                B64(root, "blindedPlacementId", 32, nonzero: true),
                B64(root, "selectionInputCommitment", 32, nonzero: true),
                new ProductionMailboxControlPlaneArtifacts(
                    authority.Canonical,
                    revocation.Canonical,
                    topology.Canonical,
                    selections[0].CanonicalSelection,
                    selections[1].CanonicalSelection),
                selections,
                grants,
                routeCertificate.Canonical,
                routeCertificate.Sha256,
                routeAdvertisement.Canonical,
                routeAdvertisement.Sha256,
                ReadOnlyMemory<byte>.Empty,
                ReadOnlyMemory<byte>.Empty,
                issued,
                expires);
        }
        catch (JsonException exception)
        {
            throw Invalid("Registry credential response is malformed JSON.", exception);
        }
        catch (OverflowException exception)
        {
            throw Invalid("Registry credential response numeric value overflowed.", exception);
        }
    }

    public static ProductionMailboxPeerDepositBundle ParsePeerDepositBundle(
        ReadOnlySpan<byte> utf8,
        ReadOnlyMemory<byte> recipientEd25519PublicKey,
        ReadOnlyMemory<byte> canonicalRouteAdvertisement)
    {
        var recipient = ExternalBinary(
            recipientEd25519PublicKey, 32, "recipient Session key");
        var advertisement = ExternalBinary(
            canonicalRouteAdvertisement, RouteAdvertisementLength,
            "route advertisement");
        try
        {
            using var document = ParseDocument(utf8, "credential");
            var root = document.RootElement;
            Exact(root,
                "schema", "idempotencyKey", "holderEd25519PublicKey",
                "mailboxOwnerEd25519PublicKey", "intent", "blindedMailboxId",
                "blindedPlacementId", "selectionInputCommitment", "authority",
                "revocation", "topology", "selections", "grants", "limits",
                "issuedAtUnixSeconds", "expiresAtUnixSeconds", "routeCertificate",
                "routeAdvertisement", "selectionSuccessor");
            Require(String(root, "schema") == BundleSchema,
                "Registry credential schema is unsupported.");
            Require(Number(root, "intent") ==
                    (ulong)ProductionMailboxIssuanceIntent.PeerDeposit,
                "Registry response is not PeerDeposit.");
            var authority = Artifact(
                root.GetProperty("authority"), "authority.pma1", AuthorityMedia);
            var revocation = Artifact(
                root.GetProperty("revocation"), "revocations.pmr1", RevocationMedia);
            var topology = Artifact(
                root.GetProperty("topology"), "topology.pmt1", TopologyMedia);
            var selectionsNode = root.GetProperty("selections");
            Require(selectionsNode.ValueKind == JsonValueKind.Array &&
                    selectionsNode.GetArrayLength() == 2,
                "Registry response requires exact current/next PMS1.");
            var selections = selectionsNode.EnumerateArray()
                .Select((value, index) => Selection(value, index)).ToArray();
            Require(selections[1].Epoch == checked(selections[0].Epoch + 1),
                "Registry PMS1 epochs are not exact E/E+1.");
            var grantsNode = root.GetProperty("grants");
            Require(grantsNode.ValueKind == JsonValueKind.Array &&
                    grantsNode.GetArrayLength() == 2,
                "Registry PeerDeposit requires exact current/next MCG2.");
            var grants = grantsNode.EnumerateArray()
                .Select((value, index) => Grant(
                    value, index, MailboxCapabilityDomain.Deposit)).ToArray();
            Require(grants[0].Epoch == selections[0].Epoch &&
                    grants[0].Generation == selections[0].Generation &&
                    grants[1].Epoch == selections[1].Epoch &&
                    grants[1].Generation == selections[1].Generation,
                "Registry MCG2 and PMS1 epochs differ.");
            Limits(root.GetProperty("limits"));
            Require(root.GetProperty("routeCertificate").ValueKind == JsonValueKind.Null &&
                    root.GetProperty("routeAdvertisement").ValueKind == JsonValueKind.Null &&
                    root.GetProperty("selectionSuccessor").ValueKind == JsonValueKind.Null,
                "Registry PeerDeposit returned unsupported local-owner material.");
            var issued = Number(root, "issuedAtUnixSeconds");
            var expires = Number(root, "expiresAtUnixSeconds");
            Require(issued > 0 && issued < expires,
                "Registry credential validity window is invalid.");
            return new ProductionMailboxPeerDepositBundle(
                B64(root, "holderEd25519PublicKey", 32, nonzero: true),
                recipient,
                B64(root, "mailboxOwnerEd25519PublicKey", 32, nonzero: true),
                B64(root, "idempotencyKey", 32, nonzero: true),
                B64(root, "blindedMailboxId", 32, nonzero: true),
                B64(root, "blindedPlacementId", 32, nonzero: true),
                B64(root, "selectionInputCommitment", 32, nonzero: true),
                new ProductionMailboxControlPlaneArtifacts(
                    authority.Canonical, revocation.Canonical, topology.Canonical,
                    selections[0].CanonicalSelection,
                    selections[1].CanonicalSelection),
                selections,
                grants,
                advertisement,
                SHA256.HashData(advertisement),
                issued,
                expires);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(recipient);
            CryptographicOperations.ZeroMemory(advertisement);
            throw;
        }
    }

    private static ArtifactValue Artifact(
        JsonElement value,
        string expectedFile,
        string expectedMedia)
    {
        Exact(value, "fileName", "mediaType", "sha256", "eTag", "contentPath",
            "canonicalBase64Url");
        var hash = LowerHex(value, "sha256", 32);
        var canonical = B64(value, "canonicalBase64Url", null, nonzero: true);
        var hex = Convert.ToHexStringLower(hash);
        Require(String(value, "fileName") == expectedFile &&
                String(value, "mediaType") == expectedMedia &&
                String(value, "eTag") == $"\"{hex}\"" &&
                String(value, "contentPath") ==
                    $"/api/production-mailbox/artifacts/{hex}/{expectedFile}" &&
                CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(canonical), hash),
            "Registry artifact reference is not canonical or content-addressed.");
        return new ArtifactValue(
            expectedFile, expectedMedia, hash, String(value, "eTag"),
            String(value, "contentPath"), canonical);
    }

    private static ProductionMailboxSelectionBinding Selection(
        JsonElement value,
        int index)
    {
        Exact(value, "epoch", "generation", "fileName", "mediaType", "sha256",
            "canonicalBase64Url", "replicas");
        var canonical = B64(value, "canonicalBase64Url", null, nonzero: true);
        var hash = LowerHex(value, "sha256", 32);
        var replicasNode = value.GetProperty("replicas");
        Require(replicasNode.ValueKind == JsonValueKind.Array &&
                replicasNode.GetArrayLength() == 2,
            "Registry PMS1 requires exactly two ordered replicas.");
        var replicas = replicasNode.EnumerateArray().Select(Replica).ToArray();
        Require(String(value, "fileName") ==
                    (index == 0 ? "selection-current.pms1" : "selection-next.pms1") &&
                String(value, "mediaType") == SelectionMedia &&
                CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(canonical), hash),
            "Registry PMS1 envelope is invalid.");
        return new ProductionMailboxSelectionBinding(
            Number(value, "epoch"), Number(value, "generation"), canonical, replicas);
    }

    private static ProductionMailboxReplicaBinding Replica(JsonElement value)
    {
        Exact(value, "replicaId", "httpsEndpoint", "currentSpkiSha256",
            "nextSpkiSha256");
        var endpointText = String(value, "httpsEndpoint");
        Require(Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) &&
                endpoint is not null && endpoint.Scheme == Uri.UriSchemeHttps &&
                string.IsNullOrEmpty(endpoint.UserInfo) && endpoint.AbsolutePath == "/" &&
                string.IsNullOrEmpty(endpoint.Query) &&
                string.IsNullOrEmpty(endpoint.Fragment),
            "Registry replica endpoint is not a clean HTTPS origin.");
        return new ProductionMailboxReplicaBinding(
            B64(value, "replicaId", 32, nonzero: true),
            endpoint!,
            LowerHex(value, "currentSpkiSha256", 32),
            LowerHex(value, "nextSpkiSha256", 32));
    }

    private static ProductionMailboxGrantBinding Grant(
        JsonElement value,
        int index,
        MailboxCapabilityDomain expectedDomain)
    {
        Exact(value, "domain", "epoch", "generation", "fileName", "mediaType",
            "sha256", "canonicalBase64Url");
        var canonical = B64(
            value, "canonicalBase64Url",
            MailboxAuthenticatedCapabilityLimits.GrantLength,
            nonzero: true);
        var hash = LowerHex(value, "sha256", 32);
        var domainName = expectedDomain == MailboxCapabilityDomain.Retrieve
            ? "retrieve"
            : expectedDomain == MailboxCapabilityDomain.Deposit
                ? "deposit"
                : throw Invalid("Registry MCG2 domain is unsupported.");
        Require(String(value, "domain") == domainName &&
                String(value, "fileName") ==
                    (index == 0
                        ? $"{domainName}-current.mcg2"
                        : $"{domainName}-next.mcg2") &&
                String(value, "mediaType") == GrantMedia &&
                CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(canonical), hash),
            "Registry LocalOwner MCG2 envelope is invalid.");
        return new ProductionMailboxGrantBinding(
            expectedDomain,
            Number(value, "epoch"),
            Number(value, "generation"),
            canonical);
    }

    private static CanonicalEnvelopeValue CanonicalEnvelope(
        JsonElement value,
        string expectedFile,
        string expectedMedia,
        int exactLength)
    {
        Exact(value, "fileName", "mediaType", "sha256", "canonicalBase64Url");
        var canonical = B64(value, "canonicalBase64Url", exactLength, nonzero: true);
        var hash = LowerHex(value, "sha256", 32);
        Require(String(value, "fileName") == expectedFile &&
                String(value, "mediaType") == expectedMedia &&
                CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(canonical), hash),
            "Registry canonical route envelope is invalid.");
        return new CanonicalEnvelopeValue(canonical, hash);
    }

    private static void Limits(JsonElement value)
    {
        Exact(value, "storedBytes", "storedMessages", "retentionSeconds",
            "requestsPerHour", "entitled");
        Require(Number(value, "storedBytes") == 67_108_864 &&
                Number(value, "storedMessages") == 10_000 &&
                Number(value, "retentionSeconds") == 1_209_600 &&
                Number(value, "requestsPerHour") == 1_000 &&
                value.GetProperty("entitled").ValueKind == JsonValueKind.False,
            "Registry returned unsupported or unverified service limits.");
    }

    private static byte[] B64(
        JsonElement parent,
        string property,
        int? length,
        bool nonzero)
    {
        var encoded = String(parent, property);
        if (encoded.Length == 0 || encoded.Contains('='))
            throw Invalid($"Registry {property} is not canonical base64url.");
        byte[] bytes;
        try
        {
            var padded = encoded.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            bytes = Convert.FromBase64String(padded);
        }
        catch (FormatException exception)
        {
            throw Invalid($"Registry {property} is not canonical base64url.", exception);
        }
        var canonical = Convert.ToBase64String(bytes).TrimEnd('=')
            .Replace('+', '-').Replace('/', '_');
        if (canonical != encoded || length is not null && bytes.Length != length ||
            nonzero && bytes.AsSpan().IndexOfAnyExcept((byte)0) < 0)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw Invalid($"Registry {property} has an invalid binary value.");
        }
        return bytes;
    }

    private static byte[] LowerHex(JsonElement parent, string property, int bytes)
    {
        var value = String(parent, property);
        if (value.Length != bytes * 2 || value.Any(static character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw Invalid($"Registry {property} is not lowercase hexadecimal.");
        var decoded = Convert.FromHexString(value);
        if (decoded.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            throw Invalid($"Registry {property} is all zero.");
        return decoded;
    }

    private static ulong Number(JsonElement parent, string property)
    {
        var value = parent.GetProperty(property);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt64(out var number))
            throw Invalid($"Registry {property} is not an unsigned integer.");
        return number;
    }

    private static string String(JsonElement parent, string property)
    {
        var value = parent.GetProperty(property);
        if (value.ValueKind != JsonValueKind.String)
            throw Invalid($"Registry {property} is not a string.");
        return value.GetString()!;
    }

    private static void Exact(JsonElement value, params string[] properties)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Invalid("Registry JSON value is not an object.");
        var actual = value.EnumerateObject().Select(static property => property.Name).ToArray();
        if (actual.Length != properties.Length ||
            actual.Distinct(StringComparer.Ordinal).Count() != actual.Length ||
            properties.Any(expected => !actual.Contains(expected, StringComparer.Ordinal)))
            throw Invalid("Registry JSON object has missing, duplicate, or unknown fields.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw Invalid(message);
    }

    private static InvalidDataException Invalid(string message, Exception? inner = null) =>
        new(message, inner);

    private static JsonDocument ParseDocument(ReadOnlySpan<byte> utf8, string response)
    {
        if (utf8.Length is 0 or > ProductionMailboxRegistryClient.MaximumBodyBytes)
            throw Invalid($"Registry {response} response length is invalid.");
        try
        {
            return JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 12
            });
        }
        catch (JsonException exception)
        {
            throw Invalid($"Registry {response} response is malformed JSON.", exception);
        }
    }

    private static byte[] ExternalBinary(
        ReadOnlyMemory<byte> value,
        int length,
        string name)
    {
        if (value.Length != length || value.Span.IndexOfAnyExcept((byte)0) < 0)
            throw Invalid($"Registry {name} is invalid.");
        return value.ToArray();
    }

    private sealed record ArtifactValue(
        string FileName,
        string MediaType,
        byte[] Sha256,
        string ETag,
        string ContentPath,
        byte[] Canonical)
    {
        public ProductionMailboxRegistryArtifact ToRegistryArtifact() => new(
            FileName, MediaType, Sha256, ETag, ContentPath, Canonical);
    }
    private sealed record CanonicalEnvelopeValue(byte[] Canonical, byte[] Sha256);
}
