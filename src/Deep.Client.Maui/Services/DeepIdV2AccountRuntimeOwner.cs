using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Protocol.ApplicationCore;

namespace Deep.Client.Maui.Services;

/// <summary>
/// The isolated MAUI DID2 account boundary. It owns only STORE-V2 platform
/// protection and SQLCipher state; no V1 account, contact or message runtime
/// is ever returned from this owner.
/// </summary>
internal sealed class DeepIdV2AccountRuntimeOwner : IAsyncDisposable
{
    private readonly JournaledDeepSecureStorage storage;
    private int disposed;

    private DeepIdV2AccountRuntimeOwner(JournaledDeepSecureStorage storage,
        DeepIdV2AccountService accounts)
    {
        this.storage = storage;
        Accounts = accounts;
    }

    internal DeepIdV2AccountService Accounts { get; }

#if DEEP_DID2_ACCOUNT_PROBE
    /// <summary>
    /// The isolated probe can reset a retired, unreadable test generation only
    /// after its own visible confirmation UI. Normal UAT/production builds do
    /// not contain this entry point.
    /// </summary>
    internal static async Task ResetIsolatedProbeAfterConfirmationAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(MauiProgram.ResolveAppDataDirectory());
        using var storage = PlatformDeepSecureStorage.CreateV2(root);
        var accounts = new DeepIdV2AccountService(storage,
            Path.Combine(root, "deep-store-v2"),
            ActiveBuildNetworkId.Load().Span, 1, new SystemClock(),
            DeepMlDsa65CandidateVerifierFactory.OpenForCurrentProcess);
        await accounts.ResetExplicitlyAsync(cancellationToken)
            .ConfigureAwait(false);
    }
#endif

    internal static async Task<DeepIdV2AccountRuntimeOwner> OpenAsync(
        string appDataDirectory, ReadOnlyMemory<byte> networkId,
        ushort deploymentProfileId, IClock clock,
        Func<IDeepMlDsa65VerifierLease> verifierFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(verifierFactory);
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(appDataDirectory);
        var storage = PlatformDeepSecureStorage.CreateV2(root);
        try
        {
            var accounts = new DeepIdV2AccountService(storage,
                Path.Combine(root, "deep-store-v2"), networkId.Span,
                deploymentProfileId, clock, verifierFactory);
            _ = await accounts.GetCurrentAsync(cancellationToken)
                .ConfigureAwait(false);
            return new DeepIdV2AccountRuntimeOwner(storage, accounts);
        }
        catch
        {
            storage.Dispose();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
            storage.Dispose();
        return ValueTask.CompletedTask;
    }
}
