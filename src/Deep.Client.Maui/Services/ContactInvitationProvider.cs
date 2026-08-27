using Deep.Client.Shared.State;

namespace Deep.Client.Maui.Services;

public interface IContactInvitationProvider
{
    Task<string?> GetInvitationAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// DEV/test bootstrap for the exact pre-provisioned pair. Production replaces this service with
/// a CMI1 provider backed by the verified LocalOwner PRA1.
/// </summary>
internal sealed class SessionIdContactInvitationProvider(ClientRuntime runtime) :
    IContactInvitationProvider
{
    public async Task<string?> GetInvitationAsync(
        CancellationToken cancellationToken = default) =>
        (await runtime.Accounts.GetActiveAccountAsync(cancellationToken)
            .ConfigureAwait(false))?.SessionId.Value;
}
