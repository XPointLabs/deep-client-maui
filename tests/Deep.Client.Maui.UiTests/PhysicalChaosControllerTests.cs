using System.Text.Json;

namespace Deep.Client.Maui.UiTests;

public sealed class PhysicalChaosControllerTests
{
    [Fact]
    public void Begin_status_requires_exact_v2_schema_fault_operation_and_deadline()
    {
        var status = PhysicalChaosController.ChaosStatus.ParseExact(StatusJson(
            running: true,
            operation: "mailbox-store",
            fault: "post-durable-response-drop",
            armed: true,
            consumed: false,
            started: 1_000,
            deadline: 301_000,
            expires: 300));

        status.AssertBegin("post-durable-response-drop", "mailbox-store", 300);
        Assert.Throws<InvalidOperationException>(() =>
            status.AssertBegin("pre-dispatch-outage", "mailbox-store", 300));
    }

    [Fact]
    public void Store_fault_counters_are_exact_not_minimums()
    {
        var status = PhysicalChaosController.ChaosStatus.ParseExact(StatusJson(
            running: true,
            operation: "mailbox-store",
            fault: "pre-dispatch-outage",
            armed: false,
            consumed: true,
            attempts: 2,
            dispatches: 1,
            successes: 1,
            injected: 1,
            preOutage: 1,
            started: 1_000,
            deadline: 301_000));

        status.AssertConsumed("pre-dispatch-outage", "mailbox-store",
            attempts: 2, dispatches: 1, successes: 1,
            postDrop: 0, preOutage: 1, ackDrop: 0);
        Assert.Equal(
            "running=True;armed=False;consumed=True;requests=0;attempts=2;" +
            "dispatches=1;successes=1;injected=1;postDrop=0;preOutage=1;ackDrop=0",
            status.SanitizedLifecycle);
        Assert.Throws<InvalidOperationException>(() =>
            status.AssertConsumed("pre-dispatch-outage", "mailbox-store",
                attempts: 3, dispatches: 1, successes: 1,
                postDrop: 0, preOutage: 1, ackDrop: 0));
    }

    [Fact]
    public void Ack_fault_is_a_distinct_operation_and_counter()
    {
        var status = PhysicalChaosController.ChaosStatus.ParseExact(StatusJson(
            running: true,
            operation: "mailbox-ack",
            fault: "post-durable-ack-response-drop",
            armed: false,
            consumed: true,
            attempts: 1,
            dispatches: 1,
            successes: 1,
            injected: 1,
            ackDrop: 1,
            started: 1_000,
            deadline: 301_000));

        status.AssertConsumed("post-durable-ack-response-drop", "mailbox-ack",
            attempts: 1, dispatches: 1, successes: 1,
            postDrop: 0, preOutage: 0, ackDrop: 1);

        var restarted = PhysicalChaosController.ChaosStatus.ParseExact(StatusJson(
            running: true,
            operation: "mailbox-ack",
            fault: "post-durable-ack-response-drop",
            armed: false,
            consumed: true,
            attempts: 2,
            dispatches: 2,
            successes: 2,
            injected: 1,
            ackDrop: 1,
            started: 1_000,
            deadline: 301_000));
        restarted.AssertConsumed("post-durable-ack-response-drop", "mailbox-ack",
            attempts: 2, dispatches: 2, successes: 2,
            postDrop: 0, preOutage: 0, ackDrop: 1);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            restarted.AssertCanStillReachConsumedCounters(
                "post-durable-ack-response-drop", "mailbox-ack",
                attempts: 1, dispatches: 1, successes: 1,
                postDrop: 0, preOutage: 0, ackDrop: 1));
        Assert.Contains(restarted.SanitizedLifecycle, exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Bounded_wait_accepts_the_clean_armed_state_before_consumption()
    {
        var armed = PhysicalChaosController.ChaosStatus.ParseExact(StatusJson(
            running: true,
            operation: "mailbox-store",
            fault: "pre-dispatch-outage",
            armed: true,
            consumed: false,
            started: 1_000,
            deadline: 301_000,
            expires: 300));

        armed.AssertCanStillReachConsumedCounters(
            "pre-dispatch-outage", "mailbox-store",
            attempts: 1, dispatches: 0, successes: 0,
            postDrop: 0, preOutage: 1, ackDrop: 0);

        var impossible = PhysicalChaosController.ChaosStatus.ParseExact(StatusJson(
            running: true,
            operation: "mailbox-store",
            fault: "pre-dispatch-outage",
            armed: true,
            consumed: false,
            injected: 1,
            started: 1_000,
            deadline: 301_000,
            expires: 300));
        Assert.Throws<InvalidOperationException>(() =>
            impossible.AssertCanStillReachConsumedCounters(
                "pre-dispatch-outage", "mailbox-store",
                attempts: 1, dispatches: 0, successes: 0,
                postDrop: 0, preOutage: 1, ackDrop: 0));
    }

    [Fact]
    public void Off_baseline_and_property_set_are_closed()
    {
        PhysicalChaosController.ChaosStatus.ParseExact(StatusJson()).AssertOffBaseline();
        var extra = StatusJson().Replace("}", ",\"unexpected\":0}", StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() =>
            PhysicalChaosController.ChaosStatus.ParseExact(extra));
    }

    private static string StatusJson(
        bool running = false,
        string? operation = null,
        string? fault = null,
        bool armed = false,
        bool consumed = false,
        long attempts = 0,
        long dispatches = 0,
        long successes = 0,
        long injected = 0,
        long postDrop = 0,
        long preOutage = 0,
        long ackDrop = 0,
        long started = 0,
        long deadline = 0,
        long expires = 0) => JsonSerializer.Serialize(new
        {
            schema = "deep-survival-resend-chaos-status.v2",
            mode = "development-only",
            running,
            operation,
            fault,
            armed,
            consumed,
            requestCount = 0,
            operationAttemptCount = attempts,
            operationUpstreamDispatchCount = dispatches,
            operationUpstreamSuccessCount = successes,
            injectedFaultCount = injected,
            postDurableResponseDropCount = postDrop,
            preDispatchOutageCount = preOutage,
            postDurableAckResponseDropCount = ackDrop,
            faultWindowStartedUnixMilliseconds = started,
            faultWindowDeadlineUnixMilliseconds = deadline,
            expiresInSeconds = expires,
            identifiersIncluded = false,
            payloadInspected = false
        });
}
