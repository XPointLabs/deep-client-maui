using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Services;

internal enum RuntimeTransportProtocol
{
    DirectP2p = 1,
    AuthenticatedMau2 = 2
}

internal sealed record RuntimeTransportMode(
    RuntimeTransportProtocol Protocol,
    MailboxInfrastructureOwnership Ownership)
{
    public static RuntimeTransportMode Parse(string? protocol, string? ownership)
    {
        var parsedProtocol = protocol switch
        {
            "direct-p2p" => RuntimeTransportProtocol.DirectP2p,
            "authenticated-mau2" => RuntimeTransportProtocol.AuthenticatedMau2,
            _ => throw new InvalidOperationException(
                "DEEP_TRANSPORT_PROTOCOL must be direct-p2p or authenticated-mau2.")
        };
        var parsedOwnership = ownership switch
        {
            "direct-p2p" => MailboxInfrastructureOwnership.DirectP2p,
            "user-managed" => MailboxInfrastructureOwnership.UserManaged,
            "official-managed" => MailboxInfrastructureOwnership.OfficialManaged,
            _ => throw new InvalidOperationException(
                "DEEP_TRANSPORT_OWNERSHIP must be direct-p2p, user-managed, or official-managed.")
        };
        var mode = new RuntimeTransportMode(parsedProtocol, parsedOwnership);
        mode.Validate();
        return mode;
    }

    public void Validate()
    {
        var valid = Protocol switch
        {
            RuntimeTransportProtocol.DirectP2p =>
                Ownership == MailboxInfrastructureOwnership.DirectP2p,
            RuntimeTransportProtocol.AuthenticatedMau2 =>
                Ownership is MailboxInfrastructureOwnership.UserManaged or
                    MailboxInfrastructureOwnership.OfficialManaged,
            _ => false
        };
        if (!valid)
        {
            throw new InvalidOperationException(
                "Transport protocol and infrastructure ownership are incompatible.");
        }
    }
}
