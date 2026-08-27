using System.Security.Cryptography;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Protocol.DeepExtension.MailboxAuthority;
using Deep.Protocol.DeepExtension.MailboxCapabilities;
using Deep.Protocol.DeepExtension.MailboxTopology;

namespace Deep.Client.Maui.Services;

internal sealed record AcquiredProductionMailboxLocalOwner(
    ImportedProductionMailboxRuntimeMaterial Material,
    ReadOnlyMemory<byte> CanonicalRouteAdvertisement);

/// <summary>
/// Single-attempt orchestration for official production mailbox credentials. The Registry client
/// owns HTTP bounds; this type owns transcript construction, key-role separation and import.
/// </summary>
internal sealed class ProductionMailboxCredentialAcquirer
{
    internal const ulong MaximumChallengeLifetimeSeconds = 3_600;

    private static readonly byte[] ZeroRoute = new byte[32];

    private readonly ProductionMailboxRegistryClient registry;
    private readonly SqliteSessionStore store;
    private readonly SessionIdentityProvider sessionIdentity;
    private readonly ProductionMailboxOwnerIdentity ownerIdentity;
    private readonly ProductionMailboxTrustAnchor buildAnchor;
    private readonly IProductionMailboxTrustStateStore trustStateStore;
    private readonly ProductionMailboxClientApprovalIdentity clientIdentity;
    private readonly ProductionMailboxClientPlatform platform;
    private readonly TimeProvider timeProvider;

    public ProductionMailboxCredentialAcquirer(
        ProductionMailboxRegistryClient registry,
        SqliteSessionStore store,
        SessionIdentityProvider sessionIdentity,
        ProductionMailboxOwnerIdentity ownerIdentity,
        ProductionMailboxTrustAnchor buildAnchor,
        IProductionMailboxTrustStateStore trustStateStore,
        ProductionMailboxClientApprovalIdentity clientIdentity,
        TimeProvider? timeProvider = null)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.sessionIdentity = sessionIdentity ??
            throw new ArgumentNullException(nameof(sessionIdentity));
        this.ownerIdentity = ownerIdentity ??
            throw new ArgumentNullException(nameof(ownerIdentity));
        this.buildAnchor = buildAnchor ?? throw new ArgumentNullException(nameof(buildAnchor));
        this.trustStateStore = trustStateStore ??
            throw new ArgumentNullException(nameof(trustStateStore));
        this.clientIdentity = FreezeAndValidateClientIdentity(clientIdentity);
        platform = ToProtocolPlatform(this.clientIdentity.Platform);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        ValidateBuildAnchor(buildAnchor);
    }

    public async Task<AcquiredProductionMailboxLocalOwner> AcquireLocalOwnerAsync(
        CancellationToken cancellationToken = default)
    {
        var holderKey = sessionIdentity.GetEd25519PublicKey();
        var ownerKey = ownerIdentity.GetPublicKey();
        byte[]? idempotency = null;
        byte[]? canonicalAdvertisement = null;
        try
        {
            var enrollmentChallenge = await registry.CreateChallengeAsync(cancellationToken)
                .ConfigureAwait(false);
            var enrollmentAuthority = ValidateChallenge(enrollmentChallenge);
            idempotency = ComputeIdempotency(
                enrollmentChallenge, holderKey, ownerKey,
                ProductionMailboxIssuanceIntent.LocalOwner,
                ZeroRoute, ZeroRoute, ZeroRoute);
            var enrollmentRequest = await CreateSignedRequestAsync(
                enrollmentChallenge,
                enrollmentAuthority,
                holderKey,
                ownerKey,
                idempotency,
                ProductionMailboxIssuanceIntent.LocalOwner,
                ZeroRoute,
                ZeroRoute,
                ZeroRoute,
                includeOwnerProof: true,
                ReadOnlyMemory<byte>.Empty,
                ReadOnlyMemory<byte>.Empty,
                cancellationToken).ConfigureAwait(false);

            var enrollment = await registry.SubmitRouteEnrollmentAsync(
                    enrollmentRequest, cancellationToken)
                .ConfigureAwait(false);
            var certificate = ValidateEnrollment(
                enrollment, enrollmentChallenge, enrollmentAuthority,
                holderKey, ownerKey, idempotency);
            canonicalAdvertisement = CreateInitialAdvertisement(certificate, ownerKey);

            var issuanceChallenge = await registry.CreateChallengeAsync(cancellationToken)
                .ConfigureAwait(false);
            var issuanceAuthority = ValidateChallenge(issuanceChallenge);
            RequireSameClosure(enrollmentChallenge, issuanceChallenge);
            RequireSameAuthority(enrollmentAuthority, issuanceAuthority);

            var issuanceIdempotency = ComputeIdempotency(
                issuanceChallenge, holderKey, ownerKey,
                ProductionMailboxIssuanceIntent.LocalOwner,
                ZeroRoute, ZeroRoute, ZeroRoute);
            try
            {
                Equal(issuanceIdempotency, idempotency,
                    "Registry enrollment closure changed before LocalOwner issuance.");
                var issuanceRequest = await CreateSignedRequestAsync(
                    issuanceChallenge,
                    issuanceAuthority,
                    holderKey,
                    ownerKey,
                    issuanceIdempotency,
                    ProductionMailboxIssuanceIntent.LocalOwner,
                    ZeroRoute,
                    ZeroRoute,
                    ZeroRoute,
                    includeOwnerProof: true,
                    canonicalAdvertisement,
                    enrollment.EnrollmentHandle,
                    cancellationToken).ConfigureAwait(false);
                var bundle = await registry.SubmitLocalOwnerIssuanceAsync(
                        issuanceRequest, cancellationToken)
                    .ConfigureAwait(false);
                ValidateLocalOwnerResponse(
                    bundle, issuanceChallenge, holderKey, ownerKey,
                    issuanceIdempotency, enrollment, canonicalAdvertisement);
                var imported = await ProductionMailboxCredentialBundleImporter
                    .ImportLocalOwnerAsync(
                        store,
                        new MailboxHolderIdentity(sessionIdentity.SessionId, holderKey),
                        bundle,
                        ownerKey,
                        buildAnchor,
                        trustStateStore,
                        clientIdentity,
                        MailboxInfrastructureOwnership.OfficialManaged,
                        timeProvider,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new AcquiredProductionMailboxLocalOwner(
                    imported, canonicalAdvertisement.ToArray());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(issuanceIdempotency);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(holderKey);
            CryptographicOperations.ZeroMemory(ownerKey);
            if (idempotency is not null) CryptographicOperations.ZeroMemory(idempotency);
            if (canonicalAdvertisement is not null)
                CryptographicOperations.ZeroMemory(canonicalAdvertisement);
        }
    }

    public async Task<ImportedProductionMailboxPeerDepositMaterial> AcquirePeerDepositAsync(
        VerifiedContactMailboxInvitation invitation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        if (invitation.SessionId == sessionIdentity.SessionId)
            throw new InvalidOperationException(
                "A production peer deposit cannot target the active Session identity.");

        var holderKey = sessionIdentity.GetEd25519PublicKey();
        var recipientKey = invitation.SessionEd25519PublicKey.ToArray();
        var ownerKey = invitation.MailboxOwnerEd25519PublicKey.ToArray();
        var advertisementBytes = invitation.CanonicalRouteAdvertisement.ToArray();
        byte[]? idempotency = null;
        try
        {
            var advertisement = ProductionMailboxRouteAdvertisementCodec.DecodeAdvertisement(
                advertisementBytes);
            Equal(
                ProductionMailboxRouteAdvertisementCodec.EncodeAdvertisement(advertisement),
                advertisementBytes,
                "Authenticated contact PRA1 is not canonical.");
            Equal(advertisement.Certificate.MailboxOwnerEd25519PublicKey.Span, ownerKey,
                "Authenticated contact PRA1 changed its mailbox owner.");

            var challenge = await registry.CreateChallengeAsync(cancellationToken)
                .ConfigureAwait(false);
            var authority = ValidateChallenge(challenge);
            ValidatePeerRoute(advertisement, authority);
            idempotency = ComputeIdempotency(
                challenge,
                holderKey,
                ownerKey,
                ProductionMailboxIssuanceIntent.PeerDeposit,
                advertisement.Certificate.BlindedMailboxId.Span,
                advertisement.Certificate.BlindedPlacementId.Span,
                advertisement.Certificate.SelectionInputCommitment.Span);
            var request = await CreateSignedRequestAsync(
                challenge,
                authority,
                holderKey,
                ownerKey,
                idempotency,
                ProductionMailboxIssuanceIntent.PeerDeposit,
                advertisement.Certificate.BlindedMailboxId,
                advertisement.Certificate.BlindedPlacementId,
                advertisement.Certificate.SelectionInputCommitment,
                includeOwnerProof: false,
                advertisementBytes,
                ReadOnlyMemory<byte>.Empty,
                cancellationToken).ConfigureAwait(false);
            var bundle = await registry.SubmitPeerDepositIssuanceAsync(
                    request, recipientKey, advertisementBytes, cancellationToken)
                .ConfigureAwait(false);
            ValidatePeerResponse(
                bundle, challenge, holderKey, recipientKey, ownerKey,
                idempotency, advertisement);
            return await ProductionMailboxCredentialBundleImporter.ImportPeerDepositAsync(
                    store,
                    new MailboxHolderIdentity(sessionIdentity.SessionId, holderKey),
                    invitation.SessionId,
                    ownerKey,
                    bundle,
                    buildAnchor,
                    trustStateStore,
                    clientIdentity,
                    MailboxInfrastructureOwnership.OfficialManaged,
                    timeProvider,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(holderKey);
            CryptographicOperations.ZeroMemory(recipientKey);
            CryptographicOperations.ZeroMemory(ownerKey);
            CryptographicOperations.ZeroMemory(advertisementBytes);
            if (idempotency is not null) CryptographicOperations.ZeroMemory(idempotency);
        }
    }

    private async Task<ProductionMailboxRegistryIssueRequest> CreateSignedRequestAsync(
        ProductionMailboxRegistryChallenge challenge,
        ProductionMailboxAuthority authority,
        byte[] holderKey,
        byte[] ownerKey,
        byte[] idempotency,
        ProductionMailboxIssuanceIntent intent,
        ReadOnlyMemory<byte> mailbox,
        ReadOnlyMemory<byte> placement,
        ReadOnlyMemory<byte> selection,
        bool includeOwnerProof,
        ReadOnlyMemory<byte> routeAdvertisement,
        ReadOnlyMemory<byte> enrollmentHandle,
        CancellationToken cancellationToken)
    {
        var nonce = await ProductionMailboxRegistryClient.SolveProofOfWorkAsync(
                challenge, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        _ = ValidateChallenge(challenge);
        var input = new ProductionMailboxHolderProofInput
        {
            Intent = intent,
            Platform = platform,
            NetworkId = authority.NetworkId.ToArray(),
            CanonicalAuthorityHash = challenge.Authority.Sha256.ToArray(),
            HolderEd25519PublicKey = holderKey,
            MailboxOwnerEd25519PublicKey = ownerKey,
            BlindedMailboxId = mailbox.ToArray(),
            BlindedPlacementId = placement.ToArray(),
            SelectionInputCommitment = selection.ToArray(),
            SigningCertificateSha256 = clientIdentity.SigningCertificateSha256.ToArray(),
            BuildArtifactSha256 = clientIdentity.BuildArtifactSha256.ToArray(),
            IdempotencyKey = idempotency,
            EntitlementCommitment = new byte[32],
            ChallengeId = challenge.ChallengeId.ToArray(),
            Challenge = challenge.Challenge.ToArray(),
            ProofOfWorkNonce = nonce
        };
        var signingBytes = ProductionMailboxHolderProof.GetSigningBytes(input);
        var holderSignature = sessionIdentity.SignDetached(signingBytes);
        var ownerSignature = includeOwnerProof
            ? ownerIdentity.SignLocalOwnerProof(input)
            : Array.Empty<byte>();
        try
        {
            return new ProductionMailboxRegistryIssueRequest(
                holderKey.ToArray(),
                ownerKey.ToArray(),
                intent,
                platform,
                clientIdentity.SigningCertificateSha256.ToArray(),
                clientIdentity.BuildArtifactSha256.ToArray(),
                idempotency.ToArray(),
                new byte[32],
                mailbox.ToArray(),
                placement.ToArray(),
                selection.ToArray(),
                challenge.ChallengeId.ToArray(),
                challenge.Challenge.ToArray(),
                nonce,
                holderSignature.ToArray(),
                ownerSignature.ToArray(),
                routeAdvertisement.ToArray(),
                enrollmentHandle.ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingBytes);
            CryptographicOperations.ZeroMemory(holderSignature);
            CryptographicOperations.ZeroMemory(ownerSignature);
        }
    }

    private ProductionMailboxAuthority ValidateChallenge(
        ProductionMailboxRegistryChallenge challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        var now = Now();
        if (challenge.ExpiresAtUnixSeconds <= now ||
            challenge.ExpiresAtUnixSeconds - now > MaximumChallengeLifetimeSeconds)
            throw new InvalidDataException(
                "Registry challenge is expired or exceeds the supported freshness window.");
        ValidateArtifact(challenge.Authority);
        ValidateArtifact(challenge.Revocation);
        ValidateArtifact(challenge.Topology);

        var authority = ProductionMailboxAuthorityCodec.Decode(
            challenge.Authority.Canonical.Span);
        Equal(ProductionMailboxAuthorityCodec.Encode(authority),
            challenge.Authority.Canonical.Span,
            "Registry authority artifact is not canonical.");
        if (authority.DevelopmentOnly ||
            authority.Environment != ProductionMailboxAuthorityEnvironment.Production ||
            authority.Ownership != ProductionMailboxAuthorityOwnership.OfficialManaged)
            throw new InvalidDataException(
                "Registry challenge does not carry an official production authority.");
        Equal(authority.NetworkId.Span, buildAnchor.NetworkId.Span,
            "Registry challenge belongs to another production network.");
        return authority;
    }

    private ProductionMailboxRouteCertificate ValidateEnrollment(
        ProductionMailboxRegistryRouteEnrollment enrollment,
        ProductionMailboxRegistryChallenge challenge,
        ProductionMailboxAuthority authority,
        ReadOnlySpan<byte> holderKey,
        ReadOnlySpan<byte> ownerKey,
        ReadOnlySpan<byte> idempotency)
    {
        ArgumentNullException.ThrowIfNull(enrollment);
        Equal(enrollment.IdempotencyKey.Span, idempotency,
            "Registry route enrollment changed the idempotency key.");
        Equal(enrollment.HolderEd25519PublicKey.Span, holderKey,
            "Registry route enrollment changed the holder.");
        Equal(enrollment.MailboxOwnerEd25519PublicKey.Span, ownerKey,
            "Registry route enrollment changed the mailbox owner.");
        ValidateRouteFields(
            enrollment.BlindedMailboxId.Span,
            enrollment.BlindedPlacementId.Span,
            enrollment.SelectionInputCommitment.Span);
        ValidateControlPlaneClosure(enrollment.ControlPlane, challenge);
        var now = Now();
        if (enrollment.IssuedAtUnixSeconds == 0 ||
            enrollment.IssuedAtUnixSeconds > checked(now +
                ProductionMailboxRouteAdvertisementConstants.MaximumClockSkewSeconds) ||
            enrollment.ExpiresAtUnixSeconds <= now ||
            enrollment.ExpiresAtUnixSeconds - enrollment.IssuedAtUnixSeconds >
                ProductionMailboxRouteAdvertisementConstants.MaximumLifetimeSeconds)
            throw new InvalidDataException(
                "Registry route enrollment validity window is unsafe.");

        var certificateBytes = enrollment.CanonicalRouteCertificate.ToArray();
        var certificateHash = SHA256.HashData(certificateBytes);
        try
        {
            Equal(certificateHash, enrollment.RouteCertificateSha256.Span,
                "Registry route certificate hash is invalid.");
            var certificate = ProductionMailboxRouteAdvertisementCodec.DecodeCertificate(
                certificateBytes);
            Equal(ProductionMailboxRouteAdvertisementCodec.EncodeCertificate(certificate),
                certificateBytes,
                "Registry route certificate is not canonical.");
            Equal(certificate.NetworkId.Span, authority.NetworkId.Span,
                "Registry route certificate network is invalid.");
            Equal(certificate.CanonicalAuthorityHash.Span,
                challenge.Authority.Sha256.Span,
                "Registry route certificate authority hash is invalid.");
            Equal(certificate.IssuerEd25519PublicKey.Span,
                authority.MailboxIssuerEd25519PublicKey.Span,
                "Registry route certificate issuer is invalid.");
            Equal(certificate.MailboxOwnerEd25519PublicKey.Span, ownerKey,
                "Registry route certificate owner is invalid.");
            Equal(certificate.BlindedMailboxId.Span,
                enrollment.BlindedMailboxId.Span,
                "Registry route certificate mailbox binding is invalid.");
            Equal(certificate.BlindedPlacementId.Span,
                enrollment.BlindedPlacementId.Span,
                "Registry route certificate placement binding is invalid.");
            Equal(certificate.SelectionInputCommitment.Span,
                enrollment.SelectionInputCommitment.Span,
                "Registry route certificate selection binding is invalid.");
            if (certificate.AuthorityGeneration != authority.AuthorityGeneration ||
                certificate.IssuedAtUnixSeconds != enrollment.IssuedAtUnixSeconds ||
                certificate.ExpiresAtUnixSeconds != enrollment.ExpiresAtUnixSeconds)
                throw new InvalidDataException(
                    "Registry route certificate validity binding is invalid.");
            var signingBytes = ProductionMailboxRouteAdvertisementCodec
                .GetCertificateSigningBytes(certificate);
            try
            {
                if (!new SodiumProductionMailboxRouteSignatureVerifier().Verify(
                        certificate.IssuerEd25519PublicKey.Span,
                        signingBytes,
                        certificate.IssuerSignature.Span))
                    throw new InvalidDataException(
                        "Registry route certificate issuer signature is invalid.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(signingBytes);
            }
            return certificate;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(certificateBytes);
            CryptographicOperations.ZeroMemory(certificateHash);
        }
    }

    private byte[] CreateInitialAdvertisement(
        ProductionMailboxRouteCertificate certificate,
        ReadOnlySpan<byte> ownerKey)
    {
        Equal(certificate.MailboxOwnerEd25519PublicKey.Span, ownerKey,
            "Registry route certificate owner changed before PRA1 authoring.");
        var now = Now();
        var publishedAt = Math.Max(now, certificate.IssuedAtUnixSeconds);
        var expiresAt = Math.Min(
            certificate.ExpiresAtUnixSeconds,
            checked(publishedAt +
                ProductionMailboxRouteAdvertisementConstants.MaximumLifetimeSeconds));
        if (expiresAt <= publishedAt)
            throw new InvalidDataException(
                "Registry route certificate has no safe PRA1 validity window.");
        var draft = new ProductionMailboxRouteAdvertisement
        {
            Certificate = certificate,
            Sequence = 1,
            PublishedAtUnixSeconds = publishedAt,
            ExpiresAtUnixSeconds = expiresAt,
            OwnerSignature = new byte[
                ProductionMailboxRouteAdvertisementConstants.Ed25519SignatureLength]
        };
        var signature = ownerIdentity.SignRouteAdvertisement(draft);
        try
        {
            var advertisement = draft with { OwnerSignature = signature.ToArray() };
            return ProductionMailboxRouteAdvertisementCodec.EncodeAdvertisement(advertisement);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static void ValidateLocalOwnerResponse(
        ProductionMailboxLocalOwnerBundle bundle,
        ProductionMailboxRegistryChallenge challenge,
        ReadOnlySpan<byte> holderKey,
        ReadOnlySpan<byte> ownerKey,
        ReadOnlySpan<byte> idempotency,
        ProductionMailboxRegistryRouteEnrollment enrollment,
        ReadOnlySpan<byte> advertisement)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        Equal(bundle.HolderEd25519PublicKey.Span, holderKey,
            "Registry LocalOwner response changed the holder.");
        Equal(bundle.MailboxOwnerEd25519PublicKey.Span, ownerKey,
            "Registry LocalOwner response changed the owner.");
        Equal(bundle.IdempotencyKey.Span, idempotency,
            "Registry LocalOwner response changed the idempotency key.");
        Equal(bundle.BlindedMailboxId.Span, enrollment.BlindedMailboxId.Span,
            "Registry LocalOwner response changed the mailbox route.");
        Equal(bundle.BlindedPlacementId.Span, enrollment.BlindedPlacementId.Span,
            "Registry LocalOwner response changed the placement route.");
        Equal(bundle.SelectionInputCommitment.Span,
            enrollment.SelectionInputCommitment.Span,
            "Registry LocalOwner response changed the selection route.");
        Equal(bundle.CanonicalRouteCertificate.Span,
            enrollment.CanonicalRouteCertificate.Span,
            "Registry LocalOwner response changed the enrolled PRC1.");
        Equal(bundle.CanonicalRouteAdvertisement.Span, advertisement,
            "Registry LocalOwner response changed the owner-signed PRA1.");
        ValidateControlPlaneClosure(bundle.ControlPlane, challenge);
    }

    private static void ValidatePeerResponse(
        ProductionMailboxPeerDepositBundle bundle,
        ProductionMailboxRegistryChallenge challenge,
        ReadOnlySpan<byte> holderKey,
        ReadOnlySpan<byte> recipientKey,
        ReadOnlySpan<byte> ownerKey,
        ReadOnlySpan<byte> idempotency,
        ProductionMailboxRouteAdvertisement advertisement)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        Equal(bundle.HolderEd25519PublicKey.Span, holderKey,
            "Registry PeerDeposit response changed the holder.");
        Equal(bundle.RecipientEd25519PublicKey.Span, recipientKey,
            "Registry PeerDeposit response changed the authenticated recipient.");
        Equal(bundle.MailboxOwnerEd25519PublicKey.Span, ownerKey,
            "Registry PeerDeposit response changed the recipient owner.");
        Equal(bundle.IdempotencyKey.Span, idempotency,
            "Registry PeerDeposit response changed the idempotency key.");
        Equal(bundle.BlindedMailboxId.Span,
            advertisement.Certificate.BlindedMailboxId.Span,
            "Registry PeerDeposit response changed the mailbox route.");
        Equal(bundle.BlindedPlacementId.Span,
            advertisement.Certificate.BlindedPlacementId.Span,
            "Registry PeerDeposit response changed the placement route.");
        Equal(bundle.SelectionInputCommitment.Span,
            advertisement.Certificate.SelectionInputCommitment.Span,
            "Registry PeerDeposit response changed the selection route.");
        Equal(bundle.CanonicalRouteAdvertisement.Span,
            ProductionMailboxRouteAdvertisementCodec.EncodeAdvertisement(advertisement),
            "Registry PeerDeposit response changed the authenticated PRA1.");
        ValidateControlPlaneClosure(bundle.ControlPlane, challenge);
    }

    private static void ValidatePeerRoute(
        ProductionMailboxRouteAdvertisement advertisement,
        ProductionMailboxAuthority authority)
    {
        Equal(advertisement.Certificate.NetworkId.Span, authority.NetworkId.Span,
            "Authenticated contact PRA1 belongs to another network.");
        Equal(advertisement.Certificate.CanonicalAuthorityHash.Span,
            SHA256.HashData(ProductionMailboxAuthorityCodec.Encode(authority)),
            "Authenticated contact PRA1 belongs to another authority closure.");
        ValidateRouteFields(
            advertisement.Certificate.BlindedMailboxId.Span,
            advertisement.Certificate.BlindedPlacementId.Span,
            advertisement.Certificate.SelectionInputCommitment.Span);
    }

    private static void ValidateControlPlaneClosure(
        ProductionMailboxControlPlaneArtifacts controlPlane,
        ProductionMailboxRegistryChallenge challenge)
    {
        ArgumentNullException.ThrowIfNull(controlPlane);
        Equal(controlPlane.CanonicalAuthority.Span, challenge.Authority.Canonical.Span,
            "Registry response authority differs from its challenge closure.");
        Equal(controlPlane.CanonicalRevocationSnapshot.Span,
            challenge.Revocation.Canonical.Span,
            "Registry response revocation differs from its challenge closure.");
        Equal(controlPlane.CanonicalTopology.Span, challenge.Topology.Canonical.Span,
            "Registry response topology differs from its challenge closure.");
    }

    private static void RequireSameClosure(
        ProductionMailboxRegistryChallenge first,
        ProductionMailboxRegistryChallenge second)
    {
        Equal(first.Authority.Sha256.Span, second.Authority.Sha256.Span,
            "Registry authority rotated during route enrollment.");
        Equal(first.Revocation.Sha256.Span, second.Revocation.Sha256.Span,
            "Registry revocation snapshot rotated during route enrollment.");
        Equal(first.Topology.Sha256.Span, second.Topology.Sha256.Span,
            "Registry topology rotated during route enrollment.");
    }

    private static void RequireSameAuthority(
        ProductionMailboxAuthority first,
        ProductionMailboxAuthority second)
    {
        Equal(ProductionMailboxAuthorityCodec.Encode(first),
            ProductionMailboxAuthorityCodec.Encode(second),
            "Registry authority changed during route enrollment.");
    }

    private static void ValidateArtifact(ProductionMailboxRegistryArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var digest = SHA256.HashData(artifact.Canonical.Span);
        try
        {
            Equal(digest, artifact.Sha256.Span,
                "Registry challenge artifact hash is invalid.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private byte[] ComputeIdempotency(
        ProductionMailboxRegistryChallenge challenge,
        ReadOnlySpan<byte> holderKey,
        ReadOnlySpan<byte> ownerKey,
        ProductionMailboxIssuanceIntent intent,
        ReadOnlySpan<byte> mailbox,
        ReadOnlySpan<byte> placement,
        ReadOnlySpan<byte> selection) =>
        ProductionMailboxIssuanceIdempotency.Compute(
            challenge.Authority.Sha256.Span,
            challenge.Revocation.Sha256.Span,
            challenge.Topology.Sha256.Span,
            holderKey,
            ownerKey,
            mailbox,
            placement,
            selection,
            intent,
            platform,
            clientIdentity.SigningCertificateSha256.Span,
            clientIdentity.BuildArtifactSha256.Span,
            ZeroRoute);

    private static void ValidateRouteFields(
        ReadOnlySpan<byte> mailbox,
        ReadOnlySpan<byte> placement,
        ReadOnlySpan<byte> selection)
    {
        Nonzero32(mailbox, "mailbox route");
        Nonzero32(placement, "placement route");
        Nonzero32(selection, "selection route");
        Equal(
            ProductionMailboxReplicaSelection.ComputeSelectionInputCommitment(
                new BlindedPlacementId(placement)),
            selection,
            "Production mailbox selection commitment is invalid.");
    }

    private static ProductionMailboxClientApprovalIdentity FreezeAndValidateClientIdentity(
        ProductionMailboxClientApprovalIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        Nonzero32(identity.SigningCertificateSha256.Span, "signing certificate hash");
        Nonzero32(identity.BuildArtifactSha256.Span, "build artifact hash");
        var expectedApplication = identity.Platform switch
        {
            MailboxClientPlatform.Android =>
                ProductionMailboxControlPlaneVerifier.AndroidApplicationIdentity,
            MailboxClientPlatform.Windows =>
                ProductionMailboxControlPlaneVerifier.WindowsApplicationIdentity,
            _ => throw new InvalidDataException(
                "Production mailbox client platform is unsupported.")
        };
        if (!string.Equals(identity.ApplicationIdentity, expectedApplication,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "Production mailbox application identity is invalid.");
        return identity with
        {
            SigningCertificateSha256 = identity.SigningCertificateSha256.ToArray(),
            BuildArtifactSha256 = identity.BuildArtifactSha256.ToArray()
        };
    }

    private static void ValidateBuildAnchor(ProductionMailboxTrustAnchor anchor)
    {
        Nonzero32(anchor.MrXPublicKeySha256.Span, "Mr. X trust hash");
        if (anchor.NetworkId.Length != 16 ||
            anchor.NetworkId.Span.IndexOfAnyExcept((byte)0) < 0 ||
            anchor.AuthorityGeneration == 0 ||
            anchor.RevocationGeneration == 0 ||
            anchor.TopologyGeneration == 0)
            throw new InvalidDataException(
                "Production mailbox build trust anchor is incomplete.");
        Nonzero32(anchor.AuthorityHash.Span, "authority trust hash");
        Nonzero32(anchor.RevocationHeadHash.Span, "revocation head hash");
        Nonzero32(anchor.RevocationSnapshotHash.Span, "revocation snapshot hash");
        Nonzero32(anchor.TopologyHash.Span, "topology trust hash");
    }

    private static ProductionMailboxClientPlatform ToProtocolPlatform(
        MailboxClientPlatform value) => value switch
        {
            MailboxClientPlatform.Android => ProductionMailboxClientPlatform.Android,
            MailboxClientPlatform.Windows => ProductionMailboxClientPlatform.Windows,
            _ => throw new InvalidDataException(
                "Production mailbox client platform is unsupported.")
        };

    private ulong Now()
    {
        var value = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        if (value <= 0)
            throw new InvalidDataException("Production mailbox clock is invalid.");
        return checked((ulong)value);
    }

    private static void Nonzero32(ReadOnlySpan<byte> value, string name)
    {
        if (value.Length != 32 || value.IndexOfAnyExcept((byte)0) < 0)
            throw new InvalidDataException($"Production mailbox {name} is invalid.");
    }

    private static void Equal(
        ReadOnlySpan<byte> actual,
        ReadOnlySpan<byte> expected,
        string message)
    {
        if (actual.Length != expected.Length ||
            !CryptographicOperations.FixedTimeEquals(actual, expected))
            throw new InvalidDataException(message);
    }
}
