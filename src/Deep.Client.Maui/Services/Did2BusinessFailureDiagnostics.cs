using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Deep.Client.Shared.Services;
using Deep.Protocol.ApplicationCore;

namespace Deep.Client.Maui.Services;

// Display/log metadata only. Never emits exception text or file paths. Only an
// exact built-in CryptographicException message/closed reason is compared with
// fixed owned literals; unknown text and custom getters never enter the output.
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
                OverflowException => "ArithmeticOverflow",
                ObjectDisposedException => "Disposed",
                InvalidOperationException => "InvalidOperation",
                _ => "Other"
            };
            var check = ContactCheck(error);
            var trace = new StackTrace(error,true); var count = 0;
            var observedOwners = new HashSet<Type>();
            for (var index = 0; index < Math.Min(trace.FrameCount,48) && count < 4; index++)
            {
                var frame = trace.GetFrame(index); var method = frame?.GetMethod();
                var type = method?.DeclaringType; var space = type?.Namespace;
                if (space is null || !(space.StartsWith("Deep.Client.Shared",StringComparison.Ordinal) ||
                    space.StartsWith("Deep.Protocol",StringComparison.Ordinal) ||
                    space.StartsWith("Deep.Client.Maui",StringComparison.Ordinal))) continue;
                // Keep the original four-frame privacy bound, but do not let
                // several helpers on one owner hide the actual caller boundary.
                var owner = typeof(System.Runtime.CompilerServices.IAsyncStateMachine).IsAssignableFrom(type)
                    ? type!.DeclaringType ?? type! : type!;
                if (!observedOwners.Add(owner)) continue;
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
    private static string ContactCheck(Exception error) => error is ApplicationCoreFormatException
        { Stage: ApplicationCoreValidationStage.CryptographicVerification, Rejection: ApplicationCoreRejection.VerificationFailed } prekey
        ? ReplicaPreKeyCheck(prekey.Message)
        : error.GetType() != typeof(CryptographicException) ? "Unclassified" : error.Message switch
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
            "Permanent contact trusted interval is inverted." => "PermanentInterval",
            "Permanent read issuance exceeds trusted lower time." => "PermanentIssuedLower",
            "Permanent read expiry does not cover trusted upper time." => "PermanentRequestUpper",
            "Permanent read lifetime exceeds 120 seconds." => "PermanentRequestLifetime",
            "Permanent contact object begins after trusted lower time." => "PermanentObjectLower",
            "Permanent contact object expiry does not cover trusted upper time." => "PermanentObjectUpper",
            "Owned claim route clock precedes the protected freshness reading." => "ClaimRouteSample",
            "Owned claim route clock changed its protected boot." => "ClaimRouteBoot",
            "Owned claim read issuance exceeds trusted lower time." => "ClaimIssuedLower",
            "Owned claim read expiry does not cover trusted upper time." => "ClaimRequestUpper",
            "Duplicate device prekey descriptors are forbidden." => "ContactPrekeyDuplicate",
            "New contact issuance requires a route anchored at the current verified directory head." => "ContactIssuanceAnchor",
            "The object requires the exact authorized invite kind and route generation." => "ContactInviteGeneration",
            "The exact active-device prekey descriptor is absent." => "ContactPrekeyDevice",
            "The prekey descriptor is outside the exact current device scope or interval." => "ContactPrekeyScope",
            "Contact validity cannot cover the complete trusted time interval." => "ContactObjectValidity",
            "The contact object is not the exact current reusable route." => "ContactObjectRoute",
            "Contact authoring crossed a trusted clock discontinuity." => "ContactAuthorClock",
            "Route custody is not the current authorized DID2 publisher device." => "ContactOwnedPublisher",
            "The contact bundle is not the exact owned DID2 delegation." => "ContactOwnedDelegation",
            "libsodium initialization failed." => "ContactSodiumInit",
            "libsodium failed to derive an Ed25519 public key." => "ContactSodiumKey",
            "libsodium failed to create an Ed25519 signature." => "ContactSodiumSign",
            "The completed route differs from its closed retained issuance." => "ContactRetainedRoute",
            "Retained issuance exceeds the completed route's independently verified floor." => "ContactRetainedFloor",
            "Contact minimum checkpoint differs from its signed route issuance anchor or current verified floor." => "ContactBundleAnchor",
            "Contact successor does not advance its exact signed predecessor." => "ContactSuccessorAdvance",
            "Contact successor requires separate identity, service, profile or policy rollover." => "ContactSuccessorScope",
            "Contact successor does not bind its exact current route generation." => "ContactSuccessorGeneration",
            "Contact successor changed its exact reusable invite lineage." => "ContactSuccessorInvite",
            "The current DID2 recipient and network do not share exact directory/view authority." => "ContactCurrentContext",
            "The route publisher is not an active DID2 device." => "ContactPublisherInactive",
            "Mailbox grant is outside current issuer/network/time authority." => MailboxGrantCheck(error),
            _ => RouteTimeCheck(error.Message)
        };

    private static string MailboxGrantCheck(Exception error) => error.Data["Deep.MailboxAuthority.Check"] switch
    {
        "Lifecycle" => "MailboxGrantLifecycle", "Network" => "MailboxGrantNetwork",
        "Generation" => "MailboxGrantGeneration", "IssuerKey" => "MailboxGrantIssuerKey",
        "IssuerNotBefore" => "MailboxGrantIssuerNotBefore", "GrantNotBefore" => "MailboxGrantNotBefore",
        "GrantExpiry" => "MailboxGrantExpiry", "PolicyExpiry" => "MailboxGrantPolicyExpiry",
        "NetworkExpiry" => "MailboxGrantNetworkExpiry", "Lifetime" => "MailboxGrantLifetime",
        "Signature" => "MailboxGrantSignature", _ => "Unclassified"
    };

    // Display only; no label grants retry, renewal, noncommit or currentness.
    // ApplicationCoreFormatException is sealed; arbitrary exception getters
    // are never consulted for these exact Protocol-owned literals.
    internal static string ReplicaPreKeyCheck(string message) => message switch
    {
        "XPP1 inventory begins after the authenticated lower time bound." => "PreKeyInventoryNotBefore",
        "XPP1 inventory does not cover the authenticated upper time bound." => "PreKeyInventoryExpiry",
        "XPP1 XPS1/XPI1 differ from the current DID2 device authority." => "PreKeyInventoryBinding",
        _ => "Unclassified"
    };

    private static string RouteTimeCheck(string message)
    {
        // Compare complete literals; never parse or copy caller text into a label.
        foreach (var artifact in new[] { "XNA1", "XNV1", "XNH1", "ADH1", "DeviceCertificate", "DCA1", "PMT2",
            "XRA1", "PMS2", "XRC1", "XSS1", "XIR1V2", "XRR1", "ThresholdAuthoring", "ResolveRequest" })
            foreach (var reason in new[] { "InvalidInterval", "NotBefore", "Expiry" })
                if (message == $"DID2 route time coverage failed ({artifact}; {reason}).")
                    return "RouteTime" + artifact + reason;
        return "Unclassified";
    }
}
