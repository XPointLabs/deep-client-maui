using System.Security.Authentication;
using Deep.Client.Maui.Services;

namespace Deep.Client.Maui.Clean.Tests;

public sealed class Did2TlsFailureClassifierTests
{
    [Fact]
    public void KnownChainFlagsAreClosedAndPrivateMessagesNeverEscape()
    {
        var exception = new HttpRequestException("https://private.invalid/request",
            new AuthenticationException("Private certificate, PartialChain, RevocationStatusUnknown."));
        Assert.Equal("PartialChain RevocationStatusUnknown", Did2TlsFailureClassifier.Classify(exception));
    }

    [Fact]
    public void UnknownOversizedOrWrongTypedMessagesDoNotBecomeClassifications()
    {
        Assert.Equal("Unknown", Did2TlsFailureClassifier.Classify(new AuthenticationException("private details")));
        Assert.Equal("Unknown", Did2TlsFailureClassifier.Classify(new Exception("Revoked")));
        Assert.Equal("Unknown", Did2TlsFailureClassifier.Classify(new AuthenticationException(new string('a', 4096) + " Revoked")));
        Assert.Equal("Unknown", Did2TlsFailureClassifier.Classify(new AuthenticationException("NotRevoked")));
    }

    [Fact]
    public void InnerChainDepthIsBounded()
    {
        Exception exception = new AuthenticationException("Revoked");
        for (var count = 0; count < 8; count++) exception = new Exception("private details", exception);
        Assert.Equal("Unknown", Did2TlsFailureClassifier.Classify(exception));
    }
}
