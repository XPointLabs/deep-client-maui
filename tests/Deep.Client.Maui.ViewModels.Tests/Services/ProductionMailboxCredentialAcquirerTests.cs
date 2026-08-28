using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Protocol.DeepExtension.MailboxAuthority;
using Deep.Protocol.DeepExtension.MailboxCapabilities;
using Deep.Protocol.DeepExtension.MailboxTopology;
using Sodium;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class ProductionMailboxCredentialAcquirerTests
{
    private const string HolderPhrase =
        "amaze buffet cake entrance symptoms tiger lamb maze nestle python dusted faxed faxed";
    private const string RecipientPhrase =
        "update vague zinger boxes ornament renting glass gained island nabbing afield calamity nabbing";
    private const ulong Now = 2_000_000_000;

    [Fact]
    public async Task LocalOwner_UsesTwoFreshChallenges_AndExactHolderOwnerProofs()
    {
        using var fixture = new Fixture();
        var requests = new List<byte[]>();
        var calls = 0;
        using var http = new HttpClient(new CallbackHandler(async (request, _) =>
        {
            calls++;
            var body = await request.Content!.ReadAsByteArrayAsync();
            requests.Add(body);
            return calls switch
            {
                1 => fixture.ChallengeResponse(0x11),
                2 => fixture.EnrollmentResponse(body),
                3 => fixture.ChallengeResponse(0x31),
                4 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                _ => throw new InvalidOperationException("Acquirer retried unexpectedly.")
            };
        }));
        var acquirer = fixture.CreateAcquirer(http);

        var error = await Assert.ThrowsAsync<ProductionMailboxRegistryRequestException>(
            () => acquirer.AcquireLocalOwnerAsync());

        Assert.Equal(ProductionMailboxRegistryRequestError.Unavailable, error.Error);
        Assert.Equal(4, calls);
        AssertLocalProof(requests[1], fixture, expectAdvertisement: false);
        AssertLocalProof(requests[3], fixture, expectAdvertisement: true);
        using var enrollment = JsonDocument.Parse(requests[1]);
        using var issuance = JsonDocument.Parse(requests[3]);
        Assert.Equal(
            enrollment.RootElement.GetProperty("idempotencyKey").GetString(),
            issuance.RootElement.GetProperty("idempotencyKey").GetString());
        Assert.NotEqual(
            enrollment.RootElement.GetProperty("challengeId").GetString(),
            issuance.RootElement.GetProperty("challengeId").GetString());
    }

    [Fact]
    public async Task LocalOwnerRefresh_AuthorsNextMonotonicPra1Sequence()
    {
        using var fixture = new Fixture();
        var requests = new List<byte[]>();
        var calls = 0;
        using var http = new HttpClient(new CallbackHandler(async (request, _) =>
        {
            calls++;
            var body = await request.Content!.ReadAsByteArrayAsync();
            requests.Add(body);
            return calls switch
            {
                1 => fixture.ChallengeResponse(0x12),
                2 => fixture.EnrollmentResponse(body),
                3 => fixture.ChallengeResponse(0x32),
                4 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                _ => throw new InvalidOperationException("Acquirer retried unexpectedly.")
            };
        }));

        await Assert.ThrowsAsync<ProductionMailboxRegistryRequestException>(() =>
            fixture.CreateAcquirer(http).AcquireLocalOwnerAsync(
                fixture.PredecessorRoute(sequence: 7)));

        using var issuance = JsonDocument.Parse(requests[3]);
        var advertisement = ProductionMailboxRouteAdvertisementCodec.DecodeAdvertisement(
            B64(issuance.RootElement, "routeAdvertisement"));
        Assert.Equal(8UL, advertisement.Sequence);
    }

    [Fact]
    public async Task ReactiveSuccessorRejectsNonAdvancingVerifiedServerGenerationBeforeEnrollment()
    {
        using var fixture = new Fixture();
        var calls = 0;
        using var http = new HttpClient(new CallbackHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(fixture.ChallengeResponse(0x42));
        }));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.CreateAcquirer(http).AcquireSuccessorLocalOwnerAsync(
                fixture.PredecessorRoute(sequence: 7),
                failedRuntimeGeneration: 70));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PeerDeposit_BindsVerifiedRoute_AndNeverAddsOwnerProofOrRetry()
    {
        using var fixture = new Fixture();
        var requests = new List<byte[]>();
        var calls = 0;
        using var recipient = new SessionIdentityProvider(RecipientPhrase);
        using var recipientOwner = new ProductionMailboxOwnerIdentity(Bytes(0x91, 32));
        var invitation = fixture.Invitation(recipient, recipientOwner);
        using var http = new HttpClient(new CallbackHandler(async (request, _) =>
        {
            calls++;
            requests.Add(await request.Content!.ReadAsByteArrayAsync());
            return calls switch
            {
                1 => fixture.ChallengeResponse(0x51),
                2 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                _ => throw new InvalidOperationException("Acquirer retried unexpectedly.")
            };
        }));

        var error = await Assert.ThrowsAsync<ProductionMailboxRegistryRequestException>(
            () => fixture.CreateAcquirer(http).AcquirePeerDepositAsync(invitation));

        Assert.Equal(ProductionMailboxRegistryRequestError.Unavailable, error.Error);
        Assert.Equal(2, calls);
        using var request = JsonDocument.Parse(requests[1]);
        var root = request.RootElement;
        Assert.Equal((byte)ProductionMailboxIssuanceIntent.PeerDeposit,
            root.GetProperty("intent").GetByte());
        Assert.Equal(JsonValueKind.Null,
            root.GetProperty("ownerProofSignature").ValueKind);
        Assert.Equal(JsonValueKind.Null,
            root.GetProperty("enrollmentHandle").ValueKind);
        Assert.Equal(
            B64(invitation.CanonicalRouteAdvertisement.Span),
            root.GetProperty("routeAdvertisement").GetString());
        AssertHolderProof(root, fixture.HolderIdentity.GetEd25519PublicKey());
    }

    [Fact]
    public async Task ExpiredChallenge_FailsBeforeProofOrRetry()
    {
        using var fixture = new Fixture();
        var calls = 0;
        using var http = new HttpClient(new CallbackHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(fixture.ChallengeResponse(
                0x71, expiresAt: Now));
        }));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.CreateAcquirer(http).AcquireLocalOwnerAsync());

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Constructor_RejectsUnattestedArtifactHashesBeforeNetworkUse()
    {
        using var fixture = new Fixture();
        using var http = new HttpClient(new CallbackHandler((_, _) =>
            throw new InvalidOperationException("Network must not be reached.")));

        Assert.Throws<InvalidDataException>(() => fixture.CreateAcquirer(
            http,
            new ProductionMailboxClientApprovalIdentity(
                MailboxClientPlatform.Android,
                ProductionMailboxControlPlaneVerifier.AndroidApplicationIdentity,
                new byte[32],
                Bytes(0x81, 32))));
    }

    private static void AssertLocalProof(
        byte[] body,
        Fixture fixture,
        bool expectAdvertisement)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal((byte)ProductionMailboxIssuanceIntent.LocalOwner,
            root.GetProperty("intent").GetByte());
        Assert.Equal(string.Empty, root.GetProperty("blindedMailboxId").GetString());
        Assert.Equal(string.Empty, root.GetProperty("blindedPlacementId").GetString());
        Assert.Equal(string.Empty,
            root.GetProperty("selectionInputCommitment").GetString());
        Assert.Equal(expectAdvertisement ? JsonValueKind.String : JsonValueKind.Null,
            root.GetProperty("routeAdvertisement").ValueKind);
        Assert.Equal(expectAdvertisement ? JsonValueKind.String : JsonValueKind.Null,
            root.GetProperty("enrollmentHandle").ValueKind);
        var input = ProofInput(root, fixture.NetworkId, fixture.AuthorityHash);
        var signingBytes = ProductionMailboxHolderProof.GetSigningBytes(input);
        var holderKey = fixture.HolderIdentity.GetEd25519PublicKey();
        try
        {
            var verifier = new SodiumProductionMailboxHolderProofSignatureVerifier();
            Assert.True(verifier.Verify(
                holderKey, signingBytes, B64(root, "holderProofSignature")));
            Assert.True(verifier.Verify(
                B64(root, "mailboxOwnerEd25519PublicKey"),
                signingBytes,
                B64(root, "ownerProofSignature")));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingBytes);
            CryptographicOperations.ZeroMemory(holderKey);
        }
    }

    private static void AssertHolderProof(JsonElement root, byte[] holderKey)
    {
        try
        {
            var route = ProductionMailboxRouteAdvertisementCodec.DecodeAdvertisement(
                B64(root, "routeAdvertisement"));
            var input = ProofInput(
                root,
                route.Certificate.NetworkId.ToArray(),
                route.Certificate.CanonicalAuthorityHash.ToArray());
            var signingBytes = ProductionMailboxHolderProof.GetSigningBytes(input);
            try
            {
                Assert.True(new SodiumProductionMailboxHolderProofSignatureVerifier().Verify(
                    holderKey,
                    signingBytes,
                    B64(root, "holderProofSignature")));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(signingBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(holderKey);
        }
    }

    private static ProductionMailboxHolderProofInput ProofInput(
        JsonElement root,
        byte[] networkId,
        byte[] authorityHash) => new()
    {
        Intent = (ProductionMailboxIssuanceIntent)root.GetProperty("intent").GetByte(),
        Platform = (ProductionMailboxClientPlatform)root.GetProperty("platform").GetByte(),
        NetworkId = networkId,
        CanonicalAuthorityHash = authorityHash,
        HolderEd25519PublicKey = B64(root, "holderEd25519PublicKey"),
        MailboxOwnerEd25519PublicKey = B64(root, "mailboxOwnerEd25519PublicKey"),
        BlindedMailboxId = RouteField(root, "blindedMailboxId"),
        BlindedPlacementId = RouteField(root, "blindedPlacementId"),
        SelectionInputCommitment = RouteField(root, "selectionInputCommitment"),
        SigningCertificateSha256 = B64(root, "signingCertificateSha256"),
        BuildArtifactSha256 = B64(root, "buildArtifactSha256"),
        IdempotencyKey = B64(root, "idempotencyKey"),
        EntitlementCommitment = B64(root, "entitlementCommitment"),
        ChallengeId = B64(root, "challengeId"),
        Challenge = B64(root, "challenge"),
        ProofOfWorkNonce = root.GetProperty("proofOfWorkNonce").GetUInt64()
    };

    private sealed class Fixture : IDisposable
    {
        private readonly string databasePath = Path.Combine(
            Path.GetTempPath(), $"deep-acquirer-{Guid.NewGuid():N}.db");
        private readonly byte[] mrXPrivateKey;
        private readonly byte[] issuerPrivateKey;
        private readonly ProductionMailboxAuthority authority;
        private readonly FixedTimeProvider time = new(
            DateTimeOffset.FromUnixTimeSeconds((long)Now));
        private readonly SqliteSessionStore store;
        private readonly ProductionMailboxOwnerIdentity ownerIdentity =
            new(Bytes(0x61, 32));

        public Fixture()
        {
            HolderIdentity = new SessionIdentityProvider(HolderPhrase);
            var mrX = PublicKeyAuth.GenerateKeyPair(Bytes(0x21, 32));
            var issuer = PublicKeyAuth.GenerateKeyPair(Bytes(0x41, 32));
            mrXPrivateKey = mrX.PrivateKey.ToArray();
            issuerPrivateKey = issuer.PrivateKey.ToArray();
            authority = Authority(mrX.PublicKey, issuer.PublicKey, mrXPrivateKey);
            AuthorityCanonical = ProductionMailboxAuthorityCodec.Encode(authority);
            AuthorityHash = SHA256.HashData(AuthorityCanonical);
            NetworkId = authority.NetworkId.ToArray();
            BuildAnchor = new ProductionMailboxTrustAnchor(
                SHA256.HashData(mrX.PublicKey),
                NetworkId,
                authority.AuthorityGeneration,
                AuthorityHash,
                authority.Revocation.Generation,
                authority.Revocation.HeadHash,
                authority.Revocation.SnapshotHash,
                1,
                Bytes(0x55, 32));
            store = new SqliteSessionStore(databasePath);
        }

        public SessionIdentityProvider HolderIdentity { get; }
        public byte[] AuthorityCanonical { get; }
        public byte[] AuthorityHash { get; }
        public byte[] NetworkId { get; }
        public ProductionMailboxTrustAnchor BuildAnchor { get; }

        public ProductionMailboxCredentialAcquirer CreateAcquirer(
            HttpClient http,
            ProductionMailboxClientApprovalIdentity? approval = null) => new(
                new ProductionMailboxRegistryClient(
                    http, new Uri("https://registry.example/")),
                store,
                HolderIdentity,
                ownerIdentity,
                BuildAnchor,
                new TrustStore(),
                approval ?? new ProductionMailboxClientApprovalIdentity(
                    MailboxClientPlatform.Android,
                    ProductionMailboxControlPlaneVerifier.AndroidApplicationIdentity,
                    Bytes(0x71, 32),
                    Bytes(0x81, 32)),
                time);

        public HttpResponseMessage ChallengeResponse(
            byte challengeSeed,
            ulong expiresAt = Now + 300)
        {
            var revocation = Bytes(0x31, 256);
            var topology = Bytes(0x32, 640);
            return Json(new
            {
                challengeId = B64(Bytes(challengeSeed, 16)),
                challenge = B64(Bytes((byte)(challengeSeed + 1), 32)),
                leadingZeroBits = 8,
                expiresAtUnixSeconds = expiresAt,
                authority = Artifact(
                    "authority.pma1",
                    "application/vnd.deep.production-mailbox-authority",
                    AuthorityCanonical),
                revocation = Artifact(
                    "revocations.pmr1",
                    "application/vnd.deep.production-mailbox-revocation-snapshot",
                    revocation),
                topology = Artifact(
                    "topology.pmt1",
                    "application/vnd.deep.production-mailbox-topology",
                    topology)
            });
        }

        public HttpResponseMessage EnrollmentResponse(byte[] requestBody)
        {
            using var request = JsonDocument.Parse(requestBody);
            var root = request.RootElement;
            var ownerKey = B64(root, "mailboxOwnerEd25519PublicKey");
            var mailbox = Bytes(0xa1, 32);
            var placement = Bytes(0xb1, 32);
            var selection = ProductionMailboxReplicaSelection
                .ComputeSelectionInputCommitment(new BlindedPlacementId(placement));
            var certificate = SignCertificate(new ProductionMailboxRouteCertificate
            {
                NetworkId = NetworkId,
                AuthorityGeneration = authority.AuthorityGeneration,
                CanonicalAuthorityHash = AuthorityHash,
                IssuerEd25519PublicKey = authority.MailboxIssuerEd25519PublicKey,
                MailboxOwnerEd25519PublicKey = ownerKey,
                BlindedMailboxId = mailbox,
                BlindedPlacementId = placement,
                SelectionInputCommitment = selection,
                IssuedAtUnixSeconds = Now,
                ExpiresAtUnixSeconds = Now + 600,
                IssuerSignature = new byte[64]
            });
            var canonicalCertificate = ProductionMailboxRouteAdvertisementCodec
                .EncodeCertificate(certificate);
            return Json(new
            {
                schema = "production-mailbox-route-enrollment.v1",
                enrollmentHandle = B64(Bytes(0xd1, 32)),
                idempotencyKey = root.GetProperty("idempotencyKey").GetString(),
                holderEd25519PublicKey = root.GetProperty(
                    "holderEd25519PublicKey").GetString(),
                mailboxOwnerEd25519PublicKey = root.GetProperty(
                    "mailboxOwnerEd25519PublicKey").GetString(),
                blindedMailboxId = B64(mailbox),
                blindedPlacementId = B64(placement),
                selectionInputCommitment = B64(selection),
                authority = Artifact(
                    "authority.pma1",
                    "application/vnd.deep.production-mailbox-authority",
                    AuthorityCanonical),
                revocation = Artifact(
                    "revocations.pmr1",
                    "application/vnd.deep.production-mailbox-revocation-snapshot",
                    Bytes(0x31, 256)),
                topology = Artifact(
                    "topology.pmt1",
                    "application/vnd.deep.production-mailbox-topology",
                    Bytes(0x32, 640)),
                routeCertificate = Envelope(
                    "route-certificate.prc1",
                    "application/vnd.deep.production-mailbox-route-certificate",
                    canonicalCertificate),
                issuedAtUnixSeconds = Now,
                expiresAtUnixSeconds = Now + 600
            });
        }

        public VerifiedContactMailboxInvitation Invitation(
            SessionIdentityProvider recipient,
            ProductionMailboxOwnerIdentity recipientOwner)
        {
            var ownerKey = recipientOwner.GetPublicKey();
            var placement = Bytes(0xe1, 32);
            var certificate = SignCertificate(new ProductionMailboxRouteCertificate
            {
                NetworkId = NetworkId,
                AuthorityGeneration = authority.AuthorityGeneration,
                CanonicalAuthorityHash = AuthorityHash,
                IssuerEd25519PublicKey = authority.MailboxIssuerEd25519PublicKey,
                MailboxOwnerEd25519PublicKey = ownerKey,
                BlindedMailboxId = Bytes(0xd1, 32),
                BlindedPlacementId = placement,
                SelectionInputCommitment = ProductionMailboxReplicaSelection
                    .ComputeSelectionInputCommitment(new BlindedPlacementId(placement)),
                IssuedAtUnixSeconds = Now,
                ExpiresAtUnixSeconds = Now + 600,
                IssuerSignature = new byte[64]
            });
            var draft = new ProductionMailboxRouteAdvertisement
            {
                Certificate = certificate,
                Sequence = 1,
                PublishedAtUnixSeconds = Now,
                ExpiresAtUnixSeconds = Now + 600,
                OwnerSignature = new byte[64]
            };
            var advertisement = draft with
            {
                OwnerSignature = recipientOwner.SignRouteAdvertisement(draft)
            };
            var canonical = ProductionMailboxRouteAdvertisementCodec
                .EncodeAdvertisement(advertisement);
            return new VerifiedContactMailboxInvitation(
                recipient.SessionId,
                recipient.GetEd25519PublicKey(),
                ownerKey,
                canonical,
                Now,
                Now + 600,
                1,
                ProductionMailboxRouteAdvertisementCodec.ComputeRouteDomainHash(certificate),
                SHA256.HashData(canonical));
        }

        public ProductionMailboxLocalOwnerPublicRoute PredecessorRoute(ulong sequence)
        {
            var ownerKey = ownerIdentity.GetPublicKey();
            var placement = Bytes(0xb1, 32);
            var certificate = new ProductionMailboxRouteCertificate
            {
                NetworkId = NetworkId,
                AuthorityGeneration = authority.AuthorityGeneration,
                CanonicalAuthorityHash = AuthorityHash,
                IssuerEd25519PublicKey = authority.MailboxIssuerEd25519PublicKey,
                MailboxOwnerEd25519PublicKey = ownerKey,
                BlindedMailboxId = Bytes(0xa1, 32),
                BlindedPlacementId = placement,
                SelectionInputCommitment = ProductionMailboxReplicaSelection
                    .ComputeSelectionInputCommitment(new BlindedPlacementId(placement)),
                IssuedAtUnixSeconds = Now - 600,
                ExpiresAtUnixSeconds = Now,
                IssuerSignature = Bytes(0x91, 64)
            };
            var advertisement = new ProductionMailboxRouteAdvertisement
            {
                Certificate = certificate,
                Sequence = sequence,
                PublishedAtUnixSeconds = Now - 600,
                ExpiresAtUnixSeconds = Now,
                OwnerSignature = Bytes(0x92, 64)
            };
            return new ProductionMailboxLocalOwnerPublicRoute(
                ownerKey,
                ProductionMailboxRouteAdvertisementCodec.EncodeAdvertisement(advertisement),
                advertisement.ExpiresAtUnixSeconds);
        }

        private ProductionMailboxRouteCertificate SignCertificate(
            ProductionMailboxRouteCertificate draft) => draft with
        {
            IssuerSignature = PublicKeyAuth.SignDetached(
                ProductionMailboxRouteAdvertisementCodec.GetCertificateSigningBytes(draft),
                issuerPrivateKey)
        };

        public void Dispose()
        {
            HolderIdentity.Dispose();
            ownerIdentity.Dispose();
            store.Dispose();
            CryptographicOperations.ZeroMemory(mrXPrivateKey);
            CryptographicOperations.ZeroMemory(issuerPrivateKey);
            TryDelete(databasePath);
            TryDelete(databasePath + "-wal");
            TryDelete(databasePath + "-shm");
        }
    }

    private static ProductionMailboxAuthority Authority(
        byte[] mrXPublicKey,
        byte[] issuerPublicKey,
        byte[] mrXPrivateKey)
    {
        var draft = new ProductionMailboxAuthority
        {
            DevelopmentOnly = false,
            Environment = ProductionMailboxAuthorityEnvironment.Production,
            Transport = ProductionMailboxAuthorityTransport.AuthenticatedMau2,
            Ownership = ProductionMailboxAuthorityOwnership.OfficialManaged,
            EndpointPolicy = ProductionMailboxAuthorityEndpointPolicy.PublicHttpsOnly,
            NetworkId = Bytes(0x01, 16),
            AuthorityGeneration = 7,
            PreviousAuthorityHash = Bytes(0x11, 32),
            MailboxIssuerEd25519PublicKey = issuerPublicKey,
            MrXApprovalEd25519PublicKey = mrXPublicKey,
            Coordinator = Endpoint("https://coord.example.net/", 0x31),
            NodeIngress = Endpoint("https://ingress.example.net/mau2/", 0x41),
            CurrentEpoch = Epoch(9, 70, Now - 60, Now + 1_800, 0x51),
            NextEpoch = Epoch(10, 71, Now + 900, Now + 3_600, 0x61),
            Revocation = new ProductionMailboxAuthorityRevocation
            {
                SnapshotHash = Bytes(0x71, 32),
                HeadHash = Bytes(0x81, 32),
                PreviousHeadHash = Bytes(0x91, 32),
                Generation = 6,
                IssuedAtUnixSeconds = Now - 60,
                ExpiresAtUnixSeconds = Now + 1_800
            },
            MrXApproval = new ProductionMailboxAuthorityApproval
            {
                AuthorityPayloadHash = Bytes(0xa1, 32),
                AllowedAndroidSigningCertificateSha256 = [Bytes(0x71, 32)],
                AllowedWindowsSigningCertificateSha256 = [Bytes(0x72, 32)],
                AndroidReleaseBuildArtifactSha256 = [Bytes(0x81, 32)],
                WindowsReleaseBuildArtifactSha256 = [Bytes(0x82, 32)],
                RolloutNotBeforeUnixSeconds = Now - 60,
                RolloutNotAfterUnixSeconds = Now + 1_800
            },
            Signature = new byte[64]
        };
        var bound = draft with
        {
            MrXApproval = draft.MrXApproval with
            {
                AuthorityPayloadHash = ProductionMailboxAuthorityCodec
                    .ComputePayloadHash(draft)
            }
        };
        return bound with
        {
            Signature = PublicKeyAuth.SignDetached(
                ProductionMailboxAuthorityCodec.GetSigningBytes(bound),
                mrXPrivateKey)
        };
    }

    private static ProductionMailboxAuthorityEndpoint Endpoint(
        string uri,
        byte seed) => new()
    {
        Uri = uri,
        CurrentSpkiSha256 = Bytes(seed, 32),
        NextSpkiSha256 = Bytes((byte)(seed + 1), 32)
    };

    private static ProductionMailboxAuthorityEpoch Epoch(
        ulong epoch,
        ulong generation,
        ulong from,
        ulong until,
        byte seed) => new()
    {
        Epoch = epoch,
        Generation = generation,
        MembershipCommitment = Bytes(seed, 32),
        TopologyPlacementCommitment = Bytes((byte)(seed + 1), 32),
        NotBeforeUnixSeconds = from,
        NotAfterUnixSeconds = until
    };

    private static object Artifact(string file, string media, byte[] canonical)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(canonical));
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

    private static object Envelope(string file, string media, byte[] canonical) => new
    {
        fileName = file,
        mediaType = media,
        sha256 = Convert.ToHexStringLower(SHA256.HashData(canonical)),
        canonicalBase64Url = B64(canonical)
    };

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value))
        {
            Headers = { ContentType = new("application/json") { CharSet = "utf-8" } }
        }
    };

    private static byte[] B64(JsonElement root, string property) =>
        DecodeB64(root.GetProperty(property).GetString() ?? string.Empty);

    private static byte[] RouteField(JsonElement root, string property)
    {
        var decoded = B64(root, property);
        return decoded.Length == 0 ? new byte[32] : decoded;
    }

    private static string B64(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] DecodeB64(string value)
    {
        var padding = (4 - value.Length % 4) % 4;
        return Convert.FromBase64String(
            value.Replace('-', '+').Replace('_', '/') + new string('=', padding));
    }

    private static byte[] Bytes(byte seed, int length) =>
        Enumerable.Range(0, length)
            .Select(index => unchecked((byte)(seed + index)))
            .ToArray();

    private static void TryDelete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private sealed class CallbackHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => callback(request, cancellationToken);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TrustStore : IProductionMailboxTrustStateStore
    {
        public Task<ProductionMailboxTrustState?> ReadAsync(
            CancellationToken cancellationToken = default) => Task.FromResult<
                ProductionMailboxTrustState?>(null);

        public Task CommitAsync(
            ulong expectedRevision,
            ProductionMailboxTrustState replacement,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Import must not be reached in this focused test.");
    }
}
