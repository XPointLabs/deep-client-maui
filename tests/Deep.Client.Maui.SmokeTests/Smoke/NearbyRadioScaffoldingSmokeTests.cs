namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class NearbyRadioScaffoldingSmokeTests
{
    [Fact]
    public void AndroidManifestDeclaresOnlySdkBoundNearbyPermissionsAndOptionalRadios()
    {
        var manifest = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "Platforms", "Android", "AndroidManifest.xml");

        Assert.Contains(
            "android.permission.BLUETOOTH\" android:maxSdkVersion=\"30\"",
            manifest,
            StringComparison.Ordinal);
        Assert.Contains(
            "android.permission.BLUETOOTH_ADMIN\" android:maxSdkVersion=\"30\"",
            manifest,
            StringComparison.Ordinal);
        Assert.Contains("android.permission.BLUETOOTH_SCAN", manifest, StringComparison.Ordinal);
        Assert.Contains("android.permission.BLUETOOTH_CONNECT", manifest, StringComparison.Ordinal);
        Assert.Contains("android.permission.BLUETOOTH_ADVERTISE", manifest, StringComparison.Ordinal);
        Assert.Contains(
            "android.permission.ACCESS_FINE_LOCATION\" android:maxSdkVersion=\"32\"",
            manifest,
            StringComparison.Ordinal);
        Assert.Contains("android.permission.NEARBY_WIFI_DEVICES", manifest, StringComparison.Ordinal);
        Assert.Equal(2, Count(manifest, "android:usesPermissionFlags=\"neverForLocation\""));

        Assert.Contains(
            "android.hardware.bluetooth_le\" android:required=\"false\"",
            manifest,
            StringComparison.Ordinal);
        Assert.Contains(
            "android.hardware.wifi.direct\" android:required=\"false\"",
            manifest,
            StringComparison.Ordinal);
        Assert.Contains(
            "android.hardware.wifi.aware\" android:required=\"false\"",
            manifest,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionCompositionDoesNotRegisterNearbyRadioOrDirectMessageTransport()
    {
        var program = ReadWorkspaceFile("src", "Deep.Client.Maui", "MauiProgram.cs");
        var android = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "Platforms", "Android",
            "AndroidNearbyRadioScaffolding.cs");

        Assert.DoesNotContain("AndroidNearbyPermissionBroker", program, StringComparison.Ordinal);
        Assert.DoesNotContain("AndroidNearbyRadioCapabilityProbe", program, StringComparison.Ordinal);
        Assert.DoesNotContain("DisabledAndroidNearbyDiscoveryAdapter", program, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AddSingleton<IDirectP2pSessionMessageTransport",
            program,
            StringComparison.Ordinal);
        Assert.Contains("ProtocolActivationAllowed: false", android, StringComparison.Ordinal);
        Assert.Contains("new NearbyRadioUnavailableException()", android, StringComparison.Ordinal);
        Assert.DoesNotContain("StartAdvertising", android, StringComparison.Ordinal);
        Assert.DoesNotContain("StartDiscovery", android, StringComparison.Ordinal);
        Assert.DoesNotContain("Attach", android, StringComparison.Ordinal);
        Assert.DoesNotContain("Publish", android, StringComparison.Ordinal);
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static string ReadWorkspaceFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "Deep.Client.Maui.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine([directory!.FullName, .. parts]));
    }
}
