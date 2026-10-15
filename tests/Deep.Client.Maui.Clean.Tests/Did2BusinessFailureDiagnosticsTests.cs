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
            Assert.Matches(@"\Akind="+expected+@"; depth=0; method=[A-Za-z0-9_.+<>`]{1,240}; line=[0-9]{1,5}\z",value);
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
        Assert.All(locations,value => Assert.Matches(@"\Akind=Io; depth=[0-3]; method=unavailable; line=0\z",value));
        Assert.Single(Did2BusinessFailureDiagnostics.Capture(new UnreadablePrivateFailure()));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowOwnedFailure(Exception error) => throw error;
    private sealed class UnreadablePrivateFailure : IOException
    {
        public override string Message => throw new InvalidOperationException("Must not read private details.");
        public override string ToString() => throw new InvalidOperationException("Must not stringify private details.");
    }
}
