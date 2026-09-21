using System.Security.Cryptography;
using Deep.Client.Shared.Services.ContactV1;
using Deep.Client.Shared.Services.MessagingV1;
using Deep.Protocol.ContactV1;

namespace Deep.Client.Maui.Services;

/// <summary>
/// Binds one verifier-minted route proposal, the protected metadata-sealing
/// key, and the current-device custody signer before XRA1 authoring. Network
/// publication and threshold completion remain separate explicit steps.
/// </summary>
internal sealed class AccountOwnedContactRouteAdvertisementAuthor
{
    private readonly DeepAccountRuntimeAccessor accounts;
    private readonly DeepContactResolveRuntimeAccessor contactResolve;
    private readonly ContactRouteAuthorityClient authorityClient;
    private readonly ContactPublicationAuthorityClient publicationAuthorityClient;

    internal AccountOwnedContactRouteAdvertisementAuthor(
        DeepAccountRuntimeAccessor accounts,
        DeepContactResolveRuntimeAccessor contactResolve,
        ContactRouteAuthorityClient authorityClient,
        ContactPublicationAuthorityClient publicationAuthorityClient)
    {
        this.accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        this.contactResolve = contactResolve ??
            throw new ArgumentNullException(nameof(contactResolve));
        this.authorityClient = authorityClient ??
            throw new ArgumentNullException(nameof(authorityClient));
        this.publicationAuthorityClient = publicationAuthorityClient ??
            throw new ArgumentNullException(nameof(publicationAuthorityClient));
    }

    internal async ValueTask<AuthoredContactRouteAdvertisement> AuthorGenesisAsync(
        VerifiedContactRouteProposalAuthority proposal,
        uint maximumAcceptedHellos,
        ReadOnlyMemory<byte> antiSpamPolicyHash,
        ulong issuedAtUnixSeconds,
        ulong expiresAtUnixSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var signer = await accounts.TryGetContactDeviceCustodySignerAsync(
                cancellationToken)
            .ConfigureAwait(false) ?? throw new CryptographicException(
                "XRA1 authoring requires the current device custody signer.");
        var key = await accounts.PrepareContactMetadataSealingKeyAsync(
                proposal, cancellationToken)
            .ConfigureAwait(false);
        var request = new ContactRouteAdvertisementAuthoringRequest(
            proposal, maximumAcceptedHellos, antiSpamPolicyHash.Span,
            key.KeyId.Span, key.X25519PublicKey.Span,
            issuedAtUnixSeconds, expiresAtUnixSeconds);
        return await ContactRouteAdvertisementAuthor.AuthorGenesisAsync(
            request, signer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Authors XRA1 with the protected local metadata key, obtains the exact
    /// threshold route from Registry, verifies it, then signs XRR1/XIR1 with
    /// the same current device custody authority. No route is persisted or
    /// exposed before the complete closure verifies.
    /// </summary>
    internal async ValueTask<AuthoredPermanentContactRoute>
        AuthorAndCompleteGenesisAsync(
            VerifiedContactRouteProposalAuthority proposal,
            uint maximumAcceptedHellos,
            ReadOnlyMemory<byte> antiSpamPolicyHash,
            ulong issuedAtUnixSeconds,
            ulong expiresAtUnixSeconds,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var signer = await accounts.TryGetContactDeviceCustodySignerAsync(
                cancellationToken)
            .ConfigureAwait(false) ?? throw new CryptographicException(
                "Route completion requires the current device custody signer.");
        var key = await accounts.PrepareContactMetadataSealingKeyAsync(
                proposal, cancellationToken)
            .ConfigureAwait(false);
        var advertisement = await ContactRouteAdvertisementAuthor.AuthorGenesisAsync(
                new ContactRouteAdvertisementAuthoringRequest(
                    proposal, maximumAcceptedHellos, antiSpamPolicyHash.Span,
                    key.KeyId.Span, key.X25519PublicKey.Span,
                    issuedAtUnixSeconds, expiresAtUnixSeconds),
                signer,
                cancellationToken)
            .ConfigureAwait(false);
        var threshold = await authorityClient.IssueAsync(
                proposal, advertisement, cancellationToken)
            .ConfigureAwait(false);
        return await ContactRouteCompletionAuthor.AuthorAsync(
                new ContactRouteCompletionAuthoringRequest(
                    proposal, advertisement, threshold),
                signer,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Device-authorizes one verified generation-zero DCR1/XPU1 body, obtains
    /// threshold XPA1 and independently re-verifies the exact returned XPU1.
    /// The caller must durably stage and publish the returned exact bytes; this
    /// method intentionally does not turn an in-memory result into a liveness claim.
    /// </summary>
    internal async ValueTask<AuthoredPermanentAddressPublication>
        AuthorizeGenesisPublicationAsync(
            AuthoredPermanentContactPublication publication,
            AuthoredPermanentContactRoute route,
            ulong issuedAtUnixSeconds,
            ulong expiresAtUnixSeconds,
            ulong effectiveExpiresAtUnixSeconds,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        ArgumentNullException.ThrowIfNull(route);
        if (publication.Generation != 0)
            throw new ArgumentException(
                "Genesis publication authorization requires generation zero.",
                nameof(publication));
        var signer = await accounts.TryGetContactDeviceCustodySignerAsync(
                cancellationToken)
            .ConfigureAwait(false) ?? throw new CryptographicException(
                "Contact publication requires the current device custody signer.");
        var requestNonce = RandomNonZero32();
        var operationId = RandomNonZero32();
        var ownerRetrieveCapability = RandomNonZero32();
        try
        {
            var authoredRequest = await ContactPublicationAuthorityAuthor.AuthorAsync(
                    publication,
                    route,
                    signer,
                    requestNonce,
                    operationId,
                    new byte[32],
                    issuedAtUnixSeconds,
                    expiresAtUnixSeconds,
                    effectiveExpiresAtUnixSeconds,
                    ownerRetrieveCapability,
                    cancellationToken)
                .ConfigureAwait(false);
            return await publicationAuthorityClient.IssueAsync(
                    authoredRequest, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(requestNonce);
            CryptographicOperations.ZeroMemory(operationId);
            CryptographicOperations.ZeroMemory(ownerRetrieveCapability);
        }
    }

    /// <summary>
    /// Completes the clean generation-zero ContactV1 publication: current
    /// proposal, threshold route, XPS1/DCB1/DCR1, durable XPI1/XPP1 quorum and
    /// crash-safe XPU1 publication. No raw authority or private key enters the
    /// host composition.
    /// </summary>
    internal async ValueTask<AuthoredGenesisContactReleaseState>
        PublishGenesisAsync(
            uint maximumAcceptedHellos,
            ReadOnlyMemory<byte> antiSpamPolicyHash,
            ushort oneTimePreKeyCount = 32,
            ushort lastResortReuseLimit = 8,
            string profileName = "",
            CancellationToken cancellationToken = default)
    {
        var proposal = await contactResolve.MintLocalRouteProposalAsync(cancellationToken)
            .ConfigureAwait(false);
        var issuedAt = proposal.TrustedLowerUnixSeconds;
        var expiresAt = Math.Min(
            proposal.ExpiresAtUnixSeconds,
            checked(issuedAt + 86_400));
        if (issuedAt < proposal.NotBeforeUnixSeconds ||
            expiresAt <= proposal.TrustedUpperUnixSeconds)
        {
            throw new CryptographicException(
                "The current route proposal has no usable generation-zero publication window.");
        }

        var signer = await accounts.TryGetContactDeviceCustodySignerAsync(
                cancellationToken)
            .ConfigureAwait(false) ?? throw new CryptographicException(
                "Genesis ContactV1 publication requires the current device custody signer.");
        var key = await accounts.PrepareContactMetadataSealingKeyAsync(
                proposal,
                cancellationToken)
            .ConfigureAwait(false);
        var advertisement = await ContactRouteAdvertisementAuthor.AuthorGenesisAsync(
                new ContactRouteAdvertisementAuthoringRequest(
                    proposal,
                    maximumAcceptedHellos,
                    antiSpamPolicyHash.Span,
                    key.KeyId.Span,
                    key.X25519PublicKey.Span,
                    issuedAt,
                    expiresAt),
                signer,
                cancellationToken)
            .ConfigureAwait(false);
        var threshold = await authorityClient.IssueAsync(
                proposal,
                advertisement,
                cancellationToken)
            .ConfigureAwait(false);
        var route = await ContactRouteCompletionAuthor.AuthorAsync(
                new ContactRouteCompletionAuthoringRequest(
                    proposal,
                    advertisement,
                    threshold),
                signer,
                cancellationToken)
            .ConfigureAwait(false);
        var preKeyService = await proposal.AuthorPreKeyServiceAsync(
                oneTimePreKeyCount,
                lastResortReuseLimit,
                issuedAt,
                expiresAt,
                signer,
                cancellationToken)
            .ConfigureAwait(false);
        var contact = await proposal.AuthorGenesisContactPublicationAsync(
                route,
                preKeyService,
                issuedAt,
                expiresAt,
                signer,
                profileName,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var address = await AuthorizeGenesisPublicationCoreAsync(
                contact,
                route,
                signer,
                issuedAt,
                expiresAt,
                expiresAt,
                cancellationToken)
            .ConfigureAwait(false);

        var inventoryOperationId = RandomNonZero32();
        try
        {
            var inventoryPlacement = route.Verified.Authority
                .CreatePreKeyInventoryPlacement(preKeyService);
            var inventory = await accounts.EnsureGenesisDirectMessagingInventoryAsync(
                    preKeyService,
                    inventoryPlacement,
                    issuedAt,
                    issuedAt,
                    expiresAt,
                    inventoryOperationId,
                    oneTimePreKeyCount,
                    lastResortReuseLimit,
                    cancellationToken)
                .ConfigureAwait(false);
            var verifiedInventory = await contactResolve.PublishLocalPreKeyInventoryAsync(
                    inventory,
                    route.Verified.Authority,
                    contact.Verified,
                    cancellationToken)
                .ConfigureAwait(false);
            var addressResult = await contactResolve.PublishLocalContactAddressAsync(
                    address,
                    cancellationToken)
                .ConfigureAwait(false);
            if (addressResult.Disposition is not (
                    ContactAddressPublicationDisposition.Confirmed or
                    ContactAddressPublicationDisposition.AlreadyConfirmed))
            {
                throw new CryptographicException(
                    $"The generation-zero XPU1 publication was not confirmed: {addressResult.Disposition}.");
            }

            _ = await accounts.EnsureGenesisContactUpdateRendezvousAsync(
                    route.Verified,
                    key,
                    issuedAt,
                    expiresAt,
                    cancellationToken)
                .ConfigureAwait(false);

            return new AuthoredGenesisContactReleaseState(
                route,
                preKeyService,
                contact,
                address,
                inventory,
                verifiedInventory,
                addressResult);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(inventoryOperationId);
        }
    }

    private async ValueTask<AuthoredPermanentAddressPublication>
        AuthorizeGenesisPublicationCoreAsync(
            AuthoredPermanentContactPublication publication,
            AuthoredPermanentContactRoute route,
            IContactDeviceCustodySigner signer,
            ulong issuedAtUnixSeconds,
            ulong expiresAtUnixSeconds,
            ulong effectiveExpiresAtUnixSeconds,
            CancellationToken cancellationToken)
    {
        var requestNonce = RandomNonZero32();
        var operationId = RandomNonZero32();
        var ownerRetrieveCapability = RandomNonZero32();
        try
        {
            var authoredRequest = await ContactPublicationAuthorityAuthor.AuthorAsync(
                    publication,
                    route,
                    signer,
                    requestNonce,
                    operationId,
                    new byte[32],
                    issuedAtUnixSeconds,
                    expiresAtUnixSeconds,
                    effectiveExpiresAtUnixSeconds,
                    ownerRetrieveCapability,
                    cancellationToken)
                .ConfigureAwait(false);
            return await publicationAuthorityClient.IssueAsync(
                    authoredRequest,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(requestNonce);
            CryptographicOperations.ZeroMemory(operationId);
            CryptographicOperations.ZeroMemory(ownerRetrieveCapability);
        }
    }

    private static byte[] RandomNonZero32()
    {
        var value = new byte[32];
        do RandomNumberGenerator.Fill(value);
        while (value.AsSpan().IndexOfAnyExcept((byte)0) < 0);
        return value;
    }
}

internal sealed record AuthoredGenesisContactReleaseState(
    AuthoredPermanentContactRoute Route,
    VerifiedContactPreKeyService PreKeyService,
    AuthoredPermanentContactPublication Contact,
    AuthoredPermanentAddressPublication Address,
    DeepDirectMessagingInventoryPublication Inventory,
    VerifiedPreKeyInventoryPublication VerifiedInventory,
    ContactAddressPublicationResult AddressPublication);
