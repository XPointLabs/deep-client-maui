using Xunit;

namespace Deep.Client.Maui.UiTests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class StrictWindowsUiFactAttribute : FactAttribute
{
    public StrictWindowsUiFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(StrictLaneEnvironment.WindowsUiEnabledKey),
                "1",
                StringComparison.Ordinal))
        {
            Skip = $"NOT-RUN: set {StrictLaneEnvironment.WindowsUiEnabledKey}=1 and use eng/Invoke-StrictClientLane.ps1.";
        }
    }
}

internal static class StrictLaneEnvironment
{
    internal const string WindowsUiEnabledKey = "DEEP_STRICT_WINDOWS_UI";

    internal static readonly string[] EndpointKeys =
    [
        "XNODE_URLS",
        "DEEP_STORAGE_URL",
        "DEEP_TRANSPORT_BASE_URL",
        "DEEP_FILE_URL",
        "DEEP_PUSH_URL",
        "DEEP_CALL_SIGNALING_BASE_URL",
        "DEEP_CALL_SIGNALING_URL"
    ];
}
