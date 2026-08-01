using System.Net;
using Deep.Client.Maui.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class IncomingCallPollingBackoffTests
{
    [Theory]
    [InlineData(HttpRequestError.ResponseEnded)]
    [InlineData(HttpRequestError.ConnectionError)]
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.ProxyTunnelError)]
    [InlineData(HttpRequestError.VersionNegotiationError)]
    public void TransportFailuresAreTransient(HttpRequestError error)
    {
        var exception = new HttpRequestException(error, "sensitive route", null);

        Assert.True(IncomingCallPollingBackoff.IsTransient(
            exception,
            CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    public void HttpStatusClassificationIsFailClosed(
        HttpStatusCode status,
        bool expected)
    {
        var exception = new HttpRequestException("request failed", null, status);

        Assert.Equal(expected, IncomingCallPollingBackoff.IsTransient(
            exception,
            CancellationToken.None));
    }

    [Fact]
    public void TimeoutIsTransientButCallerCancellationIsNot()
    {
        Assert.True(IncomingCallPollingBackoff.IsTransient(
            new TaskCanceledException(),
            CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.False(IncomingCallPollingBackoff.IsTransient(
            new OperationCanceledException(cancellation.Token),
            cancellation.Token));
    }

    [Fact]
    public void CadenceCapsAndSuccessResetsWithOneShotTransitions()
    {
        var policy = new IncomingCallPollingBackoff();
        var transient = new HttpRequestException(
            HttpRequestError.ResponseEnded,
            "sensitive route",
            null);

        var first = policy.RecordFailure(transient, CancellationToken.None);
        var second = policy.RecordFailure(transient, CancellationToken.None);
        var third = policy.RecordFailure(transient, CancellationToken.None);
        var fourth = policy.RecordFailure(transient, CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(1), first.NextDelay);
        Assert.Equal(TimeSpan.FromMinutes(2), second.NextDelay);
        Assert.Equal(TimeSpan.FromMinutes(5), third.NextDelay);
        Assert.Equal(TimeSpan.FromMinutes(5), fourth.NextDelay);
        Assert.True(first.EnteredDegraded);
        Assert.False(second.EnteredDegraded);
        Assert.False(first.ShouldLogUnexpected);

        var recovered = policy.RecordSuccess();
        Assert.True(recovered.Recovered);
        Assert.Equal(TimeSpan.FromSeconds(30), recovered.NextDelay);
        Assert.False(policy.RecordSuccess().Recovered);
        Assert.Equal(TimeSpan.FromMinutes(1),
            policy.RecordFailure(transient, CancellationToken.None).NextDelay);
    }

    [Fact]
    public void UnexpectedFailureLogsOnlyOncePerDegradedEpisodeAndUsesMaximumDelay()
    {
        var policy = new IncomingCallPollingBackoff();

        var first = policy.RecordFailure(
            new InvalidOperationException("sensitive invariant"),
            CancellationToken.None);
        var second = policy.RecordFailure(
            new InvalidOperationException("sensitive invariant"),
            CancellationToken.None);

        Assert.False(first.IsTransient);
        Assert.True(first.EnteredDegraded);
        Assert.True(first.ShouldLogUnexpected);
        Assert.Equal(TimeSpan.FromMinutes(5), first.NextDelay);
        Assert.False(second.EnteredDegraded);
        Assert.False(second.ShouldLogUnexpected);

        _ = policy.RecordSuccess();
        Assert.True(policy.RecordFailure(
            new InvalidOperationException(),
            CancellationToken.None).ShouldLogUnexpected);
    }

    [Fact]
    public void StaleQueuedActivityCannotPassTheTimerStartGuard()
    {
        using var current = new CancellationTokenSource();
        using var replacement = new CancellationTokenSource();
        var captured = current.Token;

        Assert.True(IncomingCallPollingBackoff.IsCurrentActivity(current, captured));
        Assert.False(IncomingCallPollingBackoff.IsCurrentActivity(null, captured));
        Assert.False(IncomingCallPollingBackoff.IsCurrentActivity(
            replacement,
            captured));

        current.Cancel();
        Assert.False(IncomingCallPollingBackoff.IsCurrentActivity(current, captured));
    }
}
