namespace Deep.Client.Maui.Core.Services;

/// <summary>
/// Activates the local account and, when a production directory endpoint is
/// configured, obtains an independently verified genesis admission.
/// </summary>
public interface IDeepAccountDirectoryAdmissionCoordinator
{
    Task EnsureCurrentAccountAdmittedAsync(
        CancellationToken cancellationToken = default);
}
