using System.Security.Cryptography;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Domain.ContactV1;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.Services.ContactV1;
using Deep.Protocol.ContactV1;
using Deep.Protocol.DeepExtension.PrivacyRouting;
using Deep.Protocol.Identity;
using Deep.Protocol.XPointNetworkV1;

namespace Deep.Client.Maui.Services.GroupV1;

internal enum GroupContactCapabilityRole
{
    Owner = 1,
    Participant = 2,
}

/// <summary>
/// Ephemeral, process-local capability. Callers may persist exact signed
/// artifacts or hashes, never this CLR object.
/// </summary>
internal sealed record VerifiedGroupContactCapability(
    GroupContactCapabilityRole Role,
    DeepAccountId32 AccountId,
    VerifiedContactBundleClosure Bundle,
    VerifiedContactRouteClosure Route,
    VerifiedContactServicePlacement Placement);

internal interface IGroupContactCapabilitySource
{
    ValueTask<VerifiedGroupContactCapability> ReverifyOwnerAsync(
        DeepLocalIdentitySnapshot expectedOwner,
        ContactResolverVerificationInput liveTranscript,
        CancellationToken cancellationToken = default);

    ValueTask<VerifiedGroupContactCapability> ReverifyParticipantAsync(
        DeepLocalIdentitySnapshot expectedOwner,
        DeepAccountId32 expectedParticipantAccountId,
        ContactResolverVerificationInput liveTranscript,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Adapts the existing production ContactV1 verifier to Group v1. It accepts
/// only a live exact resolver transcript because Shared currently exposes no
/// public reconstruction API for the durable XIQ1/XIS1 pair.
/// </summary>
internal sealed class ProductionGroupContactCapabilitySource : IGroupContactCapabilitySource
{
    private readonly DeepAccountService accounts;
    private readonly IContactResolverVerifiedCapabilitySource contactCapabilities;

    internal ProductionGroupContactCapabilitySource(
        DeepAccountService accounts,
        IContactResolverVerifiedCapabilitySource contactCapabilities)
    {
        this.accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        this.contactCapabilities = contactCapabilities
            ?? throw new ArgumentNullException(nameof(contactCapabilities));
    }

    public ValueTask<VerifiedGroupContactCapability> ReverifyOwnerAsync(
        DeepLocalIdentitySnapshot expectedOwner,
        ContactResolverVerificationInput liveTranscript,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedOwner);
        return ReverifyAsync(
            GroupContactCapabilityRole.Owner,
            expectedOwner,
            expectedOwner.Account.AccountIdentity.AccountId,
            liveTranscript,
            cancellationToken);
    }

    public ValueTask<VerifiedGroupContactCapability> ReverifyParticipantAsync(
        DeepLocalIdentitySnapshot expectedOwner,
        DeepAccountId32 expectedParticipantAccountId,
        ContactResolverVerificationInput liveTranscript,
        CancellationToken cancellationToken = default) =>
        ReverifyAsync(
            GroupContactCapabilityRole.Participant,
            expectedOwner,
            expectedParticipantAccountId,
            liveTranscript,
            cancellationToken);

    private async ValueTask<VerifiedGroupContactCapability> ReverifyAsync(
        GroupContactCapabilityRole role,
        DeepLocalIdentitySnapshot expectedOwner,
        DeepAccountId32 expectedSubjectAccountId,
        ContactResolverVerificationInput liveTranscript,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedOwner);
        ArgumentNullException.ThrowIfNull(expectedSubjectAccountId);
        ArgumentNullException.ThrowIfNull(liveTranscript);
        cancellationToken.ThrowIfCancellationRequested();

        var current = await ReadCurrentOwnerAsync(expectedOwner, cancellationToken)
            .ConfigureAwait(false);
        if (!liveTranscript.Address.NetworkId.Span.SequenceEqual(current.NetworkId.Span))
        {
            throw new CryptographicException(
                "The ContactV1 transcript belongs to another Group v1 network.");
        }
        if (role == GroupContactCapabilityRole.Owner
            && liveTranscript.Address.Kind != ContactAddressKind.PermanentDeepId)
        {
            throw new CryptographicException(
                "A Group v1 owner capability requires the permanent Deep ID transcript.");
        }
        if (role == GroupContactCapabilityRole.Participant
            && expectedSubjectAccountId.Equals(current.Account.AccountIdentity.AccountId))
        {
            throw new CryptographicException(
                "A Group v1 participant capability cannot alias the local owner.");
        }

        ContactResolverVerifiedCapabilitySet verified;
        try
        {
            verified = await contactCapabilities.VerifyAsync(liveTranscript, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new CryptographicException(
                    "The ContactV1 verifier returned no Group v1 capability.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CryptographicException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new CryptographicException(
                "The ContactV1 capability re-verification failed closed.",
                exception);
        }

        current = await ReadCurrentOwnerAsync(expectedOwner, cancellationToken)
            .ConfigureAwait(false);
        var verifiedNetwork = verified.Bundle.Directory.Record.NetworkId;
        var verifiedAccount = verified.Bundle.Directory.Record.DeepAccountId;
        ValidateVerifiedBinding(
            role,
            current.NetworkId.Span,
            current.Account.AccountIdentity.AccountId.Bytes.Span,
            expectedSubjectAccountId.Bytes.Span,
            liveTranscript.Address.Kind,
            verifiedNetwork.Span,
            verifiedAccount.Span,
            verified.ClaimReceipt is not null);

        return new VerifiedGroupContactCapability(
            role,
            expectedSubjectAccountId,
            verified.Bundle,
            verified.Route,
            verified.Placement);
    }

    private async Task<DeepLocalIdentitySnapshot> ReadCurrentOwnerAsync(
        DeepLocalIdentitySnapshot expectedOwner,
        CancellationToken cancellationToken)
    {
        var current = await accounts.GetLocalIdentityAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CryptographicException("No current Deep account owns Group v1.");
        if (current.StoreGeneration != expectedOwner.StoreGeneration
            || current.Account.AccountIdentity.AccountGeneration
                != expectedOwner.Account.AccountIdentity.AccountGeneration
            || current.Device.DeviceGeneration != expectedOwner.Device.DeviceGeneration
            || !current.NetworkId.Span.SequenceEqual(expectedOwner.NetworkId.Span)
            || !current.Account.AccountIdentity.AccountId.Equals(
                expectedOwner.Account.AccountIdentity.AccountId)
            || !current.Device.DeviceId.Equals(expectedOwner.Device.DeviceId))
        {
            throw new CryptographicException(
                "The Group v1 capability request belongs to a stale or different local account.");
        }
        return current;
    }

    internal static void ValidateVerifiedBinding(
        GroupContactCapabilityRole role,
        ReadOnlySpan<byte> ownerNetworkId,
        ReadOnlySpan<byte> ownerAccountId,
        ReadOnlySpan<byte> expectedSubjectAccountId,
        ContactAddressKind addressKind,
        ReadOnlySpan<byte> verifiedNetworkId,
        ReadOnlySpan<byte> verifiedAccountId,
        bool hasOneTimeClaimReceipt)
    {
        if (!Enum.IsDefined(role)
            || ownerNetworkId.Length != 16
            || ownerAccountId.Length != DeepAccountId32.Size
            || expectedSubjectAccountId.Length != DeepAccountId32.Size
            || verifiedNetworkId.Length != 16
            || verifiedAccountId.Length != DeepAccountId32.Size)
        {
            throw new CryptographicException(
                "The Group v1 ContactV1 capability binding is malformed.");
        }
        if (!CryptographicOperations.FixedTimeEquals(ownerNetworkId, verifiedNetworkId))
        {
            throw new CryptographicException(
                "The verified ContactV1 closure belongs to another network.");
        }
        if (!CryptographicOperations.FixedTimeEquals(
                expectedSubjectAccountId,
                verifiedAccountId))
        {
            throw new CryptographicException(
                "The verified ContactV1 closure belongs to another account.");
        }
        if (role == GroupContactCapabilityRole.Owner
            && (!CryptographicOperations.FixedTimeEquals(ownerAccountId, verifiedAccountId)
                || addressKind != ContactAddressKind.PermanentDeepId
                || hasOneTimeClaimReceipt))
        {
            throw new CryptographicException(
                "The verified ContactV1 closure is not the permanent local owner.");
        }
        if (role == GroupContactCapabilityRole.Participant
            && CryptographicOperations.FixedTimeEquals(ownerAccountId, verifiedAccountId))
        {
            throw new CryptographicException(
                "The verified ContactV1 participant aliases the local owner.");
        }
        if (addressKind == ContactAddressKind.PermanentDeepId && hasOneTimeClaimReceipt
            || addressKind == ContactAddressKind.OneTimeInvitation && !hasOneTimeClaimReceipt)
        {
            throw new CryptographicException(
                "The verified ContactV1 claim semantics do not match the exact address kind.");
        }
    }
}
