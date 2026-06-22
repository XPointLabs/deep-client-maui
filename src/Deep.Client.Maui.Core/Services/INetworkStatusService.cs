namespace Deep.Client.Maui.Core.Services;

public interface INetworkStatusService
{
    bool IsConnected { get; }

    string ConnectionLabel { get; }

    event EventHandler? StatusChanged;
}
