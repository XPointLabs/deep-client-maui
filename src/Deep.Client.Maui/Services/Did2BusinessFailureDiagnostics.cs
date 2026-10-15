using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Services;

// Display/log metadata only. Never reads Message, Data, ToString, file paths,
// payloads or credentials. No outcome/authority/retry decision consumes it.
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
                result.Add(FormattableString.Invariant($"kind={category}; depth={depth}; method={origin}; line={line}"));
                count++;
            }
            if (count == 0) result.Add(string.Create(CultureInfo.InvariantCulture,
                $"kind={category}; depth={depth}; method=unavailable; line=0"));
        }
        return result;
    }
}
