#if ANDROID
using Android.Content;
using Android.Net;
using Android.OS;
using Android.Util;
using Java.Lang;
using Microsoft.Maui.ApplicationModel;
using Object = Java.Lang.Object;

namespace Deep.Client.Maui;

internal sealed class AndroidXrayDialerController : Object, global::LibXray.IDialerController
{
    private const string LogTag = "DeepXray";
    private readonly ConnectivityManager? connectivityManager =
        Platform.AppContext.GetSystemService(Context.ConnectivityService) as ConnectivityManager;
    private bool directBindUnavailable;
    private bool protectFromVpnUnavailable;
    private int statusLogState;

    public bool ProtectFd(long fd)
    {
        if (fd is < int.MinValue or > int.MaxValue)
        {
            return false;
        }

        try
        {
            if (!Microsoft.Maui.Storage.Preferences.Default.Get(Services.ClientSettingKeys.NetworkBypassSystemVpn, false))
            {
                LogStatusOnce("Embedded Xray respects the active Android VPN.");
                return true;
            }

            var socketFd = (int)fd;
            if (!protectFromVpnUnavailable && TryProtectFromVpn(socketFd))
            {
                LogStatusOnce("Embedded Xray sockets are protected from the active Android VPN.");
                return true;
            }

            if (directBindUnavailable)
            {
                LogStatusOnce("Android denied external VPN bypass; embedded Xray will use the active network path.");
                return true;
            }

            var network = SelectNonVpnNetwork();
            if (network is null)
            {
                LogStatusOnce("No non-VPN network is available; embedded Xray will use the active network path.");
                return true;
            }

            using var socket = ParcelFileDescriptor.FromFd(socketFd);
            var fileDescriptor = socket?.FileDescriptor;
            if (fileDescriptor is null)
            {
                LogStatusOnce("Android did not expose the Xray socket descriptor; embedded Xray will use the active network path.");
                return true;
            }

            network.BindSocket(fileDescriptor);
            LogStatusOnce($"Embedded Xray sockets are bound to non-VPN network {network}.");
            return true;
        }
        catch (System.Exception exception)
        {
            directBindUnavailable = true;
            Log.Debug(LogTag, $"Could not bind embedded Xray socket outside VPN: {exception.Message}");
            LogStatusOnce("Android denied external VPN bypass; embedded Xray will use the active network path.");
            return true;
        }
    }

    private bool TryProtectFromVpn(int socketFd)
    {
        var manager = connectivityManager;
        if (manager is null)
        {
            return false;
        }

        try
        {
            var method = manager.Class.GetDeclaredMethod("protectFromVpn", Java.Lang.Integer.Type!);
            method.Accessible = true;
            using var boxedFd = Java.Lang.Integer.ValueOf(socketFd);
            using var result = method.Invoke(manager, boxedFd);
            return result is Java.Lang.Boolean value && value.BooleanValue();
        }
        catch (Java.Lang.Exception exception)
        {
            protectFromVpnUnavailable = true;
            Log.Debug(LogTag, $"Android protectFromVpn is unavailable, falling back to network bind: {exception.Message}");
            return false;
        }
    }

    private Network? SelectNonVpnNetwork()
    {
        var manager = connectivityManager;
        if (manager is null)
        {
            return null;
        }

#pragma warning disable CA1422
        var networks = manager.GetAllNetworks() ?? [];
#pragma warning restore CA1422
        return networks
            .Select(network => new { Network = network, Capabilities = manager.GetNetworkCapabilities(network) })
            .Where(item => item.Capabilities is not null
                && item.Capabilities.HasCapability(NetCapability.Internet)
                && !item.Capabilities.HasTransport(TransportType.Vpn))
            .OrderByDescending(item => item.Capabilities!.HasCapability(NetCapability.Validated))
            .ThenByDescending(item => item.Capabilities!.HasTransport(TransportType.Wifi))
            .ThenByDescending(item => item.Capabilities!.HasTransport(TransportType.Ethernet))
            .ThenByDescending(item => item.Capabilities!.HasTransport(TransportType.Cellular))
            .Select(item => item.Network)
            .FirstOrDefault();
    }

    private void LogStatusOnce(string message)
    {
        if (Interlocked.Exchange(ref statusLogState, 1) == 0)
        {
            Log.Info(LogTag, message);
        }
    }
}
#endif
