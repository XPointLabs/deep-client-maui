using System.Security.Cryptography;
using System.Net;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.Services.AccountDirectoryV2;
using Deep.Client.Shared.Persistence.XPointNetworkV1;
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
        // Local SQLCipher rejection is not evidence of a remote transport
        // failure. Match only the owned exception type, never its private text
        // or a generic wrapper's inner exception. These labels grant no reset,
        // retry or authority and do not disclose a path/key/account scope.
        var failure = exception is EntryGuardStoreOpenException
            ? "LocalEntryGuardRejected"
            : exception is XPointNetworkStoreOpenException
                ? "LocalNetworkStoreRejected"
            : exception is ClientMailboxTransportException { Failure: ClientMailboxTransportFailure.DependencyUnavailable }
                ? "OnionDependencyRejected"
            : exception is ClientMailboxDispatchOutcomeUnknownException
            ? exception.InnerException switch
            {
                null => "OnionCompletionUnknown",
                CryptographicException or OnionBoundaryException => "OnionReplyRejected",
                OperationCanceledException => "OnionTimeoutUnknown",
                HttpRequestException or IOException => "OnionTransportUnknown",
                _ => "OnionOutcomeUnknown"
            }
            : exception is DeepIdV2DirectoryProofUnavailableException proof
                ? proof.StatusCode == HttpStatusCode.TooManyRequests
                    ? "ProofRateLimited" : "ProofAuthorityUnavailable"
                : exception is DeepIdV2GenesisAdmissionUnavailableException admission
                    ? admission.StatusCode == HttpStatusCode.TooManyRequests
                        ? "AdmissionRateLimited" : "AdmissionAuthorityUnavailable"
                : exception is HttpServiceRequestTransportException transport
                    ? transport.Error switch
                    {
                        HttpServiceRequestTransportError.EndpointChanged => "HttpEndpointChanged",
                        HttpServiceRequestTransportError.UnexpectedMediaType => "HttpUnexpectedMediaType",
                        HttpServiceRequestTransportError.ResponseTooLarge => "HttpResponseTooLarge",
                        HttpServiceRequestTransportError.EmptyResponse => "HttpEmptyResponse",
                        _ => "TransportIo"
                    }
                    : exception is HttpIOException http
                        ? http.HttpRequestError switch
                        {
                            HttpRequestError.ResponseEnded => "HttpResponseEnded",
                            HttpRequestError.InvalidResponse => "HttpInvalidResponse",
                            _ => "HttpStreamIo"
                        }
                        : "TransportIo";
        return new InvalidOperationException(
            $"DID2 {stage} failed ({failure}).", exception);
    }
}
