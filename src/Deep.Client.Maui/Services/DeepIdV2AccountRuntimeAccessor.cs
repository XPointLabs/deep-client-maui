using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Services;
using Deep.Protocol.ApplicationCore;

namespace Deep.Client.Maui.Services;

/// <summary>
/// Lazily opens one isolated DID2 local owner. This accessor has no method
/// returning the incompatible V1 account or its transport graph.
/// </summary>
internal sealed class DeepIdV2AccountRuntimeAccessor :
    IDeepIdV2AccountRuntimeAccessor
{
    private readonly string appDataDirectory;
    private readonly Func<ReadOnlyMemory<byte>> networkIdFactory;
    private readonly ushort deploymentProfileId;
    private readonly IClock clock;
    private readonly Func<IDeepMlDsa65VerifierLease> verifierFactory;
    private readonly SemaphoreSlim gate = new(1, 1);
    private DeepIdV2AccountRuntimeOwner? owner;
    private bool disposed;

    internal DeepIdV2AccountRuntimeAccessor(string appDataDirectory,
        Func<ReadOnlyMemory<byte>> networkIdFactory,
        ushort deploymentProfileId, IClock clock,
        Func<IDeepMlDsa65VerifierLease> verifierFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        this.appDataDirectory = Path.GetFullPath(appDataDirectory);
        this.networkIdFactory = networkIdFactory ??
            throw new ArgumentNullException(nameof(networkIdFactory));
        this.deploymentProfileId = deploymentProfileId;
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.verifierFactory = verifierFactory ??
            throw new ArgumentNullException(nameof(verifierFactory));
    }

    public async Task<DeepIdV2AccountService> GetAccountsAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner ??= await DeepIdV2AccountRuntimeOwner.OpenAsync(
                appDataDirectory, networkIdFactory(), deploymentProfileId,
                clock, verifierFactory, cancellationToken).ConfigureAwait(false);
            return owner.Accounts;
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            if (owner is not null)
                await owner.DisposeAsync().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}
