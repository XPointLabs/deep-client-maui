using Deep.Client.Shared.Features;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Domain.MessagingV1;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Persistence.GroupV1;
using Deep.Client.Shared.Persistence.MessagingV1;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.Services.GroupV1;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.Services;

internal sealed record StoreBoundRuntimeTransportComposition(
    ISessionMessageTransport Transport,
    IMailboxDeliveryPolicy DeliveryPolicy);

internal sealed record DeepGroupV1RuntimeBinding(
    SqliteGroupStateStore StateStore,
    SqliteGroupInvitationActivationStore InvitationActivationStore,
    MessageStoreScope MessagingScope,
    IDeepGroupV1Runtime Runtime);

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
        IGroupMailboxRouteExchange? groupMailboxRoutes = null)
        => CreateCore(
            stateDbPath,
            featureFlags,
            clock,
            avatarProfiles,
            sqlCipherKey,
            transportFactory,
            transportOutboxExecutor,
            groupMailboxRoutes,
            groupV1: null);

    internal static async Task<ClientRuntime> CreateAsync(
        string stateDbPath,
        ClientFeatureFlags featureFlags,
        IClock clock,
        IAvatarProfileTransport avatarProfiles,
        string sqlCipherKey,
        Func<SqliteSessionStore, SecureRecoverySessionStore,
            StoreBoundRuntimeTransportComposition> transportFactory,
        IExternalTransportOutboxExecutor? transportOutboxExecutor,
        DeepAccountRuntimeAccessor accountRuntime,
        IGroupMailboxRouteExchange? groupMailboxRoutes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accountRuntime);
        var groupV1 = await accountRuntime
            .TryGetGroupV1RuntimeBindingAsync(cancellationToken)
            .ConfigureAwait(false);
        return CreateCore(
            stateDbPath,
            featureFlags,
            clock,
            avatarProfiles,
            sqlCipherKey,
            transportFactory,
            transportOutboxExecutor,
            groupMailboxRoutes,
            groupV1);
    }

    private static ClientRuntime CreateCore(
        string stateDbPath,
        ClientFeatureFlags featureFlags,
        IClock clock,
        IAvatarProfileTransport avatarProfiles,
        string sqlCipherKey,
        Func<SqliteSessionStore, SecureRecoverySessionStore,
            StoreBoundRuntimeTransportComposition> transportFactory,
        IExternalTransportOutboxExecutor? transportOutboxExecutor,
        IGroupMailboxRouteExchange? groupMailboxRoutes,
        DeepGroupV1RuntimeBinding? groupV1)
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
                messageDispatchFailureObserver: PhysicalE2eMessageDispatchFailureObserver.Instance,
#else
                messageDispatchFailureObserver: null,
#endif
                groupMailboxRoutes: groupMailboxRoutes,
                messagingV1Persistence: new MessagingV1PersistenceOptions(
                    stateDbPath + ".msg01",
                    sqlCipherKey,
                    groupV1?.MessagingScope),
                groupV1StateStore: groupV1?.StateStore);
#if DEBUG && DEEP_PHYSICAL_E2E
            PhysicalE2eAckCorrelationProvider.Bind(runtime);
#endif
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
