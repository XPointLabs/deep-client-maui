using Android.App;
using Android.Bluetooth;
using Android.Content;
using Android.Content.PM;
using Android.Net.Wifi;
using Android.Net.Wifi.Aware;
using Deep.Client.Maui.Core.Services;
using Microsoft.Maui.ApplicationModel;
using Application = Android.App.Application;

namespace Deep.Client.Maui.Platforms.Android;

internal sealed class AndroidNearbyPermissionBroker : INearbyPermissionBroker
{
    private const string BluetoothScanPermission =
        "android.permission.BLUETOOTH_SCAN";
    private const string BluetoothConnectPermission =
        "android.permission.BLUETOOTH_CONNECT";
    private const string BluetoothAdvertisePermission =
        "android.permission.BLUETOOTH_ADVERTISE";
    private const string NearbyWifiDevicesPermission =
        "android.permission.NEARBY_WIFI_DEVICES";
    private const string FineLocationPermission =
        "android.permission.ACCESS_FINE_LOCATION";
    private readonly Context context;

    internal AndroidNearbyPermissionBroker(Context? context = null)
    {
        this.context = context ?? Application.Context;
    }

    public Task<IReadOnlyList<NearbyPermissionStatus>> CheckAsync(
        NearbyRadioTechnology technology,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTechnology(technology);
        return Task.FromResult(BuildReport(technology));
    }

    public async Task<IReadOnlyList<NearbyPermissionStatus>> RequestAsync(
        NearbyRadioTechnology technology,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTechnology(technology);

        if (technology == NearbyRadioTechnology.BluetoothLowEnergy)
        {
            _ = await MainThread.InvokeOnMainThreadAsync(
                    Permissions.RequestAsync<BluetoothDiscoveryPermissions>)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            _ = await MainThread.InvokeOnMainThreadAsync(
                    Permissions.RequestAsync<WifiPeerDiscoveryPermissions>)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return BuildReport(technology);
    }

    private IReadOnlyList<NearbyPermissionStatus> BuildReport(
        NearbyRadioTechnology technology)
    {
        var bluetoothModern = OperatingSystem.IsAndroidVersionAtLeast(31);
        var wifiModern = OperatingSystem.IsAndroidVersionAtLeast(33);
        return
        [
            Status(
                NearbyPermissionKind.BluetoothScan,
                technology == NearbyRadioTechnology.BluetoothLowEnergy && bluetoothModern,
                BluetoothScanPermission),
            Status(
                NearbyPermissionKind.BluetoothConnect,
                technology == NearbyRadioTechnology.BluetoothLowEnergy && bluetoothModern,
                BluetoothConnectPermission),
            Status(
                NearbyPermissionKind.BluetoothAdvertise,
                technology == NearbyRadioTechnology.BluetoothLowEnergy && bluetoothModern,
                BluetoothAdvertisePermission),
            Status(
                NearbyPermissionKind.NearbyWifiDevices,
                technology is NearbyRadioTechnology.WifiDirect or NearbyRadioTechnology.WifiAware &&
                wifiModern,
                NearbyWifiDevicesPermission),
            Status(
                NearbyPermissionKind.LegacyFineLocation,
                technology switch
                {
                    NearbyRadioTechnology.BluetoothLowEnergy => !bluetoothModern,
                    NearbyRadioTechnology.WifiDirect or NearbyRadioTechnology.WifiAware => !wifiModern,
                    _ => false
                },
                FineLocationPermission)
        ];
    }

    private NearbyPermissionStatus Status(
        NearbyPermissionKind kind,
        bool required,
        string permission)
    {
        if (!required)
        {
            return new NearbyPermissionStatus(
                kind,
                NearbyPermissionDisposition.NotRequired);
        }

        var granted = context.CheckSelfPermission(permission) ==
            Permission.Granted;
        return new NearbyPermissionStatus(
            kind,
            granted
                ? NearbyPermissionDisposition.Granted
                : NearbyPermissionDisposition.Denied);
    }

    private static void ValidateTechnology(NearbyRadioTechnology technology)
    {
        if (!Enum.IsDefined(technology))
        {
            throw new ArgumentOutOfRangeException(nameof(technology));
        }
    }

    private sealed class BluetoothDiscoveryPermissions :
        Permissions.BasePlatformPermission
    {
        public override (string androidPermission, bool isRuntime)[]
            RequiredPermissions => OperatingSystem.IsAndroidVersionAtLeast(31)
                ?
                [
                    (BluetoothScanPermission, true),
                    (BluetoothConnectPermission, true),
                    (BluetoothAdvertisePermission, true)
                ]
                :
                [
                    (FineLocationPermission, true)
                ];
    }

    private sealed class WifiPeerDiscoveryPermissions :
        Permissions.BasePlatformPermission
    {
        public override (string androidPermission, bool isRuntime)[]
            RequiredPermissions => OperatingSystem.IsAndroidVersionAtLeast(33)
                ?
                [
                    (NearbyWifiDevicesPermission, true)
                ]
                :
                [
                    (FineLocationPermission, true)
                ];
    }
}

internal sealed class AndroidNearbyRadioCapabilityProbe(
    INearbyPermissionBroker permissions,
    Context? context = null) : INearbyRadioCapabilityProbe
{
    private const string BluetoothLeFeature = "android.hardware.bluetooth_le";
    private const string WifiDirectFeature = "android.hardware.wifi.direct";
    private const string WifiAwareFeature = "android.hardware.wifi.aware";
    private const string ActivationLimitation =
        "Native nearby discovery is disabled until the authenticated direct-peer protocol is approved.";
    private readonly INearbyPermissionBroker permissions = permissions ??
        throw new ArgumentNullException(nameof(permissions));
    private readonly Context context = context ?? Application.Context;

    public async Task<NearbyRadioReadiness> InspectAsync(
        NearbyRadioTechnology technology,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(technology))
        {
            throw new ArgumentOutOfRangeException(nameof(technology));
        }

        var permissionReport = await permissions
            .CheckAsync(technology, cancellationToken)
            .ConfigureAwait(false);
        var hardware = technology switch
        {
            NearbyRadioTechnology.BluetoothLowEnergy => InspectBluetooth(),
            NearbyRadioTechnology.WifiDirect => InspectWifiDirect(),
            NearbyRadioTechnology.WifiAware => InspectWifiAware(),
            _ => NearbyRadioHardwareState.Unavailable
        };

        return new NearbyRadioReadiness(
            technology,
            hardware,
            permissionReport,
            ProtocolActivationAllowed: false,
            ActivationLimitation);
    }

    private NearbyRadioHardwareState InspectBluetooth()
    {
        if (!HasFeature(BluetoothLeFeature))
        {
            return NearbyRadioHardwareState.Unavailable;
        }

        var manager = context.GetSystemService(Context.BluetoothService) as
            BluetoothManager;
        return manager?.Adapter?.IsEnabled == true
            ? NearbyRadioHardwareState.Ready
            : NearbyRadioHardwareState.Disabled;
    }

    private NearbyRadioHardwareState InspectWifiDirect()
    {
        if (!HasFeature(WifiDirectFeature))
        {
            return NearbyRadioHardwareState.Unavailable;
        }

        var manager = context.GetSystemService(Context.WifiService) as WifiManager;
        return manager?.IsWifiEnabled == true
            ? NearbyRadioHardwareState.Ready
            : NearbyRadioHardwareState.Disabled;
    }

    private NearbyRadioHardwareState InspectWifiAware()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26) ||
            !HasFeature(WifiAwareFeature))
        {
            return NearbyRadioHardwareState.Unavailable;
        }

        var manager = context.GetSystemService(Context.WifiAwareService) as
            WifiAwareManager;
        return manager?.IsAvailable == true
            ? NearbyRadioHardwareState.Ready
            : NearbyRadioHardwareState.Disabled;
    }

    private bool HasFeature(string name) =>
        context.PackageManager?.HasSystemFeature(name) == true;
}

internal sealed class DisabledAndroidNearbyDiscoveryAdapter(
    INearbyRadioCapabilityProbe capabilityProbe) : INearbyOpaqueDiscoveryAdapter
{
    private readonly INearbyRadioCapabilityProbe capabilityProbe =
        capabilityProbe ?? throw new ArgumentNullException(nameof(capabilityProbe));

    public Task<NearbyRadioReadiness> InspectAsync(
        NearbyRadioTechnology technology,
        CancellationToken cancellationToken = default) =>
        capabilityProbe.InspectAsync(technology, cancellationToken);

    public Task<INearbyOpaqueDiscoverySession> OpenAsync(
        NearbyOpaqueDiscoveryRequest request,
        Func<NearbyOpaqueDiscoveryObservation, CancellationToken, ValueTask> observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(observation);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<INearbyOpaqueDiscoverySession>(
            new NearbyRadioUnavailableException());
    }
}
