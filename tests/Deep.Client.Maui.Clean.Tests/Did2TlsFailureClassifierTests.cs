using System.Security.Authentication;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Deep.Client.Maui.Services;

namespace Deep.Client.Maui.Clean.Tests;

public sealed class Did2TlsFailureClassifierTests
{
    [Theory]
    [InlineData(SslPolicyErrors.None, X509ChainStatusFlags.NoError, "PlatformChainAccepted")]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors, X509ChainStatusFlags.PartialChain | X509ChainStatusFlags.RevocationStatusUnknown, "PartialChain RevocationStatusUnknown")]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch, X509ChainStatusFlags.NoError, "RemoteCertificateNameMismatch")]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors, X509ChainStatusFlags.InvalidBasicConstraints, "OtherChainError")]
    public void SeparateDiagnosticAlwaysRejectsAndReportsOnlyClosedFlags(
        SslPolicyErrors errors, X509ChainStatusFlags flags, string expected)
    {
        string? result = null;
        Assert.False(Did2TlsFailureClassifier.ObserveAndReject(errors, flags, value => result = value));
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task SeparateDiagnosticRejectsNonPublicOrNonCanonicalOriginBeforeConnect()
    {
        foreach (var address in new[] { "http://registry.example/", "https://localhost/", "https://registry.example:8443/", "https://registry.example/private" })
            await Assert.ThrowsAsync<ArgumentException>(() =>
                Did2TlsFailureClassifier.ObserveRejectedHandshakeAsync(new Uri(address), default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Did2TlsFailureClassifier.ObserveRejectedHandshakeAsync(new Uri("https://registry.example/"), cancelled.Token));
    }

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
