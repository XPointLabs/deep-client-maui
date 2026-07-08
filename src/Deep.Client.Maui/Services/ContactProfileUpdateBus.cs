using Deep.Client.Shared.Domain;

namespace Deep.Client.Maui.Services;

internal static class ContactProfileUpdateBus
{
    public static event EventHandler<SessionId>? ContactChanged;

    public static void NotifyContactChanged(SessionId contactId) =>
        ContactChanged?.Invoke(null, contactId);
}
