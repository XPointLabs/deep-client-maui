using Deep.Client.Shared.Domain;

namespace Deep.Client.Maui.Core.ViewModels;

public interface IContactMailboxOnboarding
{
    bool CanAccept(string contactInput);

    Task<SessionId> PrepareAsync(
        string contactInput,
        CancellationToken cancellationToken = default);
}

public sealed class SessionIdContactMailboxOnboarding : IContactMailboxOnboarding
{
    public bool CanAccept(string contactInput)
    {
        try
        {
            _ = SessionId.Parse(contactInput.Trim());
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Task<SessionId> PrepareAsync(
        string contactInput,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SessionId.Parse(contactInput.Trim()));
    }
}
