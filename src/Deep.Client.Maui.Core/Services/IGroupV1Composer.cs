namespace Deep.Client.Maui.Core.Services;

public enum GroupV1ComposerAvailability
{
    Ready = 1,
    NoLocalAccount = 2,
    CustodyRuntimeUnavailable = 3,
    ContactVerificationUnavailable = 4,
    OwnerContactCapabilityUnavailable = 5,
}

public sealed record GroupV1ComposerReadiness(
    GroupV1ComposerAvailability Availability,
    string Message)
{
    public bool IsReady => Availability == GroupV1ComposerAvailability.Ready;
}

/// <summary>
/// UI-safe projection of a ContactV1 capability. The canonical address is
/// reverified by the production composer before authoring any GroupV1 bytes.
/// </summary>
public sealed record GroupV1DraftMember(
    string CanonicalAddress,
    string DisplayName);

public sealed record GroupV1ComposerGroup(
    string GroupId,
    string Name,
    int MemberCount,
    int PendingInvitationCount,
    bool HasForkConflict);

/// <summary>
/// Account-scoped GroupV1 composition boundary. It accepts only canonical
/// permanent deep1/DID1 or deepinvite/DIA1 text and never grants authority to
/// caller-provided identifiers.
/// </summary>
public interface IGroupV1Composer
{
    ValueTask<GroupV1ComposerReadiness> GetReadinessAsync(
        CancellationToken cancellationToken = default);

    ValueTask<GroupV1DraftMember> VerifyMemberAsync(
        string canonicalAddress,
        CancellationToken cancellationToken = default);

    ValueTask<GroupV1ComposerGroup> CreateAsync(
        string groupName,
        IReadOnlyList<GroupV1DraftMember> members,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<GroupV1ComposerGroup>> ReadAllAsync(
        CancellationToken cancellationToken = default);
}

public sealed class GroupV1ComposerException(
    string code,
    string message,
    Exception? inner = null) : InvalidOperationException(message, inner)
{
    public string Code { get; } = string.IsNullOrWhiteSpace(code)
        ? throw new ArgumentException("A GroupV1 composer error code is required.", nameof(code))
        : code;
}
