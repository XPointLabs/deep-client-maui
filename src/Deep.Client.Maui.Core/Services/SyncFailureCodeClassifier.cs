using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Core.Services;

internal static class SyncFailureCodeClassifier
{
    internal static string Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ClientMailboxTransportException mailbox)
                return $"mailbox-{(int)mailbox.Failure}";
            if (current is DurableInboxDigestMismatchException)
                return "inbox-corrupt";
            if (current is TransportOutboxCorruptException)
                return "outbox-corrupt";
            if (current is TransportOutboxCommitOutcomeUnknownException)
                return "outbox-unknown";
            if (current is System.Security.Authentication.AuthenticationException)
                return "tls";
            if (current is InvalidDataException or System.Security.Cryptography.CryptographicException)
                return "runtime-policy";
            if (current is UnauthorizedAccessException)
                return "local-access";
            if (current is InvalidOperationException)
                return "invalid-state";
            if (current is IOException)
                return "io";
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is System.Net.Http.HttpRequestException { StatusCode: { } statusCode })
                return $"http-{(int)statusCode}";
            if (current is System.Net.Http.HttpRequestException)
                return "transport";
        }

        return "unknown";
    }
}
