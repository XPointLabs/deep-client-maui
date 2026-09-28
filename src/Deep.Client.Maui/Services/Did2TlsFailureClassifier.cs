using System.Security.Authentication;
using System.Text.RegularExpressions;

namespace Deep.Client.Maui.Services;

/// <summary>Display-only, closed TLS classifications. Never copies exception
/// messages, certificate names, URLs or request bytes into UI/evidence.</summary>
internal static class Did2TlsFailureClassifier
{
    private static readonly string[] KnownStatuses = ["Revoked", "NotTimeValid",
        "UntrustedRoot", "PartialChain", "RevocationStatusUnknown", "OfflineRevocation",
        "RemoteCertificateNameMismatch", "RemoteCertificateNotAvailable"];

    internal static string Classify(Exception exception)
    {
        var statuses = new HashSet<string>(StringComparer.Ordinal);
        Exception? current = exception;
        for (var depth = 0; current is not null && depth < 8; depth++, current = current.InnerException)
        {
            if (current is not AuthenticationException || current.Message.Length > 4096) continue;
            foreach (var status in KnownStatuses)
                if (Regex.IsMatch(current.Message, @"\b" + status + @"\b", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
                    statuses.Add(status);
        }
        return statuses.Count == 0 ? "Unknown" : string.Join(" ", KnownStatuses.Where(statuses.Contains));
    }
}
