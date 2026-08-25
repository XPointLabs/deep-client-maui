using System.Security.Cryptography;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui.Services;

internal enum LocalStateDatabaseLane
{
    Ordinary,
    PhysicalE2E
}

internal interface ILocalStateDatabaseKeyStorage
{
    Task<string?> GetAsync(string key);

    Task SetAsync(string key, string value);

    bool Remove(string key);
}

internal sealed class MauiLocalStateDatabaseKeyStorage : ILocalStateDatabaseKeyStorage
{
    public Task<string?> GetAsync(string key) => SecureStorage.Default.GetAsync(key);

    public Task SetAsync(string key, string value) => SecureStorage.Default.SetAsync(key, value);

    public bool Remove(string key) => SecureStorage.Default.Remove(key);
}

internal sealed class LocalStateDatabaseKeySlot
{
    internal const string OrdinarySlotName = "client-state.sqlcipher-key.v1";
    internal const string PhysicalE2ESlotName =
        "client-state.sqlcipher-key.physical-e2e.v1";

#if DEEP_PHYSICAL_E2E
    internal const string ActiveSlotName = PhysicalE2ESlotName;
#else
    internal const string ActiveSlotName = OrdinarySlotName;
#endif

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly ILocalStateDatabaseKeyStorage storage;

    private LocalStateDatabaseKeySlot(
        string name,
        ILocalStateDatabaseKeyStorage storage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(storage);
        Name = name;
        this.storage = storage;
    }

    internal string Name { get; }

    internal static LocalStateDatabaseKeySlot Active { get; } =
        new(ActiveSlotName, new MauiLocalStateDatabaseKeyStorage());

    internal static LocalStateDatabaseKeySlot ForLane(
        LocalStateDatabaseLane lane,
        ILocalStateDatabaseKeyStorage storage) =>
        new(NameForLane(lane), storage);

    internal static string NameForLane(LocalStateDatabaseLane lane) => lane switch
    {
        LocalStateDatabaseLane.Ordinary => OrdinarySlotName,
        LocalStateDatabaseLane.PhysicalE2E => PhysicalE2ESlotName,
        _ => throw new ArgumentOutOfRangeException(nameof(lane), lane, null)
    };

    internal async Task<string> ResolveAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = await storage.GetAsync(Name).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }

            var key = GenerateKey();
            await storage.SetAsync(Name, key).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return key;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Secure local database key storage is unavailable.", ex);
        }
        finally
        {
            Gate.Release();
        }
    }

    internal async Task<string> ResetAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            storage.Remove(Name);
            var key = GenerateKey();
            await storage.SetAsync(Name, key).ConfigureAwait(false);
            return key;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Secure local database key storage is unavailable.", ex);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string GenerateKey() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
