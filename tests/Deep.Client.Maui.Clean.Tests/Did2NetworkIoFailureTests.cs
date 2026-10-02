using Deep.Client.Maui.Services;

namespace Deep.Client.Maui.Clean.Tests;

public sealed class Did2NetworkIoFailureTests
{
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
}
