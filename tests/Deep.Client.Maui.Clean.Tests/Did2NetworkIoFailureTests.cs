using Deep.Client.Maui.Services;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.Services.AccountDirectoryV2;
using System.Net;
using System.Security.Cryptography;

namespace Deep.Client.Maui.Clean.Tests;

public sealed class Did2NetworkIoFailureTests
{
    [Theory]
    [InlineData(HttpRequestError.ResponseEnded, "HttpResponseEnded")]
    [InlineData(HttpRequestError.InvalidResponse, "HttpInvalidResponse")]
    [InlineData(HttpRequestError.Unknown, "HttpStreamIo")]
    [InlineData((HttpRequestError)999, "HttpStreamIo")]
    public void FrameworkHttpStreamFailureHasClosedCategoryWithoutMessageLeak(HttpRequestError error, string expected)
    {
        var cause = new HttpIOException(error, "private endpoint and response bytes");
        var result = Did2NetworkIoFailure.AtStage("AccountProof", cause);
        Assert.Equal($"DID2 AccountProof failed ({expected}).", result.Message);
        Assert.Same(cause, result.InnerException);
        Assert.DoesNotContain("private", result.Message);
        Assert.Equal("DID2 AccountProof failed (TransportIo).",
            Did2NetworkIoFailure.AtStage("AccountProof", new IOException("private", cause)).Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "AdmissionRateLimited")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "AdmissionAuthorityUnavailable")]
    public void TypedAdmissionFailureIsNotAProofOrStreamFailure(HttpStatusCode status, string expected)
    {
        var cause = new DeepIdV2GenesisAdmissionUnavailableException(status, TimeSpan.FromSeconds(10));
        var result = Did2NetworkIoFailure.AtStage("AccountProof", cause);
        Assert.Equal($"DID2 AccountProof failed ({expected}).", result.Message);
        Assert.Same(cause, result.InnerException);
        Assert.Equal("DID2 AccountProof failed (TransportIo).",
            Did2NetworkIoFailure.AtStage("AccountProof", new IOException("private", cause)).Message);
    }

    [Theory]
    [InlineData(HttpServiceRequestTransportError.EndpointChanged, "HttpEndpointChanged")]
    [InlineData(HttpServiceRequestTransportError.UnexpectedMediaType, "HttpUnexpectedMediaType")]
    [InlineData(HttpServiceRequestTransportError.ResponseTooLarge, "HttpResponseTooLarge")]
    [InlineData(HttpServiceRequestTransportError.EmptyResponse, "HttpEmptyResponse")]
    [InlineData((HttpServiceRequestTransportError)999, "TransportIo")]
    public void TypedHttpBoundaryFailureDoesNotExposeItsMessage(HttpServiceRequestTransportError error, string expected)
    {
        var cause = new HttpServiceRequestTransportException(error, "private endpoint, headers and payload");
        var result = Did2NetworkIoFailure.AtStage("AccountProof", cause);
        Assert.Equal($"DID2 AccountProof failed ({expected}).", result.Message);
        Assert.Same(cause, result.InnerException);
        Assert.DoesNotContain("private", result.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "ProofRateLimited")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "ProofAuthorityUnavailable")]
    public void TypedDirectoryFailureCannotBeMistakenForIngressIo(HttpStatusCode code, string expected)
    {
        var cause = new DeepIdV2DirectoryProofUnavailableException(code, TimeSpan.FromSeconds(10));
        var result = Did2NetworkIoFailure.AtStage("PreKeyPublication", cause);
        Assert.Equal($"DID2 PreKeyPublication failed ({expected}).", result.Message);
        Assert.Same(cause, result.InnerException);
    }

    [Theory]
    [InlineData("AccountProof")]
    [InlineData("NetworkVerification")]
    [InlineData("PreKeyStaging")]
    [InlineData("PreKeyPublication")]
    [InlineData("ContactPublication")]
    [InlineData("ContactResolution")]
    public void IoFailureReportsOnlyClosedStageAndPreservesCause(string stage)
    {
        var cause = new IOException("private URL, request bytes, account and capability",
            new IOException("private certificate and endpoint"));
        var result = Did2NetworkIoFailure.AtStage(stage, cause);
        Assert.Equal($"DID2 {stage} failed (TransportIo).", result.Message);
        Assert.Same(cause, result.InnerException);
        Assert.DoesNotContain("private", result.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("private URL")]
    [InlineData("accountproof")]
    public void UnknownStageCannotEnterDisplayText(string stage)
    {
        var result = Assert.Throws<ArgumentException>(() =>
            Did2NetworkIoFailure.AtStage(stage, new IOException("private details")));
        Assert.DoesNotContain(stage.Length == 0 ? "private" : stage, result.Message);
    }

    [Theory]
    [InlineData("completion", "OnionCompletionUnknown")]
    [InlineData("crypto", "OnionReplyRejected")]
    [InlineData("cancellation", "OnionTimeoutUnknown")]
    [InlineData("http", "OnionTransportUnknown")]
    [InlineData("io", "OnionTransportUnknown")]
    [InlineData("other", "OnionOutcomeUnknown")]
    public void UnknownOnionCompletionHasClosedDisplayClassification(string kind, string expected)
    {
        const string secret = "private endpoint, account, capability, certificate and payload";
        Exception? inner = kind switch
        {
            "completion" => null,
            "crypto" => new CryptographicException(secret),
            "cancellation" => new OperationCanceledException(secret),
            "http" => new HttpRequestException(secret),
            "io" => new IOException(secret),
            _ => new InvalidOperationException(secret)
        };
        var cause = new ClientMailboxDispatchOutcomeUnknownException(secret, inner);
        var result = Did2NetworkIoFailure.AtStage("PreKeyPublication", cause);
        Assert.Equal($"DID2 PreKeyPublication failed ({expected}).", result.Message);
        Assert.Same(cause, result.InnerException);
        Assert.DoesNotContain("private", result.Message);
    }

    [Fact]
    public void ArbitraryIoWrapperCannotClaimAnAuthenticatedOnionOutcome()
    {
        var cause = new IOException("private", new ClientMailboxDispatchOutcomeUnknownException("private"));
        Assert.Equal("DID2 PreKeyPublication failed (TransportIo).",
            Did2NetworkIoFailure.AtStage("PreKeyPublication", cause).Message);
    }
}
