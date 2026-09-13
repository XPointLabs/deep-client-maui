using System.Security.Cryptography;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.Services.AccountDirectoryV1;
using Deep.Protocol.AccountDirectoryV1;

namespace Deep.Client.Maui.Services;

internal sealed class DeepAccountDirectoryAdmissionCoordinator :
    IDeepAccountDirectoryAdmissionCoordinator,
    IDisposable
{
    private readonly IDeepAccountRuntimeAccessor accounts;
    private readonly HttpServiceTransportFactory transportFactory;
    private readonly HttpServiceClientOptions clientOptions;
    private readonly string? registryUrl;
    private readonly SemaphoreSlim gate = new(1, 1);
    private byte[]? admittedCheckpointHash;
    private int disposed;

    internal DeepAccountDirectoryAdmissionCoordinator(
        IDeepAccountRuntimeAccessor accounts,
        HttpServiceTransportFactory transportFactory,
        HttpServiceClientOptions clientOptions,
        string? registryUrl)
    {
        this.accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        this.transportFactory = transportFactory
            ?? throw new ArgumentNullException(nameof(transportFactory));
        this.clientOptions = clientOptions
            ?? throw new ArgumentNullException(nameof(clientOptions));
        this.registryUrl = string.IsNullOrWhiteSpace(registryUrl)
            ? null
            : registryUrl.Trim();
    }

    public async Task EnsureCurrentAccountAdmittedAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var accountService = await accounts.GetAccountsAsync(cancellationToken)
            .ConfigureAwait(false);
        var identity = await accountService.GetLocalIdentityAsync(cancellationToken)
            .ConfigureAwait(false);
        if (identity is null ||
            identity.Account.ActivationState ==
                DeepAccountActivationState.RestorePendingActivation)
        {
            return;
        }
        await accounts.EnsureLocalIdentityActivatedAsync(cancellationToken)
            .ConfigureAwait(false);
        if (registryUrl is null)
        {
            return;
        }
        var activation = await accountService
            .EnsureGenesisDeviceActivatedAsync(cancellationToken)
            .ConfigureAwait(false);
        var exactCheckpoint = AccountDirectoryAdc1Codec.Encode(
            activation.DirectoryCheckpoint.Checkpoint);
        var checkpointHash = SHA256.HashData(exactCheckpoint);
        CryptographicOperations.ZeroMemory(exactCheckpoint);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (admittedCheckpointHash is not null &&
                CryptographicOperations.FixedTimeEquals(
                    admittedCheckpointHash, checkpointHash))
            {
                return;
            }

            using var client = transportFactory
                .CreateAccountDirectoryGenesisAdmissionClient(
                    registryUrl, clientOptions);
            _ = await client.AdmitAsync(activation, cancellationToken)
                .ConfigureAwait(false);
            var previous = admittedCheckpointHash;
            admittedCheckpointHash = checkpointHash.ToArray();
            if (previous is not null)
            {
                CryptographicOperations.ZeroMemory(previous);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(checkpointHash);
            gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        if (admittedCheckpointHash is not null)
        {
            CryptographicOperations.ZeroMemory(admittedCheckpointHash);
            admittedCheckpointHash = null;
        }
        gate.Dispose();
    }
}
