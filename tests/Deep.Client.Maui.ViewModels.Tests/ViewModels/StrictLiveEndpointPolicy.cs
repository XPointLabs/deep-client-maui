using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.ViewModels.Tests.ViewModels;

internal static class StrictLiveEndpointPolicy
{
    internal const string PhysicalE2eEnvironmentVariable = "DEEP_STRICT_LIVE_PHYSICAL_E2E";

    internal static RoutedRuntimeEndpointPolicy Resolve(string? physicalE2eOptIn) =>
        string.Equals(physicalE2eOptIn, "1", StringComparison.Ordinal)
            ? RoutedRuntimeEndpointPolicy.PhysicalE2eDevelopment
            : RoutedRuntimeEndpointPolicy.Production;

    internal static HttpServiceEndpointPolicy ResolveHttpServicePolicy(
        RoutedRuntimeEndpointPolicy routedPolicy) =>
        ReferenceEquals(routedPolicy, RoutedRuntimeEndpointPolicy.PhysicalE2eDevelopment)
            ? HttpServiceEndpointPolicy.PhysicalE2eDevelopment
            : HttpServiceEndpointPolicy.Production;
}
