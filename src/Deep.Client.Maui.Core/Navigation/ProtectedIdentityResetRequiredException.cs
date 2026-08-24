namespace Deep.Client.Maui.Core.Navigation;

public enum ProtectedIdentityResetRequiredReason
{
    Missing,
    Incompatible,
    AccountMismatch
}

public sealed class ProtectedIdentityResetRequiredException : Exception
{
    public ProtectedIdentityResetRequiredException(
        ProtectedIdentityResetRequiredReason reason,
        string message)
        : base(message)
    {
        Reason = reason;
    }

    public ProtectedIdentityResetRequiredReason Reason { get; }
}
