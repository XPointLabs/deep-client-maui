using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Services;

internal sealed record PrivacyMailboxRouteDiagnosticHop(
    int Index,
    string RouterId,
    Uri? PublicIngress);

internal sealed record PrivacyMailboxRouteDiagnosticSnapshot(
    IReadOnlyList<PrivacyMailboxRouteDiagnosticHop> Primary,
    IReadOnlyList<PrivacyMailboxRouteDiagnosticHop> Fallback);

internal sealed record PrivacyMailboxRouteDiagnosticSelection(
    string Route,
    string EntryRouterId);

public sealed class PrivacyMailboxRouteDiagnostics
{
    private PrivacyMailboxRouteDiagnosticSelection? selection;

    // No route topology is published until it originates from a verified XNV/XNH/PMT/DTT
    // closure. In particular, diagnostics must never turn configuration JSON into routing
    // authority or expose caller-authored hops as if they had been observed.
    internal PrivacyMailboxRouteDiagnosticSnapshot? Current => null;

    internal PrivacyMailboxRouteDiagnosticSelection? CurrentSelection =>
        Volatile.Read(ref selection);

    internal void ObserveSelection(
        PrivacyMailboxRouteSelection route,
        ReadOnlyMemory<byte> entryRouterId)
    {
        if (entryRouterId.Length != 32 ||
            entryRouterId.Span.IndexOfAnyExcept((byte)0) < 0)
            return;
        Interlocked.Exchange(
            ref selection,
            new PrivacyMailboxRouteDiagnosticSelection(
                route == PrivacyMailboxRouteSelection.Primary ? "primary" : "fallback",
                Convert.ToHexStringLower(entryRouterId.Span)));
    }

}

internal sealed class PrivacyMailboxRouteSelectionBridge(
    PrivacyMailboxRouteDiagnostics diagnostics) :
    IPrivacyMailboxRouteSelectionObserver
{
    public void Observe(
        PrivacyMailboxRouteSelection selection,
        ReadOnlyMemory<byte> entryRouterId) =>
        diagnostics.ObserveSelection(selection, entryRouterId);
}
