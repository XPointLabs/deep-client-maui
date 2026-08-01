using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Features;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class PersistentClientRuntimeComposerTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-26T10:00:00Z");

    [Fact]
    public async Task ProductionCompositionUsesEncryptedSqliteState()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"deep-secure-outbox-composition-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var statePath = Path.Combine(directory, "client-state.db");
        var executor = new ReadyExecutor();
        var transport = new AuthenticatedTransport();
        using var membershipProvider =
            DevLocalMembershipRouteCompositionTests.CreateProviderForCompositionTest();
        try
        {
            using (var runtime = PersistentClientRuntimeComposer.Create(
                       statePath,
                       ClientFeatureFlags.Defaults with { PersistentTransportOutboxEnabled = true },
                       new FixedClock(),
                       new DisabledAvatarProfileTransport(),
                       new string('A', 64),
                       (_, _) => new StoreBoundRuntimeTransportComposition(
                           transport,
                           new DirectP2pMailboxDeliveryPolicy()),
                       executor,
                       membershipProvider))
            {

                Assert.NotNull(runtime.TransportOutbox);
                Assert.True(membershipProvider.IsBound);
                var item = TransportOutboxPreparedItem.Create(
                    OutboxAccountScope.FromBytes(Bytes(TransportOutboxLimits.AccountScopeBytes, 0x11)),
                    OutboxLogicalId.FromBytes(Bytes(TransportOutboxLimits.LogicalIdBytes, 0x22)),
                    OutboxDedupMaterial.FromBytes(Bytes(TransportOutboxLimits.DedupMaterialBytes, 0x33)),
                    Bytes(64, 0x44),
                    Now,
                    Now.AddHours(1),
                    Now);

                Assert.Equal(
                    TransportOutboxCommitResult.Applied,
                    await runtime.TransportOutbox!.PrepareAsync(item));
                var result = await runtime.TransportOutbox.DispatchReadyAsync(item.AccountScope);
                var persisted = await runtime.TransportOutbox.ReadAsync(
                    item.AccountScope,
                    item.LogicalId);

                Assert.Equal(1, result.DurableCount);
                Assert.Equal(TransportOutboxState.Durable, persisted.Item?.State);
            }

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
