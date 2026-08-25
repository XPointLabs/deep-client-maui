using Deep.Client.Maui.Services;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class PrivacyMailboxRouteDiagnosticsTests
{
    [Theory]
    [InlineData((int)PrivacyMailboxRouteSelection.Primary, "primary", 0x11)]
    [InlineData((int)PrivacyMailboxRouteSelection.Fallback, "fallback", 0x71)]
    public void BridgePublishesExactSelectedEntryRouter(
        int selectionValue,
        string expectedRoute,
        byte seed)
    {
        var diagnostics = new PrivacyMailboxRouteDiagnostics();
        var bridge = new PrivacyMailboxRouteSelectionBridge(diagnostics);
        var routerId = Enumerable.Range(0, 32)
            .Select(index => checked((byte)(seed + index)))
            .ToArray();

        bridge.Observe((PrivacyMailboxRouteSelection)selectionValue, routerId);
        Array.Clear(routerId);

        var observed = Assert.IsType<PrivacyMailboxRouteDiagnosticSelection>(
            diagnostics.CurrentSelection);
        Assert.Equal(expectedRoute, observed.Route);
        Assert.Equal(
            Convert.ToHexStringLower(Enumerable.Range(0, 32)
                .Select(index => checked((byte)(seed + index)))
                .ToArray()),
            observed.EntryRouterId);
    }

    [Fact]
    public void InvalidEntryRouterIdIsIgnored()
    {
        var diagnostics = new PrivacyMailboxRouteDiagnostics();

        diagnostics.ObserveSelection(
            PrivacyMailboxRouteSelection.Primary,
            new byte[32]);

        Assert.Null(diagnostics.CurrentSelection);
    }
}
