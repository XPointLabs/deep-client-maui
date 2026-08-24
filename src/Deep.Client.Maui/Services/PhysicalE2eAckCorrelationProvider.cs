#if DEBUG && DEEP_PHYSICAL_E2E
using System.Security.Cryptography;
using System.Text;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.Services;

internal sealed class PhysicalE2eAckCorrelationProvider
{
    private static readonly object CurrentGate = new();
    private static PhysicalE2eAckCorrelationProvider? current;
    private readonly ClientRuntime runtime;

    private PhysicalE2eAckCorrelationProvider(ClientRuntime runtime) =>
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    internal static void Bind(ClientRuntime runtime)
    {
        lock (CurrentGate) current = new PhysicalE2eAckCorrelationProvider(runtime);
    }

    internal static void Unbind(ClientRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        lock (CurrentGate)
        {
            if (ReferenceEquals(current?.runtime, runtime))
                current = null;
        }
    }

    internal static async Task<string?> ReadCurrentAsync(
        MessageId messageId,
        CancellationToken cancellationToken = default)
    {
        PhysicalE2eAckCorrelationProvider? provider;
        lock (CurrentGate) provider = current;
        return provider is null
            ? null
            : await provider.ReadAsync(messageId, cancellationToken).ConfigureAwait(false);
    }

    internal static string MarkerHash(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("deep.physical-e2e.ack-marker.v1\0"u8);
        var bytes = Encoding.UTF8.GetBytes(body);
        try
        {
            hash.AppendData(bytes);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task<string?> ReadAsync(
        MessageId messageId,
        CancellationToken cancellationToken)
    {
        var message = await runtime.Store.GetAsync(messageId, cancellationToken)
            .ConfigureAwait(false);
        if (message is null
            || message.Direction != MessageDirection.Incoming
            || message.Recipient is not { } recipient
            || string.IsNullOrWhiteSpace(message.ServerHash)
            || runtime.MessageTransport is not IMailboxAckCorrelationProjectionSource source)
            return null;
        var projection = await source.ProjectMailboxAckCorrelationAsync(
            recipient,
            message.ServerHash,
            cancellationToken).ConfigureAwait(false);
        if (projection is null) return null;
        var state = projection.State switch
        {
            MailboxAckCorrelationState.AmbiguousAttempted
                when projection.AttemptCount == 1 => "ambiguous-attempted",
            MailboxAckCorrelationState.RecoveredDurable
                when projection.AttemptCount == 2 => "recovered-durable",
            _ => throw new InvalidDataException(
                "Physical ACK projection returned an invalid closed state.")
        };
        return string.Join('|',
            "v1",
            $"marker={MarkerHash(message.Body)}",
            $"correlation={projection.CorrelationHash}",
            $"state={state}",
            $"attempts={projection.AttemptCount}");
    }
}
#endif
