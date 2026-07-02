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

public sealed class CallSessionCoordinator(
    ClientRuntime runtime,
    ICallSignalingTransport transport,
    ICallIceConfigurationProvider iceConfiguration)
{
    private readonly SemaphoreSlim receiveLock = new(1, 1);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<CallSignalEnvelope>> pendingByCall = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> announcedOffers = new(StringComparer.Ordinal);

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

    public Task SendAsync(
        CallDescriptor call,
        CallSignalType type,
        string payload,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync(
            new CallSignalEnvelope(
                call.CallId,
                call.ConversationId,
                call.LocalParty,
                call.RemoteParty,
                type,
                payload,
                DateTimeOffset.UtcNow),
            cancellationToken);

    public async Task<IReadOnlyList<CallSignalEnvelope>> ReceiveForCallAsync(
        CallDescriptor call,
        CancellationToken cancellationToken = default)
    {
        await PollAsync(call.LocalParty, cancellationToken).ConfigureAwait(false);
        if (!pendingByCall.TryGetValue(call.CallId, out var queue))
        {
            return [];
        }

        var result = new List<CallSignalEnvelope>();
        while (queue.TryDequeue(out var signal))
        {
            result.Add(signal);
        }

        if (queue.IsEmpty)
        {
            pendingByCall.TryRemove(call.CallId, out _);
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

        await PollAsync(account.SessionId, cancellationToken).ConfigureAwait(false);
        var calls = new List<CallDescriptor>();
        foreach (var pair in pendingByCall)
        {
            var offer = pair.Value.FirstOrDefault(static signal => signal.Type == CallSignalType.Offer);
            if (offer is null || !announcedOffers.TryAdd(offer.CallId, 0))
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

    private async Task PollAsync(SessionId local, CancellationToken cancellationToken)
    {
        await receiveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var inbound = await transport.ReceiveAsync(local, cancellationToken).ConfigureAwait(false);
            foreach (var signal in inbound)
            {
                pendingByCall
                    .GetOrAdd(signal.CallId, static _ => new ConcurrentQueue<CallSignalEnvelope>())
                    .Enqueue(signal);
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
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("video", out var value) && value.GetBoolean();
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
