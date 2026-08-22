#if DEBUG && DEEP_PHYSICAL_E2E
using System.Text.RegularExpressions;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Services;

internal sealed partial class PhysicalE2eMessageDispatchFailureObserver : IMessageDispatchFailureObserver
{
    public static PhysicalE2eMessageDispatchFailureObserver Instance { get; } = new();

    private PhysicalE2eMessageDispatchFailureObserver()
    {
    }

    public void Observe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var type = exception.GetType().FullName ?? exception.GetType().Name;
        var originType = exception.TargetSite?.DeclaringType?.FullName ?? "unknown";
        var originMethod = exception.TargetSite?.Name ?? "unknown";
        var detail = Sanitize(exception.Message);
        var diagnostic = new InvalidOperationException(
            $"Physical message dispatch failed before a durable receipt. " +
            $"Type={type}; Origin={originType}.{originMethod}; Detail={detail}");
        global::Deep.Client.Maui.CrashDiagnostics.LogException(
            "PhysicalE2E.MessageDispatch",
            diagnostic,
            "The production transport path rejected the physical message dispatch.");
    }

    private static string Sanitize(string? value)
    {
        var bounded = string.IsNullOrWhiteSpace(value)
            ? "unavailable"
            : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        bounded = LongHex().Replace(bounded, "<redacted-hex>");
        bounded = LongBase58().Replace(bounded, "<redacted-id>");
        return bounded.Length <= 512 ? bounded : bounded[..512];
    }

    [GeneratedRegex("(?i)\\b[0-9a-f]{32,}\\b", RegexOptions.CultureInvariant)]
    private static partial Regex LongHex();

    [GeneratedRegex("\\b[1-9A-HJ-NP-Za-km-z]{32,}\\b", RegexOptions.CultureInvariant)]
    private static partial Regex LongBase58();
}
#endif
