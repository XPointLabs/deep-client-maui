using System.Collections.Concurrent;
using System.Text.Json;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class CallSessionCoordinatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-11T08:00:00Z");

    [Fact]
    public void MalformedAuthenticatedPayloadCannotEscapeTheWebSignalBoundary()
    {
        var local = SessionId.CreateNew();
        var remote = SessionId.CreateNew();
        var malformed = Signal("malformed", local, remote, CallSignalType.Offer, "{\"description\":", Now);
        var wrongShape = Signal("wrong-shape", local, remote, CallSignalType.Answer, "[]", Now);
        var valid = Signal(
            "valid",
            local,
            remote,
            CallSignalType.IceCandidate,
            "{\"candidate\":{\"candidate\":\"candidate:1\"}}",
            Now);

        Assert.False(CallSessionCoordinator.TryCreateWebSignalMessage(malformed, out _));
        Assert.False(CallSessionCoordinator.TryCreateWebSignalMessage(wrongShape, out _));
        Assert.True(CallSessionCoordinator.TryCreateWebSignalMessage(valid, out var message));
        using var document = JsonDocument.Parse(message);
        Assert.Equal("signal", document.RootElement.GetProperty("command").GetString());
    }

    [Fact]
    public async Task DeclinedAndRemotelyCompletedCallsReleaseAllTrackedState()
    {
        using var runtime = ClientRuntime.CreateStubbed();
        var local = SessionId.CreateNew();
        var remote = SessionId.CreateNew();
        await SetActiveAccountAsync(runtime, local);
        var transport = new QueuedCallTransport();
        var coordinator = CreateCoordinator(runtime, transport);
        transport.Enqueue(Signal("declined", local, remote, CallSignalType.Offer, "{\"video\":false}", Now));

        var declined = Assert.Single(await coordinator.ReceiveIncomingOffersAsync());
        Assert.Equal(new(1, 1, 1, 1), coordinator.GetRetentionSnapshot());

        await coordinator.SendAsync(declined, CallSignalType.Bye, "{\"reason\":\"declined\"}");

        Assert.Equal(new(0, 0, 0, 0), coordinator.GetRetentionSnapshot());

        transport.Enqueue(Signal("completed", local, remote, CallSignalType.Offer, "{\"video\":false}", Now));
        var completed = Assert.Single(await coordinator.ReceiveIncomingOffersAsync());
        transport.Enqueue(Signal("completed", local, remote, CallSignalType.Bye, "{\"reason\":\"remote\"}", Now));

        var received = await coordinator.ReceiveForCallAsync(completed);

        Assert.Contains(received, static signal => signal.Type == CallSignalType.Bye);
        Assert.Equal(new(0, 0, 0, 0), coordinator.GetRetentionSnapshot());
    }

    [Fact]
    public async Task ActiveCallDropsAuthenticatedSignalsOutsideItsExactPartyAndConversationBinding()
    {
        using var runtime = ClientRuntime.CreateStubbed();
        var local = SessionId.CreateNew();
        var remote = SessionId.CreateNew();
        var other = SessionId.CreateNew();
        await SetActiveAccountAsync(runtime, local);
        var transport = new QueuedCallTransport();
        var coordinator = CreateCoordinator(runtime, transport);
        var offer = Signal(
            "bound-call",
            local,
            remote,
            CallSignalType.Offer,
            "{\"video\":false}",
            Now);
        transport.Enqueue(offer);
        var call = Assert.Single(await coordinator.ReceiveIncomingOffersAsync());

        transport.Enqueue(
            offer with
            {
                Sender = other,
                Type = CallSignalType.Bye,
                Payload = "{\"reason\":\"impostor\"}"
            },
            offer with
            {
                ConversationId = "wrong-conversation",
                Type = CallSignalType.Answer,
                Payload = "{}"
            },
            offer with
            {
                Type = CallSignalType.IceCandidate,
                Payload = "{\"candidate\":{\"candidate\":\"candidate:1\"}}"
            });

        var received = await coordinator.ReceiveForCallAsync(call);
        Assert.Collection(
            received,
            signal => Assert.Equal(CallSignalType.Offer, signal.Type),
            signal => Assert.Equal(CallSignalType.IceCandidate, signal.Type));
        Assert.All(received, signal =>
        {
            Assert.Equal(remote, signal.Sender);
            Assert.Equal(call.ConversationId, signal.ConversationId);
        });
    }

    [Fact]
    public async Task ExpiredAndFloodedCallSignalsAreDroppedOrStrictlyBounded()
    {
        using var runtime = ClientRuntime.CreateStubbed();
        var local = SessionId.CreateNew();
        var remote = SessionId.CreateNew();
        await SetActiveAccountAsync(runtime, local);
        var transport = new QueuedCallTransport();
        var coordinator = CreateCoordinator(runtime, transport, maxTrackedCalls: 4, maxSignalsPerCall: 3);
        transport.Enqueue(Signal(
            "expired",
            local,
            remote,
            CallSignalType.Offer,
            "{\"video\":false}",
            Now - TimeSpan.FromMinutes(6)));

        Assert.Empty(await coordinator.ReceiveIncomingOffersAsync());
        Assert.Equal(new(0, 0, 0, 0), coordinator.GetRetentionSnapshot());

        transport.Enqueue(Enumerable.Range(0, 10).Select(index =>
            Signal($"call-{index}", local, remote, CallSignalType.Answer, "{}", Now)).ToArray());
        await coordinator.ReceiveIncomingOffersAsync();

        var boundedCalls = coordinator.GetRetentionSnapshot();
        Assert.Equal(4, boundedCalls.PendingCalls);
        Assert.InRange(boundedCalls.PendingSignals, 0, 12);
        Assert.InRange(boundedCalls.LargestPendingCall, 0, 3);

        transport.Enqueue(Enumerable.Range(0, 10).Select(_ =>
            Signal("hot-call", local, remote, CallSignalType.IceCandidate, "{}", Now)).ToArray());
        await coordinator.ReceiveIncomingOffersAsync();

        var boundedSignals = coordinator.GetRetentionSnapshot();
        Assert.Equal(4, boundedSignals.PendingCalls);
        Assert.InRange(boundedSignals.PendingSignals, 0, 12);
        Assert.InRange(boundedSignals.LargestPendingCall, 0, 3);
    }

    private static CallSessionCoordinator CreateCoordinator(
        ClientRuntime runtime,
        QueuedCallTransport transport,
        int maxTrackedCalls = 64,
        int maxSignalsPerCall = 64) =>
        new(
            runtime,
            transport,
            transport,
            new FixedTimeProvider(Now),
            TimeSpan.FromMinutes(5),
            maxTrackedCalls,
            maxSignalsPerCall);

    private static Task SetActiveAccountAsync(ClientRuntime runtime, SessionId local) =>
        runtime.Store.SetAsync(
            SessionAccountService.ActiveAccountKey,
            new SessionAccount(local, "Alice", Now));

    private static CallSignalEnvelope Signal(
        string callId,
        SessionId recipient,
        SessionId sender,
        CallSignalType type,
        string payload,
        DateTimeOffset createdAt) =>
        new(callId, $"conversation-{callId}", sender, recipient, type, payload, createdAt, "authenticated", "signature");

    private sealed class QueuedCallTransport : ICallSignalingTransport, ICallIceConfigurationProvider
    {
        private readonly ConcurrentQueue<CallSignalEnvelope> inbound = new();

        public ConcurrentQueue<CallSignalEnvelope> Sent { get; } = new();

        public void Enqueue(params CallSignalEnvelope[] signals)
        {
            foreach (var signal in signals)
            {
                inbound.Enqueue(signal);
            }
        }

        public Task SendAsync(CallSignalEnvelope envelope, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sent.Enqueue(envelope);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CallSignalEnvelope>> ReceiveAsync(
            SessionId recipient,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new List<CallSignalEnvelope>();
            while (inbound.TryDequeue(out var signal))
            {
                result.Add(signal);
            }

            return Task.FromResult<IReadOnlyList<CallSignalEnvelope>>(result);
        }

        public Task<CallIceConfiguration> GetAsync(
            SessionId recipient,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CallIceConfiguration([], Now + TimeSpan.FromMinutes(5)));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
