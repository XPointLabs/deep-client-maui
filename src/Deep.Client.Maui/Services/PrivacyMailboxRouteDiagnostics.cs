using Deep.Protocol.DeepExtension.PrivacyRouting;

namespace Deep.Client.Maui.Services;

internal sealed record PrivacyMailboxRouteDiagnosticHop(
    int Index,
    string RouterId,
    Uri? PublicIngress);

internal sealed record PrivacyMailboxRouteDiagnosticSnapshot(
    IReadOnlyList<PrivacyMailboxRouteDiagnosticHop> Primary,
    IReadOnlyList<PrivacyMailboxRouteDiagnosticHop> Fallback);

public sealed class PrivacyMailboxRouteDiagnostics
{
    private PrivacyMailboxRouteDiagnosticSnapshot? snapshot;

    internal PrivacyMailboxRouteDiagnosticSnapshot? Current =>
        Volatile.Read(ref snapshot);

    internal void Publish(MailboxPrivacyRouteSet routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var next = new PrivacyMailboxRouteDiagnosticSnapshot(
            Project(routes.Primary.EntryOrigin, routes.Primary.Hops),
            Project(routes.Fallback.EntryOrigin, routes.Fallback.Hops));
        Interlocked.Exchange(ref snapshot, next);
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
