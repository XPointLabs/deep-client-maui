using Deep.Client.Maui.Services;
using Deep.Client.Shared.Features;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
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
                });

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
            });
        fixture = new Fixture(directory, sqlite, secureStore, identity, transport);
        return fixture;
    }

    private static IDisposable EnterOperation(StoreBoundNativeMau2Transport transport) =>
        (IDisposable)(typeof(StoreBoundNativeMau2Transport)
            .GetMethod("EnterOperation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(transport, null)
            ?? throw new InvalidOperationException("Operation lease was not created."));

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
