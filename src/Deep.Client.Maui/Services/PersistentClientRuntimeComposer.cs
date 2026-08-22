using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Features;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.Services;

internal sealed record StoreBoundRuntimeTransportComposition(
    ISessionMessageTransport Transport,
    IMailboxDeliveryPolicy DeliveryPolicy);

internal static class PersistentClientRuntimeComposer
{
    public static ClientRuntime Create(
        string stateDbPath,
        ClientFeatureFlags featureFlags,
        IClock clock,
        IAvatarProfileTransport avatarProfiles,
        string sqlCipherKey,
        Func<SqliteSessionStore, SecureRecoverySessionStore,
            StoreBoundRuntimeTransportComposition> transportFactory,
        IExternalTransportOutboxExecutor? transportOutboxExecutor,
        DeferredVerifiedMembershipRouteCatalogProvider? membershipRouteCatalogProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDbPath);
        ArgumentNullException.ThrowIfNull(featureFlags);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(avatarProfiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlCipherKey);
        ArgumentNullException.ThrowIfNull(transportFactory);

        var sqlite = new SqliteSessionStore(
            new SqliteSessionStoreOptions(stateDbPath, sqlCipherKey));
        var secureStore = new SecureRecoverySessionStore(
            sqlite,
            transportOutboxExecutor as IDisposable);
        StoreBoundRuntimeTransportComposition? composition = null;
        ClientRuntime? runtime = null;
        try
        {
            composition = transportFactory(sqlite, secureStore) ??
                throw new InvalidOperationException(
                    "The store-bound transport factory returned no composition.");
            ArgumentNullException.ThrowIfNull(composition.Transport);
            ArgumentNullException.ThrowIfNull(composition.DeliveryPolicy);
            runtime = new ClientRuntime(
                secureStore,
                featureFlags,
                clock,
                composition.Transport,
                groupSyncTransport: null,
                avatarProfiles,
                requireE2eeTransport: true,
                transportOutboxExecutor,
                composition.DeliveryPolicy,
                ownsMessageTransport: true,
#if DEBUG && DEEP_PHYSICAL_E2E
                messageDispatchFailureObserver: PhysicalE2eMessageDispatchFailureObserver.Instance);
#else
                messageDispatchFailureObserver: null);
#endif
            if (membershipRouteCatalogProvider is not null)
                membershipRouteCatalogProvider.Bind(secureStore);
            return runtime;
        }
        catch
        {
            if (runtime is not null)
            {
                runtime.Dispose();
            }
            else
            {
                (composition?.Transport as IDisposable)?.Dispose();
                secureStore.Dispose();
            }
            throw;
        }
    }
}
