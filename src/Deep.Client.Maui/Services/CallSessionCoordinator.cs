using System.Collections.Concurrent;
using System.Text.Json;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.Services;

public sealed record CallDescriptor(
    string CallId,
    string ConversationId,
    SessionId LocalParty,
    SessionId RemoteParty,
    string DisplayName,
    bool IsVideo,
    bool IsIncoming);

public sealed class CallSessionCoordinator
{
    internal const int DefaultMaxTrackedCalls = 32;
    internal const int DefaultMaxSignalsPerCall = 32;
    internal const int MaxSignalPayloadCharacters = 64 * 1024;
    private static readonly TimeSpan DefaultSignalRetention = TimeSpan.FromMinutes(5);
    private readonly ClientRuntime runtime;
    private readonly ICallSignalingTransport transport;
    private readonly ICallIceConfigurationProvider iceConfiguration;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan signalRetention;
    private readonly int maxTrackedCalls;
    private readonly int maxSignalsPerCall;
    private readonly SemaphoreSlim receiveLock = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingCallSignals> pendingByCall = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> announcedOffers = new(StringComparer.Ordinal);

    public CallSessionCoordinator(
        ClientRuntime runtime,
        ICallSignalingTransport transport,
        ICallIceConfigurationProvider iceConfiguration)
        : this(
            runtime,
            transport,
            iceConfiguration,
            TimeProvider.System,
            DefaultSignalRetention,
            DefaultMaxTrackedCalls,
            DefaultMaxSignalsPerCall)
    {
    }

    internal CallSessionCoordinator(
        ClientRuntime runtime,
        ICallSignalingTransport transport,
        ICallIceConfigurationProvider iceConfiguration,
        TimeProvider timeProvider,
        TimeSpan signalRetention,
        int maxTrackedCalls,
        int maxSignalsPerCall)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(iceConfiguration);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (signalRetention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(signalRetention));
        }
        if (maxTrackedCalls <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTrackedCalls));
        }
        if (maxSignalsPerCall <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSignalsPerCall));
        }

        this.runtime = runtime;
        this.transport = transport;
        this.iceConfiguration = iceConfiguration;
        this.timeProvider = timeProvider;
        this.signalRetention = signalRetention;
        this.maxTrackedCalls = maxTrackedCalls;
        this.maxSignalsPerCall = maxSignalsPerCall;
    }

    public async Task<CallDescriptor> CreateOutgoingAsync(
        SessionId remote,
        string conversationId,
        string displayName,
        bool isVideo,
        CancellationToken cancellationToken = default)
    {
        var account = await RequireAccountAsync(cancellationToken);
        return new CallDescriptor(
            Guid.NewGuid().ToString("N"),
            conversationId,
            account.SessionId,
            remote,
            displayName,
            isVideo,
            IsIncoming: false);
    }

    public async Task<CallIceConfiguration> GetIceConfigurationAsync(
        SessionId local,
        CancellationToken cancellationToken = default) =>
        await iceConfiguration.GetAsync(local, cancellationToken).ConfigureAwait(false);

    public async Task SendAsync(
        CallDescriptor call,
        CallSignalType type,
        string payload,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await transport.SendAsync(
                new CallSignalEnvelope(
                    call.CallId,
                    call.ConversationId,
                    call.LocalParty,
                    call.RemoteParty,
                    type,
                    payload,
                    timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (type == CallSignalType.Bye)
            {
                ReleaseCall(call.CallId);
            }
        }
    }

    public async Task<IReadOnlyList<CallSignalEnvelope>> ReceiveForCallAsync(
        CallDescriptor call,
        CancellationToken cancellationToken = default)
    {
        await PollAsync(call.LocalParty, call.CallId, cancellationToken).ConfigureAwait(false);
        if (!pendingByCall.TryGetValue(call.CallId, out var queue))
        {
            return [];
        }

        var result = new List<CallSignalEnvelope>();
        while (queue.Signals.TryDequeue(out var signal))
        {
            result.Add(signal);
        }

        if (result.Any(static signal => signal.Type == CallSignalType.Bye))
        {
            ReleaseCall(call.CallId);
        }
        else if (queue.Signals.IsEmpty)
        {
            ((ICollection<KeyValuePair<string, PendingCallSignals>>)pendingByCall)
                .Remove(new KeyValuePair<string, PendingCallSignals>(call.CallId, queue));
        }

        return result;
    }

    public async Task<IReadOnlyList<CallDescriptor>> ReceiveIncomingOffersAsync(
        CancellationToken cancellationToken = default)
    {
        var account = await runtime.Accounts.GetActiveAccountAsync(cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return [];
        }

        await PollAsync(account.SessionId, activeCallId: null, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var calls = new List<CallDescriptor>();
        foreach (var pair in pendingByCall)
        {
            var offer = pair.Value.Signals.FirstOrDefault(static signal => signal.Type == CallSignalType.Offer);
            if (offer is null)
            {
                continue;
            }

            EnsureAnnouncementCapacity(offer.CallId);
            if (!announcedOffers.TryAdd(offer.CallId, now))
            {
                continue;
            }

            calls.Add(new CallDescriptor(
                offer.CallId,
                offer.ConversationId,
                account.SessionId,
                offer.Sender,
                ResolveDisplayName(offer.Sender),
                ReadVideoFlag(offer.Payload),
                IsIncoming: true));
        }

        return calls;
    }

    public void ReleaseAnnouncement(string callId) => announcedOffers.TryRemove(callId, out _);

    public void ReleaseCall(string callId)
    {
        if (string.IsNullOrWhiteSpace(callId))
        {
            return;
        }

        pendingByCall.TryRemove(callId, out _);
        announcedOffers.TryRemove(callId, out _);
    }

    private async Task PollAsync(
        SessionId local,
        string? activeCallId,
        CancellationToken cancellationToken)
    {
        await receiveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var inbound = await transport.ReceiveAsync(local, cancellationToken).ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            PruneExpiredState(now);
            foreach (var signal in inbound)
            {
                if (!IsAcceptedSignal(signal, local, now))
                {
                    continue;
                }

                if (signal.Type == CallSignalType.Bye
                    && !string.Equals(signal.CallId, activeCallId, StringComparison.Ordinal))
                {
                    ReleaseCall(signal.CallId);
                    continue;
                }

                AddPendingSignal(signal, activeCallId, now);
            }
        }
        finally
        {
            receiveLock.Release();
        }
    }

    private async Task<SessionAccount> RequireAccountAsync(CancellationToken cancellationToken) =>
        await runtime.Accounts.GetActiveAccountAsync(cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("Для звонка требуется активный аккаунт.");

    private static string ResolveDisplayName(SessionId sender) =>
        $"Deep {sender.Value[^6..]}";

    private static bool ReadVideoFlag(string payload)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(payload) || payload.Length > MaxSignalPayloadCharacters)
            {
                return false;
            }

            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("video", out var value)
                && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                && value.GetBoolean();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool TryCreateWebSignalMessage(CallSignalEnvelope signal, out string message)
    {
        message = string.Empty;
        var signalType = signal.Type switch
        {
            CallSignalType.Offer => "offer",
            CallSignalType.Answer => "answer",
            CallSignalType.IceCandidate => "ice",
            _ => null
        };
        if (signalType is null
            || string.IsNullOrWhiteSpace(signal.Payload)
            || signal.Payload.Length > MaxSignalPayloadCharacters)
        {
            return false;
        }

        try
        {
            using var payloadDocument = JsonDocument.Parse(signal.Payload);
            if (payloadDocument.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            message = JsonSerializer.Serialize(new
            {
                command = "signal",
                signalType,
                payload = payloadDocument.RootElement
            });
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    internal CallRetentionSnapshot GetRetentionSnapshot() =>
        new(
            pendingByCall.Count,
            announcedOffers.Count,
            pendingByCall.Sum(static pair => pair.Value.Signals.Count),
            pendingByCall.Select(static pair => pair.Value.Signals.Count).DefaultIfEmpty().Max());

    private bool IsAcceptedSignal(CallSignalEnvelope signal, SessionId local, DateTimeOffset now)
    {
        if (signal is null
            || signal.Recipient != local
            || string.IsNullOrWhiteSpace(signal.CallId)
            || signal.CallId.Length > 128
            || string.IsNullOrWhiteSpace(signal.ConversationId)
            || signal.ConversationId.Length > 256
            || !Enum.IsDefined(signal.Type)
            || signal.Payload is null
            || signal.Payload.Length > MaxSignalPayloadCharacters)
        {
            return false;
        }

        return (now - signal.CreatedAt).Duration() <= signalRetention;
    }

    private void AddPendingSignal(CallSignalEnvelope signal, string? activeCallId, DateTimeOffset now)
    {
        if (!pendingByCall.TryGetValue(signal.CallId, out var pending))
        {
            if (!EnsurePendingCallCapacity(activeCallId, now))
            {
                return;
            }

            pending = pendingByCall.GetOrAdd(signal.CallId, _ => new PendingCallSignals(now));
        }

        pending.Touch(now);
        pending.Signals.Enqueue(signal);
        while (pending.Signals.Count > maxSignalsPerCall)
        {
            pending.Signals.TryDequeue(out _);
        }
    }

    private void PruneExpiredState(DateTimeOffset now)
    {
        foreach (var pair in pendingByCall)
        {
            if (now - pair.Value.LastTouched > signalRetention)
            {
                ReleaseCall(pair.Key);
            }
        }

        foreach (var pair in announcedOffers)
        {
            if (now - pair.Value > signalRetention)
            {
                ((ICollection<KeyValuePair<string, DateTimeOffset>>)announcedOffers).Remove(pair);
            }
        }
    }

    private bool EnsurePendingCallCapacity(string? activeCallId, DateTimeOffset now)
    {
        PruneExpiredState(now);
        while (pendingByCall.Count >= maxTrackedCalls)
        {
            var oldest = pendingByCall
                .Where(pair => !string.Equals(pair.Key, activeCallId, StringComparison.Ordinal))
                .OrderBy(static pair => pair.Value.LastTouched)
                .Select(static pair => pair.Key)
                .FirstOrDefault();
            if (oldest is null)
            {
                return false;
            }

            ReleaseCall(oldest);
        }

        return true;
    }

    private void EnsureAnnouncementCapacity(string callId)
    {
        while (announcedOffers.Count >= maxTrackedCalls)
        {
            var oldest = announcedOffers
                .Where(pair => !string.Equals(pair.Key, callId, StringComparison.Ordinal))
                .OrderBy(static pair => pair.Value)
                .Select(static pair => pair.Key)
                .FirstOrDefault();
            if (oldest is null)
            {
                return;
            }

            ReleaseCall(oldest);
        }
    }

    private sealed class PendingCallSignals(DateTimeOffset createdAt)
    {
        private long lastTouchedUnixMilliseconds = createdAt.ToUnixTimeMilliseconds();

        public ConcurrentQueue<CallSignalEnvelope> Signals { get; } = new();

        public DateTimeOffset LastTouched =>
            DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref lastTouchedUnixMilliseconds));

        public void Touch(DateTimeOffset touchedAt) =>
            Interlocked.Exchange(ref lastTouchedUnixMilliseconds, touchedAt.ToUnixTimeMilliseconds());
    }

    internal sealed record CallRetentionSnapshot(
        int PendingCalls,
        int AnnouncedOffers,
        int PendingSignals,
        int LargestPendingCall);
}
