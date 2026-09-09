using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Domain.MessagingV1;
using Deep.Client.Shared.Features;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Persistence.MessagingV1;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class PersistentClientRuntimeComposerTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-26T10:00:00Z");

    [Fact]
    public void Composer_rejects_legacy_authenticated_transport_and_disposes_it_once()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"deep-e2ee01-blocker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var statePath = Path.Combine(directory, "client-state.db");
        var transport = new LegacyAuthenticatedTransport();
        try
        {
            var exception = Assert.Throws<MessagingV1CryptoUnavailableException>(() =>
                PersistentClientRuntimeComposer.Create(
                    statePath,
                    ClientFeatureFlags.ReleaseDefaults with
                    {
                        MetadataPrivateTransportRequired = false
                    },
                    new FixedClock(),
                    new DisabledAvatarProfileTransport(),
                    new string('A', 64),
                    (_, _) => new StoreBoundRuntimeTransportComposition(
                        transport,
                        new DirectP2pMailboxDeliveryPolicy()),
                    transportOutboxExecutor: null));

            Assert.Equal(
                MessagingV1CryptoUnavailableException.ProductionCapabilityUnavailable,
                exception.Code);
            Assert.Equal(1, transport.DisposeCount);
            Assert.False(File.Exists(statePath + ".msg01"));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void ProductionCompositionRejectsParallelLegacyTransportOutbox()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"deep-secure-outbox-composition-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var statePath = Path.Combine(directory, "client-state.db");
        var executor = new ReadyExecutor();
        var transport = new AuthenticatedTransport();
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                PersistentClientRuntimeComposer.Create(
                    statePath,
                    ClientFeatureFlags.Defaults with { PersistentTransportOutboxEnabled = true },
                    new FixedClock(),
                    new DisabledAvatarProfileTransport(),
                    new string('A', 64),
                    (_, _) => new StoreBoundRuntimeTransportComposition(
                        transport,
                        new DirectP2pMailboxDeliveryPolicy()),
                    executor));

            Assert.Contains("cannot own the same production dispatch path",
                exception.Message, StringComparison.Ordinal);
            Assert.Equal(1, transport.DisposeCount);
        }
        finally
        {
            executor.Dispose();
            TryDeleteDirectory(directory);
        }
    }

    private static byte[] Bytes(int length, byte value) =>
        Enumerable.Repeat(value, length).ToArray();

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class AuthenticatedTransport :
        ISessionMessageTransport,
        IAuthenticatedInboxTransport,
        IDirectP2pSessionMessageTransport,
        IMsg01AuthenticatedEvidenceSource,
        IDisposable
    {
        private readonly Msg01VerifiedSessionAuthority evidenceAuthority =
            Msg01VerifiedSessionAuthority.CreateTestEd25519(Bytes(32, 0x6A));
        public int DisposeCount { get; private set; }
        public Msg01VerifiedSessionAuthority EvidenceAuthority => evidenceAuthority;

        public ValueTask<SessionId> GetLocalAccountAsync(CancellationToken cancellationToken) =>
            ValueTask.FromException<SessionId>(new InvalidOperationException("No test account is active."));

        public ValueTask<DirectoryHeadHash32> ResolveFanoutTargetAsync(
            Msg01ResolveFanoutTargetRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromException<DirectoryHeadHash32>(new NotSupportedException());

        public ValueTask<Msg01PreparedTransportAttempt> PrepareAttemptAsync(
            Msg01PrepareAttemptRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromException<Msg01PreparedTransportAttempt>(new NotSupportedException());

        public ValueTask<Msg01AuthenticatedDispatchResult> DispatchPreparedAsync(
            Msg01DispatchEvidenceRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromException<Msg01AuthenticatedDispatchResult>(new NotSupportedException());

        public ValueTask<Msg01AuthenticatedDispatchResult> ReconcilePreparedAsync(
            Msg01DispatchEvidenceRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromException<Msg01AuthenticatedDispatchResult>(new NotSupportedException());

        public ValueTask<Msg01AuthenticatedInboundResult> GetInboundResultAsync(
            Msg01InboundEvidenceRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromException<Msg01AuthenticatedInboundResult>(new NotSupportedException());

        public ValueTask AcknowledgeReceiptAsync(
            ReadOnlyMemory<byte> authenticatedReceipt, CancellationToken cancellationToken) =>
            ValueTask.FromException(new NotSupportedException());

        public Task SendAsync(
            OutboundMessageEnvelope envelope,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<InboundMessageEnvelope>> ReceiveAsync(
            SessionId recipient,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InboundMessageEnvelope>>([]);

        public Task<IReadOnlyList<InboundMessageEnvelope>> ReceiveAuthenticatedAsync(
            SessionIdentityProvider identity,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InboundMessageEnvelope>>([]);

        public void Dispose() => DisposeCount++;
    }

    private sealed class LegacyAuthenticatedTransport :
        ISessionMessageTransport,
        IAuthenticatedInboxTransport,
        IDisposable
    {
        public int DisposeCount { get; private set; }

        public Task SendAsync(
            OutboundMessageEnvelope envelope,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<InboundMessageEnvelope>> ReceiveAsync(
            SessionId recipient,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InboundMessageEnvelope>>([]);

        public Task<IReadOnlyList<InboundMessageEnvelope>> ReceiveAuthenticatedAsync(
            SessionIdentityProvider identity,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InboundMessageEnvelope>>([]);

        public void Dispose() => DisposeCount++;
    }

    private sealed class ReadyExecutor : IExternalTransportOutboxExecutor, IDisposable
    {
        public TimeSpan MaximumDispatchDuration => TimeSpan.FromSeconds(5);

        public bool TryAcquire(out IExternalTransportOutboxExecution? execution)
        {
            execution = new ReadyExecution();
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class ReadyExecution : IExternalTransportOutboxExecution
    {
        public Task<TransportOutboxAdapterReceipt> DispatchAsync(
            TransportOutboxDispatchRequest request,
            TimeSpan hardTimeout,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(TransportOutboxAdapterReceipt.Durable([0xA1], [0xD1]));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
