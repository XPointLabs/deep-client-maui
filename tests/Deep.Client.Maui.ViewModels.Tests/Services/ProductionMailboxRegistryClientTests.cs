using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Services;
using Deep.Protocol.DeepExtension.MailboxAuthority;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class ProductionMailboxRegistryClientTests
{
    [Fact]
    public async Task CreateChallenge_PostsExactPathAndEmptyCanonicalObject()
    {
        HttpRequestMessage? captured = null;
        byte[]? body = null;
        using var http = new HttpClient(new CallbackHandler(async (request, _) =>
        {
            captured = request;
            body = (await request.Content!.ReadAsByteArrayAsync()).ToArray();
            return Json(ChallengeResponse());
        }));
        var client = Client(http);

        var challenge = await client.CreateChallengeAsync();

        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal(
            "https://registry.example/api/production-mailbox/challenges",
            captured.RequestUri!.AbsoluteUri);
        Assert.Equal("application/json", captured.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", captured.Content.Headers.ContentType.CharSet);
        Assert.Equal("{}", Encoding.UTF8.GetString(body!));
        Assert.Equal(8, challenge.LeadingZeroBits);
        Assert.Equal(16, challenge.ChallengeId.Length);
        Assert.Equal(32, challenge.Challenge.Length);
        Assert.Equal("authority.pma1", challenge.Authority.FileName);
    }

    [Fact]
    public async Task RouteEnrollment_PostsCanonicalRequestAndStrictlyParsesResponse()
    {
        byte[]? body = null;
        using var http = new HttpClient(new CallbackHandler(async (request, _) =>
        {
            Assert.Equal(
                "/api/production-mailbox/route-enrollments",
                request.RequestUri!.AbsolutePath);
            body = (await request.Content!.ReadAsByteArrayAsync()).ToArray();
            return Json(RouteEnrollmentResponse());
        }));
        var request = Request(ProductionMailboxIssuanceIntent.LocalOwner);

        var enrollment = await Client(http).SubmitRouteEnrollmentAsync(request);

        Assert.Equal(CanonicalRequestJson(request), Encoding.UTF8.GetString(body!));
        Assert.Equal(32, enrollment.EnrollmentHandle.Length);
        Assert.Equal(304, enrollment.CanonicalRouteCertificate.Length);
        Assert.True(enrollment.ControlPlane.CanonicalCurrentSelection.IsEmpty);
        Assert.True(enrollment.ControlPlane.CanonicalNextSelection.IsEmpty);
    }

    [Fact]
    public async Task CredentialEndpoints_ParseExactLocalAndPeerResponses()
    {
        var calls = 0;
        using var http = new HttpClient(new CallbackHandler((request, _) =>
        {
            Assert.Equal(
                "/api/production-mailbox/credentials",
                request.RequestUri!.AbsolutePath);
            calls++;
            return Task.FromResult(Json(CredentialResponse(
                calls == 1
                    ? ProductionMailboxIssuanceIntent.LocalOwner
                    : ProductionMailboxIssuanceIntent.PeerDeposit)));
        }));
        var client = Client(http);
        var localRequest = Request(
            ProductionMailboxIssuanceIntent.LocalOwner,
            routeAdvertisement: Bytes(0x70, 400),
            enrollmentHandle: Bytes(0x71, 32));
        var advertisement = Bytes(0x72, 400);
        var peerRequest = Request(
            ProductionMailboxIssuanceIntent.PeerDeposit,
            route: true,
            ownerProof: false,
            routeAdvertisement: advertisement);

        var local = await client.SubmitLocalOwnerIssuanceAsync(localRequest);
        var peer = await client.SubmitPeerDepositIssuanceAsync(
            peerRequest, Bytes(0x73, 32), advertisement);

        Assert.Equal(2, calls);
        Assert.Equal(2, local.Grants.Count);
        Assert.All(local.Grants, grant =>
            Assert.Equal(
                Deep.Protocol.DeepExtension.MailboxCapabilities.MailboxCapabilityDomain.Retrieve,
                grant.Domain));
        Assert.Equal(2, peer.Grants.Count);
        Assert.All(peer.Grants, grant =>
            Assert.Equal(
                Deep.Protocol.DeepExtension.MailboxCapabilities.MailboxCapabilityDomain.Deposit,
                grant.Domain));
        Assert.Equal(advertisement, peer.CanonicalRouteAdvertisement.ToArray());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 0)]
    [InlineData(HttpStatusCode.Forbidden, 1)]
    [InlineData(HttpStatusCode.Conflict, 2)]
    [InlineData(HttpStatusCode.TooManyRequests, 3)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 4)]
    [InlineData(HttpStatusCode.Unauthorized, 5)]
    public async Task NonSuccessStatus_IsMappedExactly_WithoutRetry(
        HttpStatusCode status,
        int expected)
    {
        var calls = 0;
        using var http = new HttpClient(new CallbackHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(status));
        }));

        var error = await Assert.ThrowsAsync<ProductionMailboxRegistryRequestException>(
            () => Client(http).CreateChallengeAsync());

        Assert.Equal((ProductionMailboxRegistryRequestError)expected, error.Error);
        Assert.Equal(status, error.StatusCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OversizeResponse_IsRejectedBeforeParsing()
    {
        using var http = new HttpClient(new CallbackHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(
                    new byte[ProductionMailboxRegistryClient.MaximumBodyBytes + 1])
            })));

        var error = await Assert.ThrowsAsync<ProductionMailboxRegistryRequestException>(
            () => Client(http).CreateChallengeAsync());

        Assert.Equal(ProductionMailboxRegistryRequestError.ResponseTooLarge, error.Error);
    }

    [Fact]
    public async Task CallerCancellation_StopsInFlightRequest()
    {
        using var http = new HttpClient(new CallbackHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Client(http).CreateChallengeAsync(cancellation.Token));
    }

    [Fact]
    public async Task BoundedTimeout_StopsInFlightRequest()
    {
        using var http = new HttpClient(new CallbackHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }));
        using var client = Client(http, TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAsync<TimeoutException>(() => client.CreateChallengeAsync());
    }

    [Fact]
    public async Task ProofOfWork_MatchesFrozenVector_AndHonorsSearchBound()
    {
        var challenge = ParsedChallenge(leadingZeroBits: 8);

        var nonce = await ProductionMailboxRegistryClient.SolveProofOfWorkAsync(challenge);

        Assert.Equal(45UL, nonce);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProductionMailboxRegistryClient.SolveProofOfWorkAsync(
                challenge, maximumNonce: 44));
    }

    [Fact]
    public void ProofOfWork_RejectsServerDifficultyOutsideEightToTwentyTwo()
    {
        Assert.Throws<InvalidDataException>(() => ParsedChallenge(7));
        Assert.Throws<InvalidDataException>(() => ParsedChallenge(23));
    }

    [Fact]
    public async Task ProofOfWork_CancellationIsObserved()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProductionMailboxRegistryClient.SolveProofOfWorkAsync(
                ParsedChallenge(22), cancellationToken: cancellation.Token));
    }

    [Fact]
    public void Constructor_RejectsNonHttpsOrUnboundedTimeout()
    {
        using var http = new HttpClient(new CallbackHandler((_, _) =>
            throw new UnreachableException()));

        Assert.Throws<ArgumentException>(() =>
            ProductionMailboxRegistryClient.CreateTransportOptions(
                new Uri("http://registry.example/")));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProductionMailboxRegistryClient.CreateTransportOptions(
                new Uri("https://registry.example/"),
                ProductionMailboxRegistryClient.MaximumRequestTimeout +
                TimeSpan.FromMilliseconds(1)));
    }

    private static ProductionMailboxRegistryClient Client(
        HttpClient http,
        TimeSpan? requestTimeout = null) => new(
        new HttpServiceRequestTransport(
            http,
            ProductionMailboxRegistryClient.CreateTransportOptions(
                new Uri("https://registry.example/"), requestTimeout),
            HttpServiceEndpointPolicy.Production));

    private static ProductionMailboxRegistryChallenge ParsedChallenge(int leadingZeroBits)
    {
        using var document = JsonDocument.Parse(ChallengeResponse(leadingZeroBits));
        var values = document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone());
        return ProductionMailboxRegistryJsonCodec.ParseChallenge(
            JsonSerializer.SerializeToUtf8Bytes(values));
    }

    private static ProductionMailboxRegistryIssueRequest Request(
        ProductionMailboxIssuanceIntent intent,
        bool route = false,
        bool ownerProof = true,
        byte[]? routeAdvertisement = null,
        byte[]? enrollmentHandle = null) => new(
            Bytes(0x11, 32),
            Bytes(0x21, 32),
            intent,
            ProductionMailboxClientPlatform.Android,
            Bytes(0x31, 32),
            Bytes(0x41, 32),
            Bytes(0x51, 32),
            new byte[32],
            route ? Bytes(0x61, 32) : ReadOnlyMemory<byte>.Empty,
            route ? Bytes(0x62, 32) : ReadOnlyMemory<byte>.Empty,
            route ? Bytes(0x63, 32) : ReadOnlyMemory<byte>.Empty,
            Bytes(0x64, 16),
            Bytes(0x65, 32),
            68,
            Bytes(0x66, 64),
            ownerProof ? Bytes(0x67, 64) : ReadOnlyMemory<byte>.Empty,
            routeAdvertisement ?? ReadOnlyMemory<byte>.Empty,
            enrollmentHandle ?? ReadOnlyMemory<byte>.Empty);

    private static string CanonicalRequestJson(ProductionMailboxRegistryIssueRequest request)
    {
        var payload = new StringBuilder();
        payload.Append('{')
            .Append("\"holderEd25519PublicKey\":\"").Append(B64(request.HolderEd25519PublicKey.Span))
            .Append("\",\"mailboxOwnerEd25519PublicKey\":\"").Append(B64(request.MailboxOwnerEd25519PublicKey.Span))
            .Append("\",\"intent\":").Append((byte)request.Intent)
            .Append(",\"platform\":").Append((byte)request.Platform)
            .Append(",\"signingCertificateSha256\":\"").Append(B64(request.SigningCertificateSha256.Span))
            .Append("\",\"buildArtifactSha256\":\"").Append(B64(request.BuildArtifactSha256.Span))
            .Append("\",\"idempotencyKey\":\"").Append(B64(request.IdempotencyKey.Span))
            .Append("\",\"entitlementCommitment\":\"").Append(B64(request.EntitlementCommitment.Span))
            .Append("\",\"blindedMailboxId\":\"\",\"blindedPlacementId\":\"\",\"selectionInputCommitment\":\"\"")
            .Append(",\"challengeId\":\"").Append(B64(request.ChallengeId.Span))
            .Append("\",\"challenge\":\"").Append(B64(request.Challenge.Span))
            .Append("\",\"proofOfWorkNonce\":").Append(request.ProofOfWorkNonce)
            .Append(",\"holderProofSignature\":\"").Append(B64(request.HolderProofSignature.Span))
            .Append("\",\"ownerProofSignature\":\"").Append(B64(request.OwnerProofSignature.Span))
            .Append("\",\"opaqueEntitlement\":null,\"routeAdvertisement\":null,\"enrollmentHandle\":null}");
        return payload.ToString();
    }

    private static byte[] ChallengeResponse(int leadingZeroBits = 8)
    {
        var authority = Bytes(0x81, 320);
        var revocation = Bytes(0x82, 256);
        var topology = Bytes(0x83, 640);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            challengeId = B64(Bytes(0x01, 16)),
            challenge = B64(Bytes(0x11, 32)),
            leadingZeroBits,
            expiresAtUnixSeconds = 2_100_000_000UL,
            authority = Artifact("authority.pma1",
                "application/vnd.deep.production-mailbox-authority", authority),
            revocation = Artifact("revocations.pmr1",
                "application/vnd.deep.production-mailbox-revocation-snapshot", revocation),
            topology = Artifact("topology.pmt1",
                "application/vnd.deep.production-mailbox-topology", topology)
        });
    }

    private static byte[] RouteEnrollmentResponse()
    {
        var authority = Bytes(0x81, 320);
        var revocation = Bytes(0x82, 256);
        var topology = Bytes(0x83, 640);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "production-mailbox-route-enrollment.v1",
            enrollmentHandle = B64(Bytes(0x10, 32)),
            idempotencyKey = B64(Bytes(0x11, 32)),
            holderEd25519PublicKey = B64(Bytes(0x12, 32)),
            mailboxOwnerEd25519PublicKey = B64(Bytes(0x13, 32)),
            blindedMailboxId = B64(Bytes(0x14, 32)),
            blindedPlacementId = B64(Bytes(0x15, 32)),
            selectionInputCommitment = B64(Bytes(0x16, 32)),
            authority = Artifact("authority.pma1",
                "application/vnd.deep.production-mailbox-authority", authority),
            revocation = Artifact("revocations.pmr1",
                "application/vnd.deep.production-mailbox-revocation-snapshot", revocation),
            topology = Artifact("topology.pmt1",
                "application/vnd.deep.production-mailbox-topology", topology),
            routeCertificate = Envelope(
                "route-certificate.prc1",
                "application/vnd.deep.production-mailbox-route-certificate",
                Bytes(0x17, 304)),
            issuedAtUnixSeconds = 2_100_000_000UL,
            expiresAtUnixSeconds = 2_100_001_000UL
        });
    }

    private static byte[] CredentialResponse(ProductionMailboxIssuanceIntent intent)
    {
        var authority = Bytes(0x81, 320);
        var revocation = Bytes(0x82, 256);
        var topology = Bytes(0x83, 640);
        var retrieve = intent == ProductionMailboxIssuanceIntent.LocalOwner;
        var domain = retrieve ? "retrieve" : "deposit";
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "production-mailbox-credential-bundle.v1",
            idempotencyKey = B64(Bytes(0x20, 32)),
            holderEd25519PublicKey = B64(Bytes(0x21, 32)),
            mailboxOwnerEd25519PublicKey = B64(Bytes(0x22, 32)),
            intent = (byte)intent,
            blindedMailboxId = B64(Bytes(0x23, 32)),
            blindedPlacementId = B64(Bytes(0x24, 32)),
            selectionInputCommitment = B64(Bytes(0x25, 32)),
            authority = Artifact("authority.pma1",
                "application/vnd.deep.production-mailbox-authority", authority),
            revocation = Artifact("revocations.pmr1",
                "application/vnd.deep.production-mailbox-revocation-snapshot", revocation),
            topology = Artifact("topology.pmt1",
                "application/vnd.deep.production-mailbox-topology", topology),
            selections = new[]
            {
                Selection(9, 70, "selection-current.pms1", Bytes(0x31, 512), 0x41),
                Selection(10, 71, "selection-next.pms1", Bytes(0x32, 512), 0x51)
            },
            grants = new[]
            {
                Grant(domain, 9, 70, $"{domain}-current.mcg2", Bytes(0x33, 272)),
                Grant(domain, 10, 71, $"{domain}-next.mcg2", Bytes(0x34, 272))
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
            routeCertificate = retrieve
                ? Envelope("route-certificate.prc1",
                    "application/vnd.deep.production-mailbox-route-certificate",
                    Bytes(0x35, 304))
                : null,
            routeAdvertisement = retrieve
                ? Envelope("route-advertisement.pra1",
                    "application/vnd.deep.production-mailbox-route-advertisement",
                    Bytes(0x36, 400))
                : null,
            selectionSuccessor = (object?)null
        });
    }

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
            Replica(unchecked((byte)(seed + 1)), $"https://node-{epoch}-2.example.net/")
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
        string domain,
        ulong epoch,
        ulong generation,
        string file,
        byte[] canonical) => new
    {
        domain,
        epoch,
        generation,
        fileName = file,
        mediaType = "application/vnd.deep.mailbox-authenticated-grant",
        sha256 = Convert.ToHexStringLower(SHA256.HashData(canonical)),
        canonicalBase64Url = B64(canonical)
    };

    private static HttpResponseMessage Json(byte[] body) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(body)
        {
            Headers = { ContentType = new("application/json") }
        }
    };

    private static string B64(ReadOnlySpan<byte> value) => Convert.ToBase64String(value)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Bytes(byte seed, int length) => Enumerable.Range(0, length)
        .Select(index => unchecked((byte)(seed + index))).ToArray();

    private sealed class CallbackHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await callback(request, cancellationToken);
            response.RequestMessage ??= request;
            return response;
        }
    }
}
