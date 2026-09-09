using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deep.Client.Shared.Services;
using Deep.Protocol.DeepExtension.MailboxAuthority;

namespace Deep.Client.Maui.Services;

internal sealed record ProductionMailboxRegistryArtifact(
    string FileName,
    string MediaType,
    ReadOnlyMemory<byte> Sha256,
    string ETag,
    string ContentPath,
    ReadOnlyMemory<byte> Canonical);

internal sealed record ProductionMailboxRegistryChallenge(
    ReadOnlyMemory<byte> ChallengeId,
    ReadOnlyMemory<byte> Challenge,
    int LeadingZeroBits,
    ulong ExpiresAtUnixSeconds,
    ProductionMailboxRegistryArtifact Authority,
    ProductionMailboxRegistryArtifact Revocation,
    ProductionMailboxRegistryArtifact Topology);

internal sealed record ProductionMailboxRegistryRouteEnrollment(
    ReadOnlyMemory<byte> EnrollmentHandle,
    ReadOnlyMemory<byte> IdempotencyKey,
    ReadOnlyMemory<byte> HolderEd25519PublicKey,
    ReadOnlyMemory<byte> MailboxOwnerEd25519PublicKey,
    ReadOnlyMemory<byte> BlindedMailboxId,
    ReadOnlyMemory<byte> BlindedPlacementId,
    ReadOnlyMemory<byte> SelectionInputCommitment,
    ProductionMailboxControlPlaneArtifacts ControlPlane,
    ReadOnlyMemory<byte> CanonicalRouteCertificate,
    ReadOnlyMemory<byte> RouteCertificateSha256,
    ulong IssuedAtUnixSeconds,
    ulong ExpiresAtUnixSeconds);

internal sealed record ProductionMailboxRegistryIssueRequest(
    ReadOnlyMemory<byte> HolderEd25519PublicKey,
    ReadOnlyMemory<byte> MailboxOwnerEd25519PublicKey,
    ProductionMailboxIssuanceIntent Intent,
    ProductionMailboxClientPlatform Platform,
    ReadOnlyMemory<byte> SigningCertificateSha256,
    ReadOnlyMemory<byte> BuildArtifactSha256,
    ReadOnlyMemory<byte> IdempotencyKey,
    ReadOnlyMemory<byte> EntitlementCommitment,
    ReadOnlyMemory<byte> BlindedMailboxId,
    ReadOnlyMemory<byte> BlindedPlacementId,
    ReadOnlyMemory<byte> SelectionInputCommitment,
    ReadOnlyMemory<byte> ChallengeId,
    ReadOnlyMemory<byte> Challenge,
    ulong ProofOfWorkNonce,
    ReadOnlyMemory<byte> HolderProofSignature,
    ReadOnlyMemory<byte> OwnerProofSignature,
    ReadOnlyMemory<byte> RouteAdvertisement,
    ReadOnlyMemory<byte> EnrollmentHandle);

internal enum ProductionMailboxRegistryRequestError
{
    InvalidRequest,
    Forbidden,
    Conflict,
    RateLimited,
    Unavailable,
    UnexpectedStatus,
    ResponseTooLarge
}

internal sealed class ProductionMailboxRegistryRequestException(
    ProductionMailboxRegistryRequestError error,
    HttpStatusCode? statusCode,
    string message) : Exception(message)
{
    public ProductionMailboxRegistryRequestError Error { get; } = error;
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

/// <summary>
/// Bounded, single-attempt HTTP primitive for the production mailbox Registry.
/// Signing and orchestration remain with the caller; this type never logs request material.
/// </summary>
internal sealed class ProductionMailboxRegistryClient : IDisposable
{
    internal const int MaximumBodyBytes = 256 * 1024;
    internal const int MaximumRequestBodyBytes = 16 * 1024;
    internal static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan MaximumRequestTimeout = TimeSpan.FromSeconds(30);

    private static ReadOnlySpan<byte> ProofOfWorkDomain =>
        "Deep/production-mailbox/pow/v1"u8;

    internal const string ChallengePath = "/api/production-mailbox/challenges";
    internal const string RouteEnrollmentPath = "/api/production-mailbox/route-enrollments";
    internal const string CredentialsPath = "/api/production-mailbox/credentials";

    private readonly HttpServiceRequestTransport transport;

    internal ProductionMailboxRegistryClient(
        HttpServiceRequestTransport transport)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    internal static HttpServiceRequestTransportOptions CreateTransportOptions(
        Uri registryOrigin,
        TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(registryOrigin);
        if (!registryOrigin.IsAbsoluteUri || registryOrigin.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(registryOrigin.UserInfo)
            || registryOrigin.AbsolutePath != "/"
            || !string.IsNullOrEmpty(registryOrigin.Query)
            || !string.IsNullOrEmpty(registryOrigin.Fragment))
        {
            throw new ArgumentException(
                "Registry URI must be a clean HTTPS origin.", nameof(registryOrigin));
        }
        var boundedTimeout = requestTimeout ?? DefaultRequestTimeout;
        if (boundedTimeout <= TimeSpan.Zero || boundedTimeout > MaximumRequestTimeout)
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeout), "Registry request timeout is outside the supported bound.");
        return new HttpServiceRequestTransportOptions(
            registryOrigin.AbsoluteUri,
            [ChallengePath, RouteEnrollmentPath, CredentialsPath],
            "application/json",
            "application/json",
            MaximumRequestBodyBytes,
            MaximumBodyBytes,
            boundedTimeout,
            RequestCharset: "utf-8",
            ResponseCharset: "utf-8");
    }

    public async Task<ProductionMailboxRegistryChallenge> CreateChallengeAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await PostAsync(
            ChallengePath, "{}"u8.ToArray(), cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return ProductionMailboxRegistryJsonCodec.ParseChallenge(response);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(response);
        }
    }

    public async Task<ProductionMailboxRegistryRouteEnrollment> SubmitRouteEnrollmentAsync(
        ProductionMailboxRegistryIssueRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRouteEnrollment(request);
        var response = await PostAsync(
            RouteEnrollmentPath,
            SerializeCanonicalRequest(request), cancellationToken).ConfigureAwait(false);
        try
        {
            return ProductionMailboxRegistryJsonCodec.ParseRouteEnrollment(response);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(response);
        }
    }

    public async Task<ProductionMailboxLocalOwnerBundle> SubmitLocalOwnerIssuanceAsync(
        ProductionMailboxRegistryIssueRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateLocalOwnerIssuance(request);
        var response = await PostAsync(
            CredentialsPath,
            SerializeCanonicalRequest(request), cancellationToken).ConfigureAwait(false);
        try
        {
            return ProductionMailboxRegistryJsonCodec.ParseLocalOwnerBundle(response);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(response);
        }
    }

    public async Task<ProductionMailboxPeerDepositBundle> SubmitPeerDepositIssuanceAsync(
        ProductionMailboxRegistryIssueRequest request,
        ReadOnlyMemory<byte> recipientEd25519PublicKey,
        ReadOnlyMemory<byte> canonicalRouteAdvertisement,
        CancellationToken cancellationToken = default)
    {
        ValidatePeerDepositIssuance(
            request, recipientEd25519PublicKey, canonicalRouteAdvertisement);
        var response = await PostAsync(
            CredentialsPath,
            SerializeCanonicalRequest(request), cancellationToken).ConfigureAwait(false);
        try
        {
            return ProductionMailboxRegistryJsonCodec.ParsePeerDepositBundle(
                response, recipientEd25519PublicKey, canonicalRouteAdvertisement);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(response);
        }
    }

    public static Task<ulong> SolveProofOfWorkAsync(
        ProductionMailboxRegistryChallenge challenge,
        ulong maximumNonce = ulong.MaxValue,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        var challengeId = Exact(challenge.ChallengeId, 16, "challenge ID", true);
        var challengeValue = Exact(challenge.Challenge, 32, "challenge", true);
        if (challenge.LeadingZeroBits is < 8 or > 22)
            throw new InvalidDataException(
                "Registry proof-of-work difficulty is outside the supported range.");

        return Task.Run(
            () => SolveProofOfWork(
                challengeId, challengeValue, challenge.LeadingZeroBits,
                maximumNonce, cancellationToken),
            cancellationToken);
    }

    private async Task<byte[]> PostAsync(
        string relativePath,
        byte[] canonicalBody,
        CancellationToken cancellationToken)
    {
        try
        {
            if (canonicalBody.Length is 0 or > MaximumRequestBodyBytes)
                throw new InvalidDataException("Registry request body length is invalid.");

            try
            {
                using var response = await transport.PostAsync(
                    relativePath, canonicalBody, cancellationToken)
                    .ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK)
                    throw Status(response.StatusCode);
                return response.Body.ToArray();
            }
            catch (HttpServiceRequestTransportException exception)
                when (exception.Error == HttpServiceRequestTransportError.ResponseTooLarge)
            {
                throw new ProductionMailboxRegistryRequestException(
                    ProductionMailboxRegistryRequestError.ResponseTooLarge,
                    HttpStatusCode.OK,
                    exception.Message);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonicalBody);
        }
    }

    public void Dispose() => transport.Dispose();

    private static ProductionMailboxRegistryRequestException Status(HttpStatusCode status) =>
        status switch
        {
            HttpStatusCode.BadRequest => Error(
                ProductionMailboxRegistryRequestError.InvalidRequest, status),
            HttpStatusCode.Forbidden => Error(
                ProductionMailboxRegistryRequestError.Forbidden, status),
            HttpStatusCode.Conflict => Error(
                ProductionMailboxRegistryRequestError.Conflict, status),
            HttpStatusCode.TooManyRequests => Error(
                ProductionMailboxRegistryRequestError.RateLimited, status),
            HttpStatusCode.ServiceUnavailable => Error(
                ProductionMailboxRegistryRequestError.Unavailable, status),
            _ => Error(ProductionMailboxRegistryRequestError.UnexpectedStatus, status)
        };

    private static ProductionMailboxRegistryRequestException Error(
        ProductionMailboxRegistryRequestError error,
        HttpStatusCode status) => new(
            error, status, $"Registry request failed with HTTP {(int)status}.");

    internal static byte[] SerializeCanonicalRequest(
        ProductionMailboxRegistryIssueRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCommon(request);
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false
        }))
        {
            writer.WriteStartObject();
            Binary(writer, "holderEd25519PublicKey", request.HolderEd25519PublicKey, false);
            Binary(writer, "mailboxOwnerEd25519PublicKey", request.MailboxOwnerEd25519PublicKey, false);
            writer.WriteNumber("intent", (byte)request.Intent);
            writer.WriteNumber("platform", (byte)request.Platform);
            Binary(writer, "signingCertificateSha256", request.SigningCertificateSha256, false);
            Binary(writer, "buildArtifactSha256", request.BuildArtifactSha256, false);
            Binary(writer, "idempotencyKey", request.IdempotencyKey, false);
            Binary(writer, "entitlementCommitment", request.EntitlementCommitment, false);
            Binary(writer, "blindedMailboxId", request.BlindedMailboxId, true);
            Binary(writer, "blindedPlacementId", request.BlindedPlacementId, true);
            Binary(writer, "selectionInputCommitment", request.SelectionInputCommitment, true);
            Binary(writer, "challengeId", request.ChallengeId, false);
            Binary(writer, "challenge", request.Challenge, false);
            writer.WriteNumber("proofOfWorkNonce", request.ProofOfWorkNonce);
            Binary(writer, "holderProofSignature", request.HolderProofSignature, false);
            OptionalBinary(writer, "ownerProofSignature", request.OwnerProofSignature);
            writer.WriteNull("opaqueEntitlement");
            OptionalBinary(writer, "routeAdvertisement", request.RouteAdvertisement);
            OptionalBinary(writer, "enrollmentHandle", request.EnrollmentHandle);
            writer.WriteEndObject();
        }
        var bytes = output.WrittenSpan.ToArray();
        if (bytes.Length > MaximumRequestBodyBytes)
            throw new InvalidDataException("Registry request body exceeds the supported bound.");
        return bytes;
    }

    private static ulong SolveProofOfWork(
        byte[] challengeId,
        byte[] challenge,
        int leadingZeroBits,
        ulong maximumNonce,
        CancellationToken cancellationToken)
    {
        Span<byte> nonceBytes = stackalloc byte[8];
        for (ulong nonce = 0;; nonce++)
        {
            if ((nonce & 0x0fffUL) == 0) cancellationToken.ThrowIfCancellationRequested();
            BinaryPrimitives.WriteUInt64BigEndian(nonceBytes, nonce);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(ProofOfWorkDomain);
            hash.AppendData(challengeId);
            hash.AppendData(challenge);
            hash.AppendData(nonceBytes);
            var digest = hash.GetHashAndReset();
            if (HasLeadingZeroBits(digest, leadingZeroBits)) return nonce;
            if (nonce == maximumNonce) break;
        }
        throw new InvalidOperationException(
            "No proof-of-work nonce exists inside the configured search bound.");
    }

    private static bool HasLeadingZeroBits(ReadOnlySpan<byte> digest, int bits)
    {
        var remaining = bits;
        foreach (var value in digest)
        {
            if (remaining <= 0) return true;
            var required = Math.Min(remaining, 8);
            if ((value >> (8 - required)) != 0) return false;
            remaining -= required;
        }
        return remaining <= 0;
    }

    private static void ValidateRouteEnrollment(ProductionMailboxRegistryIssueRequest request)
    {
        ValidateCommon(request);
        if (request.Intent != ProductionMailboxIssuanceIntent.LocalOwner ||
            !IsZeroRoute(request) || request.OwnerProofSignature.IsEmpty ||
            !request.RouteAdvertisement.IsEmpty || !request.EnrollmentHandle.IsEmpty)
            throw new ArgumentException("Registry route-enrollment request shape is invalid.", nameof(request));
    }

    private static void ValidateLocalOwnerIssuance(ProductionMailboxRegistryIssueRequest request)
    {
        ValidateCommon(request);
        if (request.Intent != ProductionMailboxIssuanceIntent.LocalOwner ||
            !IsZeroRoute(request) || request.OwnerProofSignature.IsEmpty ||
            request.RouteAdvertisement.Length != 400 || request.EnrollmentHandle.Length != 32)
            throw new ArgumentException("Registry LocalOwner issuance request shape is invalid.", nameof(request));
    }

    private static void ValidatePeerDepositIssuance(
        ProductionMailboxRegistryIssueRequest request,
        ReadOnlyMemory<byte> recipientEd25519PublicKey,
        ReadOnlyMemory<byte> canonicalRouteAdvertisement)
    {
        ValidateCommon(request);
        _ = Exact(recipientEd25519PublicKey, 32, "recipient Session key", true);
        _ = Exact(canonicalRouteAdvertisement, 400, "route advertisement", true);
        if (request.Intent != ProductionMailboxIssuanceIntent.PeerDeposit ||
            request.BlindedMailboxId.Length != 32 || IsZero(request.BlindedMailboxId.Span) ||
            request.BlindedPlacementId.Length != 32 || IsZero(request.BlindedPlacementId.Span) ||
            request.SelectionInputCommitment.Length != 32 || IsZero(request.SelectionInputCommitment.Span) ||
            !request.OwnerProofSignature.IsEmpty || request.RouteAdvertisement.Length != 400 ||
            !request.EnrollmentHandle.IsEmpty ||
            !request.RouteAdvertisement.Span.SequenceEqual(canonicalRouteAdvertisement.Span))
            throw new ArgumentException("Registry PeerDeposit issuance request shape is invalid.", nameof(request));
    }

    private static void ValidateCommon(ProductionMailboxRegistryIssueRequest request)
    {
        _ = Exact(request.HolderEd25519PublicKey, 32, "holder key", true);
        _ = Exact(request.MailboxOwnerEd25519PublicKey, 32, "mailbox owner key", true);
        _ = Exact(request.SigningCertificateSha256, 32, "signing certificate hash", true);
        _ = Exact(request.BuildArtifactSha256, 32, "build artifact hash", true);
        _ = Exact(request.IdempotencyKey, 32, "idempotency key", true);
        _ = Exact(request.EntitlementCommitment, 32, "entitlement commitment", false);
        _ = Exact(request.ChallengeId, 16, "challenge ID", true);
        _ = Exact(request.Challenge, 32, "challenge", true);
        _ = Exact(request.HolderProofSignature, 64, "holder proof", true);
        if (!request.OwnerProofSignature.IsEmpty)
            _ = Exact(request.OwnerProofSignature, 64, "owner proof", true);
        if (!request.RouteAdvertisement.IsEmpty)
            _ = Exact(request.RouteAdvertisement, 400, "route advertisement", true);
        if (!request.EnrollmentHandle.IsEmpty)
            _ = Exact(request.EnrollmentHandle, 32, "enrollment handle", true);
        if (!Enum.IsDefined(request.Intent) || !Enum.IsDefined(request.Platform))
            throw new ArgumentException("Registry request enum value is unsupported.", nameof(request));
        if (!IsZero(request.EntitlementCommitment.Span))
            throw new ArgumentException("Paid entitlement is not supported.", nameof(request));
        ValidateRouteField(request.BlindedMailboxId, "mailbox ID");
        ValidateRouteField(request.BlindedPlacementId, "placement ID");
        ValidateRouteField(request.SelectionInputCommitment, "selection commitment");
    }

    private static bool IsZeroRoute(ProductionMailboxRegistryIssueRequest request) =>
        IsZeroOrEmpty(request.BlindedMailboxId) &&
        IsZeroOrEmpty(request.BlindedPlacementId) &&
        IsZeroOrEmpty(request.SelectionInputCommitment);

    private static void ValidateRouteField(ReadOnlyMemory<byte> value, string name)
    {
        if (!value.IsEmpty && value.Length != 32)
            throw new ArgumentException($"Registry {name} length is invalid.");
    }

    private static void Binary(
        Utf8JsonWriter writer,
        string property,
        ReadOnlyMemory<byte> value,
        bool emptyWhenZero)
    {
        writer.WriteString(property,
            emptyWhenZero && IsZeroOrEmpty(value) ? "" : Base64Url(value.Span));
    }

    private static void OptionalBinary(
        Utf8JsonWriter writer,
        string property,
        ReadOnlyMemory<byte> value)
    {
        if (value.IsEmpty) writer.WriteNull(property);
        else writer.WriteString(property, Base64Url(value.Span));
    }

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Exact(
        ReadOnlyMemory<byte> value,
        int length,
        string name,
        bool nonzero)
    {
        if (value.Length != length || nonzero && IsZero(value.Span))
            throw new ArgumentException($"Registry {name} is invalid.");
        return value.ToArray();
    }

    private static bool IsZeroOrEmpty(ReadOnlyMemory<byte> value) =>
        value.IsEmpty || IsZero(value.Span);

    private static bool IsZero(ReadOnlySpan<byte> value) =>
        value.IndexOfAnyExcept((byte)0) < 0;
}
