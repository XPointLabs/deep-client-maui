using System.Net;

namespace Deep.Client.Maui.Services;

internal readonly record struct IncomingCallPollingFailureDecision(
    TimeSpan NextDelay,
    bool IsTransient,
    bool EnteredDegraded,
    bool ShouldLogUnexpected);

internal readonly record struct IncomingCallPollingSuccessDecision(
    TimeSpan NextDelay,
    bool Recovered);

internal sealed class IncomingCallPollingBackoff
{
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan[] TransientDelays =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5)
    ];

    private int failureIndex;
    private bool degraded;
    private bool unexpectedLogged;

    internal TimeSpan CurrentDelay { get; private set; } = InitialDelay;

    internal IncomingCallPollingFailureDecision RecordFailure(
        Exception exception,
        CancellationToken callerCancellation)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var transient = IsTransient(exception, callerCancellation);
        var enteredDegraded = !degraded;
        degraded = true;

        if (transient)
        {
            CurrentDelay = TransientDelays[
                Math.Min(failureIndex, TransientDelays.Length - 1)];
            failureIndex = Math.Min(failureIndex + 1, TransientDelays.Length - 1);
            return new IncomingCallPollingFailureDecision(
                CurrentDelay,
                true,
                enteredDegraded,
                false);
        }

        failureIndex = TransientDelays.Length - 1;
        CurrentDelay = TransientDelays[^1];
        var shouldLogUnexpected = !unexpectedLogged;
        unexpectedLogged = true;
        return new IncomingCallPollingFailureDecision(
            CurrentDelay,
            false,
            enteredDegraded,
            shouldLogUnexpected);
    }

    internal IncomingCallPollingSuccessDecision RecordSuccess()
    {
        var recovered = degraded;
        Reset();
        return new IncomingCallPollingSuccessDecision(CurrentDelay, recovered);
    }

    internal void Reset()
    {
        failureIndex = 0;
        degraded = false;
        unexpectedLogged = false;
        CurrentDelay = InitialDelay;
    }

    internal static bool IsCurrentActivity(
        CancellationTokenSource? current,
        CancellationToken captured) =>
        current is not null &&
        current.Token == captured &&
        !captured.IsCancellationRequested;

    internal static bool IsTransient(
        Exception exception,
        CancellationToken callerCancellation)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case HttpRequestException http:
                    if (http.StatusCode is { } status)
                    {
                        return status is HttpStatusCode.RequestTimeout or
                            HttpStatusCode.TooManyRequests ||
                            (int)status >= 500;
                    }
                    if (IsTransient(http.HttpRequestError))
                    {
                        return true;
                    }
                    if (http.HttpRequestError != HttpRequestError.Unknown)
                    {
                        return false;
                    }
                    break;
                case HttpIOException io:
                    return IsTransient(io.HttpRequestError);
                case OperationCanceledException:
                    return !callerCancellation.IsCancellationRequested;
                case TimeoutException:
                    return true;
                case WebException web:
                    return web.Status is
                        WebExceptionStatus.ConnectFailure or
                        WebExceptionStatus.ConnectionClosed or
                        WebExceptionStatus.KeepAliveFailure or
                        WebExceptionStatus.NameResolutionFailure or
                        WebExceptionStatus.ProxyNameResolutionFailure or
                        WebExceptionStatus.ReceiveFailure or
                        WebExceptionStatus.SendFailure or
                        WebExceptionStatus.Timeout;
            }
        }

        return false;
    }

    private static bool IsTransient(HttpRequestError error) => error is
        HttpRequestError.ConnectionError or
        HttpRequestError.NameResolutionError or
        HttpRequestError.ProxyTunnelError or
        HttpRequestError.ResponseEnded or
        HttpRequestError.VersionNegotiationError;
}
