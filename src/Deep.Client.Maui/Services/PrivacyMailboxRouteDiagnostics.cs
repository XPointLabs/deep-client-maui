using Deep.Protocol.DeepExtension.PrivacyRouting;
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
    private PrivacyMailboxRouteDiagnosticSnapshot? snapshot;
    private PrivacyMailboxRouteDiagnosticSelection? selection;

    internal PrivacyMailboxRouteDiagnosticSnapshot? Current =>
        Volatile.Read(ref snapshot);

    internal PrivacyMailboxRouteDiagnosticSelection? CurrentSelection =>
        Volatile.Read(ref selection);

    internal void Publish(MailboxPrivacyRouteSet routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var next = new PrivacyMailboxRouteDiagnosticSnapshot(
            Project(routes.Primary.EntryOrigin, routes.Primary.Hops),
            Project(routes.Fallback.EntryOrigin, routes.Fallback.Hops));
        Interlocked.Exchange(ref snapshot, next);
    }

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

    private static IReadOnlyList<PrivacyMailboxRouteDiagnosticHop> Project(
        Uri entryOrigin,
        IReadOnlyList<PrivacyRoutingHop> hops) =>
        hops.Select((hop, index) => new PrivacyMailboxRouteDiagnosticHop(
                index,
                Convert.ToHexString(hop.RouterId.Span).ToLowerInvariant(),
                index == 0 ? entryOrigin : null))
            .ToArray();
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
