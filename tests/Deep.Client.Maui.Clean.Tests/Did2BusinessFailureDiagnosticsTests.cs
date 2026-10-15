using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Clean.Tests;

public sealed class Did2BusinessFailureDiagnosticsTests
{
    [Theory]
    [InlineData("protected", "ProtectedState")]
    [InlineData("canonical", "ProtectedState")]
    [InlineData("timeout", "Timeout")]
    [InlineData("cancelled", "Cancelled")]
    [InlineData("http", "HttpTransport")]
    [InlineData("io", "Io")]
    [InlineData("other", "Other")]
    [InlineData("unknown", "OutcomeUnknown")]
    [InlineData("mailbox", "MailboxTransport")]
    public void OnlyClosedCategoryAndCodeLocationLeaveAnActualCaughtException(string kind,string expected)
    {
        const string privateText = "private credential payload and endpoint";
        Exception failure = kind switch {
            "protected" => new CryptographicException(privateText),
            "canonical" => new InvalidDataException(privateText),
            "timeout" => new TimeoutException(privateText),
            "cancelled" => new OperationCanceledException(privateText),
            "http" => new HttpRequestException(privateText),
            "io" => new IOException(privateText),
            "unknown" => new ClientMailboxDispatchOutcomeUnknownException(privateText),
            "mailbox" => new ClientMailboxTransportException(ClientMailboxTransportFailure.DependencyUnavailable,false,privateText),
            _ => new InvalidOperationException(privateText)
        };
        IReadOnlyList<string> locations;
        try { ThrowOwnedFailure(failure); throw new InvalidOperationException(); }
        catch (Exception error) { locations = Did2BusinessFailureDiagnostics.Capture(error); }
        Assert.InRange(locations.Count,1,4);
        Assert.All(locations, value => {
            Assert.Matches(@"\Akind="+expected+@"; check=Unclassified; depth=0; method=[A-Za-z0-9_.+<>`]{1,240}; line=[0-9]{1,5}; il=(-1|[0-9]{1,7})\z",value);
            Assert.DoesNotContain(privateText,value); Assert.DoesNotContain("/",value); Assert.DoesNotContain("\\",value);
        });
        Assert.Contains(locations,value => value.Contains(nameof(ThrowOwnedFailure),StringComparison.Ordinal));
    }

    [Fact]
    public void PrivateMessageAndToStringAreNeverReadAndInnerChainIsBounded()
    {
        Exception failure = new UnreadablePrivateFailure();
        for (var i = 0; i < 12; i++) failure = new IOException("private",failure);
        var locations = Did2BusinessFailureDiagnostics.Capture(failure);
        Assert.Equal(4,locations.Count);
        Assert.All(locations,value => Assert.Matches(@"\Akind=Io; check=Unclassified; depth=[0-3]; method=unavailable; line=0; il=-1\z",value));
        Assert.Single(Did2BusinessFailureDiagnostics.Capture(new UnreadablePrivateFailure()));
        Assert.Single(Did2BusinessFailureDiagnostics.Capture(new UnreadablePrivateCryptoFailure()));
    }

    [Theory]
    [InlineData("Contact control requires two current DID2 directory proofs.", "PairFreshness")]
    [InlineData("Contact control requires distinct current accounts on one network.", "PairNetwork")]
    [InlineData("A fresh current DID2 directory checkpoint is required for the rendezvous.", "XurFreshness")]
    [InlineData("The rendezvous has no current DID2 checkpoint.", "XurFreshness")]
    [InlineData("The inbound rendezvous does not cover the entire authenticated time interval.", "XurWindow")]
    [InlineData("The inbound rendezvous differs from its current DID2 device authority.", "XurAuthority")]
    [InlineData("The inbound rendezvous current device signature is invalid.", "XurSignature")]
    [InlineData("The rendezvous trusted-time interval overflowed.", "ClockOverflow")]
    [InlineData("Contact control crossed a protected clock discontinuity.", "ClockContinuity")]
    [InlineData("ContactHello differs from its exact current DID2 endpoints.", "HelloMetadata")]
    [InlineData("ContactHello differs from its PQ-bound safety number.", "HelloSafety")]
    [InlineData("ContactHello is outside the issuer/device validity interval.", "HelloCreation")]
    [InlineData("The private reply route differs from its exact current contact sender endpoint.", "MailboxEndpoint")]
    [InlineData("private credential payload and endpoint", "Unclassified")]
    [InlineData("Permanent contact trusted interval is inverted.", "PermanentInterval")]
    [InlineData("Permanent read issuance exceeds trusted lower time.", "PermanentIssuedLower")]
    [InlineData("Permanent read expiry does not cover trusted upper time.", "PermanentRequestUpper")]
    [InlineData("Permanent read lifetime exceeds 120 seconds.", "PermanentRequestLifetime")]
    [InlineData("Permanent contact object begins after trusted lower time.", "PermanentObjectLower")]
    [InlineData("Permanent contact object expiry does not cover trusted upper time.", "PermanentObjectUpper")]
    public void OnlyFixedOwnedGuardLiteralHasAClosedDiagnosticLabel(string message,string expected)
    {
        var entry = Assert.Single(Did2BusinessFailureDiagnostics.Capture(new CryptographicException(message)));
        Assert.Equal($"kind=ProtectedState; check={expected}; depth=0; method=unavailable; line=0; il=-1",entry);
        Assert.DoesNotContain(message,entry);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowOwnedFailure(Exception error) => throw error;
    private sealed class UnreadablePrivateFailure : IOException
    {
        public override string Message => throw new InvalidOperationException("Must not read private details.");
        public override string ToString() => throw new InvalidOperationException("Must not stringify private details.");
    }
    private sealed class UnreadablePrivateCryptoFailure : CryptographicException
    {
        public override string Message => throw new InvalidOperationException("Must not read a custom private getter.");
        public override string ToString() => throw new InvalidOperationException("Must not stringify private details.");
    }
}
