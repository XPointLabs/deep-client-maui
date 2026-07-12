namespace Deep.Client.Maui.Core.Services;

public interface IAccountLogoutCoordinator
{
    Task LogoutAsync(CancellationToken cancellationToken = default);
}
