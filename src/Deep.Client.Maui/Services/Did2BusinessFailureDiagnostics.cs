using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Services;

// Display/log metadata only. Never emits exception text or file paths. Only an
// exact built-in CryptographicException message is compared with fixed owned
// guard literals; unknown text and custom getters never enter the output.
// No outcome/authority/retry decision consumes these diagnostic labels.
internal static class Did2BusinessFailureDiagnostics
{
    internal static IReadOnlyList<string> Capture(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var result = new List<string>();
        for (var depth = 0; error is not null && depth < 4; depth++,error = error.InnerException!)
        {
            var category = error switch
            {
                ClientMailboxDispatchOutcomeUnknownException => "OutcomeUnknown",
                ClientMailboxTransportException => "MailboxTransport",
                CryptographicException or InvalidDataException => "ProtectedState",
                OperationCanceledException => "Cancelled",
                TimeoutException => "Timeout",
                HttpRequestException => "HttpTransport",
                IOException => "Io",
                _ => "Other"
            };
            var check = ContactCheck(error);
            var trace = new StackTrace(error,true); var count = 0;
            for (var index = 0; index < Math.Min(trace.FrameCount,48) && count < 4; index++)
            {
                var frame = trace.GetFrame(index); var method = frame?.GetMethod();
                var type = method?.DeclaringType; var space = type?.Namespace;
                if (space is null || !(space.StartsWith("Deep.Client.Shared",StringComparison.Ordinal) ||
                    space.StartsWith("Deep.Protocol",StringComparison.Ordinal) ||
                    space.StartsWith("Deep.Client.Maui",StringComparison.Ordinal))) continue;
                var origin = (type!.DeclaringType is { } parent ? parent.Name + "+" : "") + type.Name + "." + method!.Name;
                if (origin.Length is 0 or > 240 || !origin.All(ch => char.IsAsciiLetterOrDigit(ch) || "_.+<>`".Contains(ch))) continue;
                var line = Math.Clamp(frame!.GetFileLineNumber(),0,99999);
                var il = Math.Clamp(frame.GetILOffset(),-1,1048575);
                result.Add(FormattableString.Invariant($"kind={category}; check={check}; depth={depth}; method={origin}; line={line}; il={il}"));
                count++;
            }
            if (count == 0) result.Add(string.Create(CultureInfo.InvariantCulture,
                $"kind={category}; check={check}; depth={depth}; method=unavailable; line=0; il=-1"));
        }
        return result;
    }

    // QA-only code-owned guard classification, never a message-based trust
    // check or a fallback. Renamed/unknown/private messages remain Unclassified.
    private static string ContactCheck(Exception error) => error.GetType() != typeof(CryptographicException)
        ? "Unclassified" : error.Message switch
        {
            "Contact control requires two current DID2 directory proofs." => "PairFreshness",
            "Contact control requires distinct current accounts on one network." => "PairNetwork",
            "A fresh current DID2 directory checkpoint is required for the rendezvous." or
                "The rendezvous has no current DID2 checkpoint." => "XurFreshness",
            "The inbound rendezvous does not cover the entire authenticated time interval." => "XurWindow",
            "The inbound rendezvous differs from its current DID2 device authority." => "XurAuthority",
            "The inbound rendezvous current device signature is invalid." => "XurSignature",
            "The rendezvous trusted-time interval overflowed." => "ClockOverflow",
            "Contact control crossed a protected clock discontinuity." => "ClockContinuity",
            "ContactHello differs from its exact current DID2 endpoints." => "HelloMetadata",
            "ContactHello differs from its PQ-bound safety number." => "HelloSafety",
            "ContactHello is outside the issuer/device validity interval." => "HelloCreation",
            "The private reply route differs from its exact current contact sender endpoint." => "MailboxEndpoint",
            _ => "Unclassified"
        };
}
