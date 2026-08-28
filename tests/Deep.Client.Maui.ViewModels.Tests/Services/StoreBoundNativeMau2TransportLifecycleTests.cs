using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Features;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Protocol.DeepExtension.MailboxCapabilities;
using System.Reflection;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class StoreBoundNativeMau2TransportLifecycleTests
{
    private const string RecoveryPhrase =
        "amaze buffet cake entrance symptoms tiger lamb maze nestle python dusted faxed faxed";

    [Fact]
    public async Task DisposeWaitsForInFlightLazyBindAndRejectsLaterOperations()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), $"deep-mau2-lifetime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var enteredFactory = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFactory = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var sqlite = new SqliteSessionStore(new SqliteSessionStoreOptions(
                Path.Combine(directory, "client-state.db"),
                new string('A', 64)));
            using var secureStore = new SecureRecoverySessionStore(sqlite);
            using var identity = new SessionIdentityProvider(RecoveryPhrase);
            var transport = new StoreBoundNativeMau2Transport(
                sqlite,
                secureStore,
                () =>
                {
                    enteredFactory.TrySetResult();
                    releaseFactory.Task.GetAwaiter().GetResult();
                    throw new InvalidDataException("Synthetic blocked import.");
                },
                _ => { },
                MailboxInfrastructureOwnership.UserManaged,
                ClientFeatureFlags.ReleaseDefaults with
                {
                    ClientMailboxAdapterEnabled = true
                },
                new HttpServiceTransportFactory(HttpServiceEndpointPolicy.Production),
                new HttpServiceClientOptions());

            var receive = Task.Run(async () =>
                await transport.ReceiveAuthenticatedAsync(identity));
            await enteredFactory.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var dispose = Task.Run(transport.Dispose);
            await Task.Delay(50);
            Assert.False(dispose.IsCompleted);

            releaseFactory.TrySetResult();
            await Assert.ThrowsAsync<InvalidDataException>(() => receive);
            await dispose.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => transport.ReceiveAuthenticatedAsync(identity));
        }
        finally
        {
            releaseFactory.TrySetResult();
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task StopWaitsForActiveOperationAndBlocksRebindUntilResume()
    {
        var fixture = CreateFixture();
        try
        {
            var operation = EnterOperation(fixture.Transport);
            var stop = fixture.Transport.StopAsync(fixture.Identity.SessionId);
            await Task.Delay(50);
            Assert.False(stop.IsCompleted);

            operation.Dispose();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => fixture.Transport.ReceiveAuthenticatedAsync(fixture.Identity));
            Assert.Equal(0, fixture.FactoryCalls);

            fixture.Transport.Resume(fixture.Identity.SessionId);
            await Assert.ThrowsAsync<InvalidDataException>(
                () => fixture.Transport.ReceiveAuthenticatedAsync(fixture.Identity));
            Assert.Equal(1, fixture.FactoryCalls);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task DisposeWaitsForActiveOperationBeforeTerminalCleanup()
    {
        var fixture = CreateFixture();
        var operation = EnterOperation(fixture.Transport);
        try
        {
            var dispose = Task.Run(fixture.Transport.Dispose);
            await Task.Delay(50);
            Assert.False(dispose.IsCompleted);

            operation.Dispose();
            await dispose.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => fixture.Transport.ReceiveAuthenticatedAsync(fixture.Identity));
        }
        finally
        {
            operation.Dispose();
            fixture.Dispose();
        }
    }

    [Fact]
    public void StoreBoundTransportPreservesDurableLogicalBatchCapability()
    {
        var fixture = CreateFixture();
        try
        {
            Assert.IsAssignableFrom<IResumableMailboxIdentityAuthenticatedRawTransport>(
                fixture.Transport);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task ConcurrentTerminalRetrievesCoalesceRetirementAndRefreshBeforePerCallerRetry()
    {
        using var fixture = ReactiveFixture.Create(concurrentTerminalCalls: 2);

        var first = fixture.Transport.ReceiveAuthenticatedAsync(fixture.Identity);
        var second = fixture.Transport.ReceiveAuthenticatedAsync(fixture.Identity);
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(results, static result => Assert.Empty(result));
        Assert.Equal(2, fixture.RejectedReads.ReceiveCalls);
        Assert.Equal(1, fixture.RejectedReads.RetirementCalls);
        Assert.Equal(1, fixture.Source.RefreshCalls);
        Assert.Equal(2, fixture.SuccessorReads.ReceiveCalls);
        Assert.Equal(0, fixture.InitialIngress.StoreCalls);
        Assert.Equal(0, fixture.InitialIngress.AcknowledgeCalls);
        Assert.Equal(0, fixture.SuccessorIngress.StoreCalls);
        Assert.Equal(0, fixture.SuccessorIngress.AcknowledgeCalls);
    }

    [Theory]
    [InlineData(ReactiveReadEntryPoint.Receive)]
    [InlineData(ReactiveReadEntryPoint.Retrieve)]
    [InlineData(ReactiveReadEntryPoint.OpaqueRetrieve)]
    public async Task EachReadOnlyEntryPointRetriesExactlyOnceAfterTerminalRefresh(
        ReactiveReadEntryPoint entryPoint)
    {
        using var fixture = ReactiveFixture.Create(concurrentTerminalCalls: 1);

        await fixture.ExecuteAsync(entryPoint);

        Assert.Equal(1, fixture.RejectedReads.TotalReadCalls);
        Assert.Equal(1, fixture.RejectedReads.RetirementCalls);
        Assert.Equal(1, fixture.Source.RefreshCalls);
        Assert.Equal(1, fixture.SuccessorReads.TotalReadCalls);
        Assert.Equal(0, fixture.InitialIngress.StoreCalls);
        Assert.Equal(0, fixture.InitialIngress.AcknowledgeCalls);
        Assert.Equal(0, fixture.SuccessorIngress.StoreCalls);
        Assert.Equal(0, fixture.SuccessorIngress.AcknowledgeCalls);
    }

    [Fact]
    public async Task SecondTerminalRetrieveFailureBubblesWithoutAnotherRefresh()
    {
        using var fixture = ReactiveFixture.Create(
            concurrentTerminalCalls: 1,
            successorFailure: new ClientMailboxTransportException(
                ClientMailboxTransportFailure.AuthorizationRejected,
                retryable: false,
                "Second terminal rejection."));

        var exception = await Assert.ThrowsAsync<ClientMailboxTransportException>(
            () => fixture.Transport.ReceiveAuthenticatedAsync(fixture.Identity));

        Assert.Equal(ClientMailboxTransportFailure.AuthorizationRejected, exception.Failure);
        Assert.Equal(1, fixture.RejectedReads.RetirementCalls);
        Assert.Equal(1, fixture.Source.RefreshCalls);
        Assert.Equal(1, fixture.SuccessorReads.ReceiveCalls);
    }

    [Theory]
    [InlineData(MailboxInfrastructureOwnership.UserManaged, true)]
    [InlineData(MailboxInfrastructureOwnership.OfficialManaged, false)]
    public async Task UnsupportedReactiveRefreshBubblesWithoutRetirementOrRefreshMutation(
        MailboxInfrastructureOwnership ownership,
        bool supportsReactiveRefresh)
    {
        using var fixture = ReactiveFixture.Create(
            concurrentTerminalCalls: 1,
            ownership: ownership,
            supportsReactiveRefresh: supportsReactiveRefresh);

        var exception = await Assert.ThrowsAsync<ClientMailboxTransportException>(
            () => fixture.Transport.ReceiveAuthenticatedAsync(fixture.Identity));

        Assert.Equal(ClientMailboxTransportFailure.AuthorizationRejected, exception.Failure);
        Assert.Equal(0, fixture.RejectedReads.RetirementCalls);
        Assert.Equal(0, fixture.Source.RefreshCalls);
        Assert.Equal(0, fixture.SuccessorReads.TotalReadCalls);
    }

    [Fact]
    public async Task StopWaitsForRefreshTransitionBeforeStoppingAccountGeneration()
    {
        using var fixture = ReactiveFixture.Create(
            concurrentTerminalCalls: 1,
            blockRefresh: true);

        var receive = fixture.Transport.ReceiveAuthenticatedAsync(fixture.Identity);
        await fixture.Source.RefreshEntered.WaitAsync(TimeSpan.FromSeconds(10));
        var stop = fixture.Transport.StopAsync(fixture.Identity.SessionId);

        Assert.False(stop.IsCompleted);
        fixture.Source.AllowRefresh();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            _ = await receive.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
            // Stop may win the Active-to-Stopping race after refresh completes.
        }
        Assert.Equal(1, fixture.RejectedReads.RetirementCalls);
        Assert.Equal(1, fixture.Source.RefreshCalls);
        Assert.Equal(1, fixture.Source.ReleaseCalls);
    }

    [Fact]
    public async Task DisposeWaitsForRefreshTransitionWithoutStartingConcurrentTeardown()
    {
        using var fixture = ReactiveFixture.Create(
            concurrentTerminalCalls: 1,
            blockRefresh: true);

        var receive = fixture.Transport.ReceiveAuthenticatedAsync(fixture.Identity);
        await fixture.Source.RefreshEntered.WaitAsync(TimeSpan.FromSeconds(10));
        var dispose = Task.Run(fixture.Transport.Dispose);

        Assert.False(dispose.IsCompleted);
        fixture.Source.AllowRefresh();
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            _ = await receive.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception exception) when (
            exception is ObjectDisposedException or OperationCanceledException)
        {
        }
        Assert.Equal(1, fixture.RejectedReads.RetirementCalls);
        Assert.Equal(1, fixture.Source.RefreshCalls);
    }

    [Fact]
    public async Task CallerCancellationDoesNotCancelOrStickSharedRefreshTransition()
    {
        using var fixture = ReactiveFixture.Create(
            concurrentTerminalCalls: 1,
            blockRefresh: true);
        using var cancellation = new CancellationTokenSource();

        var canceledReceive = fixture.Transport.ReceiveAuthenticatedAsync(
            fixture.Identity, cancellation.Token);
        await fixture.Source.RefreshEntered.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceledReceive.WaitAsync(TimeSpan.FromSeconds(10)));

        var transition = GetRefreshTransition(fixture.Transport);
        fixture.Source.AllowRefresh();
        await transition.WaitAsync(TimeSpan.FromSeconds(10));
        var result = await fixture.Transport.ReceiveAuthenticatedAsync(fixture.Identity)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(result);
        Assert.Equal(1, fixture.RejectedReads.RetirementCalls);
        Assert.Equal(1, fixture.Source.RefreshCalls);
        Assert.Equal(1, fixture.SuccessorReads.ReceiveCalls);
    }

    private static Fixture CreateFixture()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), $"deep-mau2-operation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var sqlite = new SqliteSessionStore(new SqliteSessionStoreOptions(
            Path.Combine(directory, "client-state.db"),
            new string('A', 64)));
        var secureStore = new SecureRecoverySessionStore(sqlite);
        var identity = new SessionIdentityProvider(RecoveryPhrase);
        Fixture? fixture = null;
        var transport = new StoreBoundNativeMau2Transport(
            sqlite,
            secureStore,
            () =>
            {
                fixture!.FactoryCalls++;
                throw new InvalidDataException("Synthetic import attempt.");
            },
            _ => { },
            MailboxInfrastructureOwnership.UserManaged,
            ClientFeatureFlags.ReleaseDefaults with
            {
                ClientMailboxAdapterEnabled = true
            },
            new HttpServiceTransportFactory(HttpServiceEndpointPolicy.Production),
            new HttpServiceClientOptions());
        fixture = new Fixture(directory, sqlite, secureStore, identity, transport);
        return fixture;
    }

    private static IDisposable EnterOperation(StoreBoundNativeMau2Transport transport) =>
        (IDisposable)(typeof(StoreBoundNativeMau2Transport)
            .GetMethod("EnterOperation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(transport, null)
            ?? throw new InvalidOperationException("Operation lease was not created."));

    private static Task GetRefreshTransition(StoreBoundNativeMau2Transport transport) =>
        (Task)(typeof(StoreBoundNativeMau2Transport)
            .GetField("refreshTransition", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(transport)
            ?? throw new InvalidOperationException("Refresh transition was not created."));

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

    public enum ReactiveReadEntryPoint
    {
        Receive,
        Retrieve,
        OpaqueRetrieve
    }

    private sealed class ReactiveFixture : IDisposable
    {
        private readonly string directory;
        private readonly SqliteSessionStore sqlite;
        private readonly SecureRecoverySessionStore secureStore;
        private readonly TestSigner signer;

        private ReactiveFixture(
            string directory,
            SqliteSessionStore sqlite,
            SecureRecoverySessionStore secureStore,
            SessionIdentityProvider identity,
            TestSigner signer,
            ReactiveProvisioningSource source,
            ScriptedReactiveReads rejectedReads,
            ScriptedReactiveReads successorReads,
            RecordingIngress initialIngress,
            RecordingIngress successorIngress,
            StoreBoundNativeMau2Transport transport)
        {
            this.directory = directory;
            this.sqlite = sqlite;
            this.secureStore = secureStore;
            Identity = identity;
            this.signer = signer;
            Source = source;
            RejectedReads = rejectedReads;
            SuccessorReads = successorReads;
            InitialIngress = initialIngress;
            SuccessorIngress = successorIngress;
            Transport = transport;
        }

        public SessionIdentityProvider Identity { get; }
        public ReactiveProvisioningSource Source { get; }
        public ScriptedReactiveReads RejectedReads { get; }
        public ScriptedReactiveReads SuccessorReads { get; }
        public RecordingIngress InitialIngress { get; }
        public RecordingIngress SuccessorIngress { get; }
        public StoreBoundNativeMau2Transport Transport { get; }

        public static ReactiveFixture Create(
            int concurrentTerminalCalls,
            Exception? successorFailure = null,
            MailboxInfrastructureOwnership ownership =
                MailboxInfrastructureOwnership.OfficialManaged,
            bool supportsReactiveRefresh = true,
            bool blockRefresh = false)
        {
            var directory = Path.Combine(
                Path.GetTempPath(), $"deep-mau2-reactive-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var sqlite = new SqliteSessionStore(new SqliteSessionStoreOptions(
                Path.Combine(directory, "client-state.db"),
                new string('A', 64)));
            var secureStore = new SecureRecoverySessionStore(sqlite);
            var identity = new SessionIdentityProvider(RecoveryPhrase);
            var signer = new TestSigner(identity);
            try
            {
                var initialIngress = new RecordingIngress();
                var successorIngress = new RecordingIngress();
                var initial = Runtime(sqlite, identity.SessionId, generation: 1, initialIngress);
                var successor = Runtime(sqlite, identity.SessionId, generation: 2, successorIngress);
                var source = new ReactiveProvisioningSource(
                    initial, successor, supportsReactiveRefresh, blockRefresh);
                var rejectedReads = ScriptedReactiveReads.Terminal(concurrentTerminalCalls);
                var successorReads = ScriptedReactiveReads.Success(successorFailure);
                var reads = new Queue<IReactiveMau2ReadRuntime>(
                    [rejectedReads, successorReads]);
                var transport = new StoreBoundNativeMau2Transport(
                    sqlite,
                    secureStore,
                    source,
                    ownership,
                    ClientFeatureFlags.ReleaseDefaults with
                    {
                        ClientMailboxAdapterEnabled = true
                    },
                    reactiveReadRuntimeFactory: _ => reads.Dequeue());
                return new ReactiveFixture(
                    directory,
                    sqlite,
                    secureStore,
                    identity,
                    signer,
                    source,
                    rejectedReads,
                    successorReads,
                    initialIngress,
                    successorIngress,
                    transport);
            }
            catch
            {
                signer.Dispose();
                identity.Dispose();
                secureStore.Dispose();
                sqlite.Dispose();
                TryDeleteDirectory(directory);
                throw;
            }
        }

        public async Task ExecuteAsync(ReactiveReadEntryPoint entryPoint)
        {
            switch (entryPoint)
            {
                case ReactiveReadEntryPoint.Receive:
                    _ = await Transport.ReceiveAuthenticatedAsync(Identity);
                    break;
                case ReactiveReadEntryPoint.Retrieve:
                    _ = await Transport.RetrieveAuthenticatedAsync(
                        Identity, cursor: null, limit: 1);
                    break;
                case ReactiveReadEntryPoint.OpaqueRetrieve:
                    _ = await Transport.RetrieveOpaqueMailboxInboxAsync(
                        signer, new OpaqueMailboxContinuation(0, []));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(entryPoint));
            }
        }

        public void Dispose()
        {
            Transport.Dispose();
            signer.Dispose();
            Identity.Dispose();
            secureStore.Dispose();
            sqlite.Dispose();
            TryDeleteDirectory(directory);
        }

        private static ProvisionedMailboxRuntime Runtime(
            SqliteSessionStore store,
            SessionId account,
            ulong generation,
            RecordingIngress ingress)
        {
            var clock = new FixedTimeProvider(
                DateTimeOffset.FromUnixTimeSeconds(1_050));
            var issuerContext = Bytes(32, 0x51);
            var self = new MailboxCredentialSelector(
                OutboxAccountScope.FromBytes(Bytes(32, 0x61)),
                MailboxCredentialScopeKind.Self,
                Bytes(32, 0x71),
                issuerContext);
            var authority = new VerifiedOfficialMailboxAuthority(
                Bytes(16, 0x11),
                generation,
                [new MailboxCapabilityIssuerAuthority
                {
                    PublicKey = Bytes(32, checked((byte)(0x20 + generation))),
                    Domain = MailboxCapabilityDomain.Retrieve,
                    AllowedLifecycle = MailboxCapabilityLifecycle.Active,
                    MinimumGeneration = generation,
                    MaximumGeneration = generation,
                    ValidFromUnixSeconds = 900,
                    ValidUntilUnixSeconds = 2_000
                }],
                requiresManagedEntitlement: false,
                static () => false,
                new EmptyRevocations(),
                clock,
                MailboxRuntimePolicyCoordinator.For(
                    store.CanonicalStateIdentity, issuerContext));
            return new ProvisionedMailboxRuntime(
                authority,
                new ClientMailboxActivation(true, issuerContext, true),
                new TimeProviderMailboxClientDecodePolicyProvider(
                    new MailboxEpochWindow
                    {
                        CurrentEpoch = generation,
                        NextEpoch = checked(generation + 1),
                        CurrentNotBeforeUnixSeconds = 900,
                        NextNotBeforeUnixSeconds = 1_100,
                        CurrentExpiresAtUnixSeconds = 1_120,
                        NextExpiresAtUnixSeconds = 1_200
                    },
                    new MailboxCapabilityDecodePolicy
                    {
                        CurrentBucket = 1_050,
                        MinimumGeneration = generation
                    },
                    clock),
                account,
                self,
                (recipient, _) => Task.FromResult<MailboxCredentialSelector?>(
                    recipient == account ? self : null),
                ingress,
                clock);
        }
    }

    private sealed class ReactiveProvisioningSource(
        ProvisionedMailboxRuntime initial,
        ProvisionedMailboxRuntime successor,
        bool supportsReactiveRefresh,
        bool blockRefresh) : IMailboxRuntimeProvisioningSource
    {
        private readonly TaskCompletionSource refreshEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseRefresh = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int refreshCalls;
        private int releaseCalls;
        public bool SupportsReactiveRejectedRetrieveRefresh => supportsReactiveRefresh;
        public int RefreshCalls => Volatile.Read(ref refreshCalls);
        public int ReleaseCalls => Volatile.Read(ref releaseCalls);
        public Task RefreshEntered => refreshEntered.Task;

        public void AllowRefresh() => releaseRefresh.TrySetResult();

        public Task<ProvisionedMailboxRuntime> ProvisionAsync(
            SqliteSessionStore store,
            MailboxHolderIdentity holder,
            MailboxInfrastructureOwnership ownership,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(initial.LocalSessionId, holder.SessionId);
            return Task.FromResult(initial);
        }

        public async Task<ProvisionedMailboxRuntime?> RefreshAfterRejectedRetrieveAsync(
            SqliteSessionStore store,
            MailboxHolderIdentity holder,
            MailboxInfrastructureOwnership ownership,
            ulong failedRuntimeGeneration,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref refreshCalls);
            refreshEntered.TrySetResult();
            Assert.Equal(initial.LocalSessionId, holder.SessionId);
            Assert.Equal(initial.Authority.MinimumGeneration, failedRuntimeGeneration);
            if (blockRefresh)
                await releaseRefresh.Task.WaitAsync(cancellationToken);
            return successor;
        }

        public Task ReleaseAsync(
            SessionId account,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(initial.LocalSessionId, account);
            Interlocked.Increment(ref releaseCalls);
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedReactiveReads : IReactiveMau2ReadRuntime
    {
        private readonly bool terminal;
        private readonly int concurrentTerminalCalls;
        private readonly Exception? successorFailure;
        private readonly TaskCompletionSource terminalBarrier = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int receiveCalls;
        private int retrieveCalls;
        private int opaqueRetrieveCalls;
        private int retirementCalls;

        private ScriptedReactiveReads(
            bool terminal,
            int concurrentTerminalCalls,
            Exception? successorFailure)
        {
            this.terminal = terminal;
            this.concurrentTerminalCalls = concurrentTerminalCalls;
            this.successorFailure = successorFailure;
        }

        public int ReceiveCalls => Volatile.Read(ref receiveCalls);
        public int TotalReadCalls =>
            Volatile.Read(ref receiveCalls) +
            Volatile.Read(ref retrieveCalls) +
            Volatile.Read(ref opaqueRetrieveCalls);
        public int RetirementCalls => Volatile.Read(ref retirementCalls);

        public static ScriptedReactiveReads Terminal(int concurrentCalls) =>
            new(true, concurrentCalls, successorFailure: null);

        public static ScriptedReactiveReads Success(Exception? failure) =>
            new(false, concurrentTerminalCalls: 0, failure);

        public async Task<IReadOnlyList<InboundMessageEnvelope>> ReceiveAuthenticatedAsync(
            SessionIdentityProvider identity,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref receiveCalls);
            await BeforeResultAsync(cancellationToken);
            return [];
        }

        public async Task<AuthenticatedInboxBatch> RetrieveAuthenticatedAsync(
            SessionIdentityProvider identity,
            string? cursor,
            int limit,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref retrieveCalls);
            await BeforeResultAsync(cancellationToken);
            return new AuthenticatedInboxBatch([], cursor);
        }

        public async Task<OpaqueMailboxInboxPage> RetrieveOpaqueMailboxInboxAsync(
            IMailboxOperationSigner signer,
            OpaqueMailboxContinuation continuation,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref opaqueRetrieveCalls);
            await BeforeResultAsync(cancellationToken);
            return new OpaqueMailboxInboxPage(continuation, continuation, []);
        }

        public Task RetireTerminallyRejectedRetrieveAsync(
            SessionId account,
            ClientMailboxTransportException terminalFailure,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref retirementCalls) != 1)
                throw new InvalidOperationException("missing-outbox");
            Assert.False(terminalFailure.Retryable);
            return Task.CompletedTask;
        }

        private async Task BeforeResultAsync(CancellationToken cancellationToken)
        {
            if (!terminal)
            {
                if (successorFailure is not null) throw successorFailure;
                return;
            }

            if (TotalReadCalls >= concurrentTerminalCalls)
                terminalBarrier.TrySetResult();
            await terminalBarrier.Task.WaitAsync(cancellationToken);
            throw new ClientMailboxTransportException(
                ClientMailboxTransportFailure.AuthorizationRejected,
                retryable: false,
                "Terminal stale runtime rejection.");
        }
    }

    private sealed class RecordingIngress : IClientMailboxBinaryIngress, IDisposable
    {
        private int storeCalls;
        private int acknowledgeCalls;
        public int StoreCalls => Volatile.Read(ref storeCalls);
        public int AcknowledgeCalls => Volatile.Read(ref acknowledgeCalls);

        public Task<ReadOnlyMemory<byte>> StoreAsync(
            ReadOnlyMemory<byte> canonicalMau2,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref storeCalls);
            return Task.FromException<ReadOnlyMemory<byte>>(new NotSupportedException());
        }

        public Task<ReadOnlyMemory<byte>> RetrieveAsync(
            ReadOnlyMemory<byte> canonicalMau2,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ReadOnlyMemory<byte>>(new NotSupportedException());

        public Task<ReadOnlyMemory<byte>> AcknowledgeAsync(
            ReadOnlyMemory<byte> canonicalMau2,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref acknowledgeCalls);
            return Task.FromException<ReadOnlyMemory<byte>>(new NotSupportedException());
        }

        public void Dispose()
        {
        }
    }

    private sealed class TestSigner(SessionIdentityProvider identity) :
        IMailboxOperationSigner,
        IDisposable
    {
        public SessionId SessionId => identity.SessionId;

        public byte[] GetEd25519PublicKey() => identity.GetEd25519PublicKey();

        public byte[] SignMailboxPresentation(
            MailboxAuthenticatedOperation operation,
            ReadOnlySpan<byte> canonicalPresentationSigningBytes) =>
            identity.SignDetached(canonicalPresentationSigningBytes);

        public void Dispose()
        {
        }
    }

    private sealed class EmptyRevocations : IFreshMailboxCapabilityRevocationSource
    {
        public void ValidateFreshness()
        {
        }

        public bool IsRevoked(MailboxCapabilityRevocationQuery query) => false;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static byte[] Bytes(int count, byte value) =>
        Enumerable.Repeat(value, count).ToArray();

    private sealed class Fixture(
        string directory,
        SqliteSessionStore sqlite,
        SecureRecoverySessionStore secureStore,
        SessionIdentityProvider identity,
        StoreBoundNativeMau2Transport transport) : IDisposable
    {
        public SessionIdentityProvider Identity { get; } = identity;
        public StoreBoundNativeMau2Transport Transport { get; } = transport;
        public int FactoryCalls { get; set; }

        public void Dispose()
        {
            Transport.Dispose();
            Identity.Dispose();
            secureStore.Dispose();
            sqlite.Dispose();
            TryDeleteDirectory(directory);
        }
    }
}
