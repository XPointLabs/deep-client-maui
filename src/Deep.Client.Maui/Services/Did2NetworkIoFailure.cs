using System.Security.Cryptography;
using Deep.Client.Shared.Services;
using Deep.Protocol.DeepExtension.PrivacyRouting;

namespace Deep.Client.Maui.Services;

/// <summary>Closed display-only failures; private transport messages remain
/// available as inner exceptions, never as UI or evidence text.</summary>
internal static class Did2NetworkIoFailure
{
    internal static InvalidOperationException AtStage(string stage, IOException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (stage is not ("AccountProof" or "NetworkVerification" or
            "PreKeyStaging" or "PreKeyPublication" or "ContactPublication" or "ContactResolution"))
            throw new ArgumentException("Unknown DID2 diagnostic stage.", nameof(stage));

        // Classify only code-owned exception types, never private messages or
        // caller-provided names. Every ONION classification retains unknown
        // completion: it must not authorize retry, success or a direct fallback.
        var failure = exception is ClientMailboxDispatchOutcomeUnknownException
            ? exception.InnerException switch
            {
                null => "OnionCompletionUnknown",
                CryptographicException or OnionBoundaryException => "OnionReplyRejected",
                OperationCanceledException => "OnionTimeoutUnknown",
                HttpRequestException or IOException => "OnionTransportUnknown",
                _ => "OnionOutcomeUnknown"
            }
            : "TransportIo";
        return new InvalidOperationException(
            $"DID2 {stage} failed ({failure}).", exception);
    }
}
