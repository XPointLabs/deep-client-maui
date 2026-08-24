using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.ViewModels.Tests.ViewModels;

internal static class StrictLiveEndpointPolicy
{
    internal static RoutedRuntimeEndpointPolicy Resolve() =>
        RoutedRuntimeEndpointPolicy.Production;

    internal static HttpServiceEndpointPolicy ResolveHttpServicePolicy() =>
        HttpServiceEndpointPolicy.Production;
}
