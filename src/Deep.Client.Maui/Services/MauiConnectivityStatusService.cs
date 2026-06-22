using Deep.Client.Maui.Core.Services;
using Microsoft.Maui.Networking;

namespace Deep.Client.Maui.Services;

public sealed class MauiConnectivityStatusService : INetworkStatusService
{
    public MauiConnectivityStatusService()
    {
        Connectivity.ConnectivityChanged += OnConnectivityChanged;
    }

    public bool IsConnected =>
        Connectivity.Current.NetworkAccess == NetworkAccess.Internet
        || Connectivity.Current.NetworkAccess == NetworkAccess.ConstrainedInternet;

    public string ConnectionLabel => Connectivity.Current.NetworkAccess switch
    {
        NetworkAccess.Internet => "Онлайн",
        NetworkAccess.ConstrainedInternet => "Ограничено",
        NetworkAccess.Local => "Только локальная сеть",
        NetworkAccess.None => "Офлайн",
        _ => "Неизвестно"
    };

    public event EventHandler? StatusChanged;

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}
