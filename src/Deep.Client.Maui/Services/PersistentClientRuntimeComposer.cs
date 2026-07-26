using Deep.Client.Shared.Features;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.Services;

internal static class PersistentClientRuntimeComposer
{
    public static ClientRuntime Create(
        string stateDbPath,
        ClientFeatureFlags featureFlags,
        IClock clock,
        ISessionMessageTransport messageTransport,
        IAvatarProfileTransport avatarProfiles,
        string legacyStatePath,
        string sqlCipherKey,
        bool requireE2eeTransport,
        IExternalTransportOutboxExecutor? transportOutboxExecutor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDbPath);
        ArgumentNullException.ThrowIfNull(featureFlags);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(messageTransport);
        ArgumentNullException.ThrowIfNull(avatarProfiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyStatePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlCipherKey);

        return ClientRuntime.CreatePersistent(
            stateDbPath,
            featureFlags,
            clock,
            messageTransport,
            groupSyncTransport: null,
            avatarProfiles,
            legacyInMemoryStatePath: legacyStatePath,
            sqlCipherKey,
            storeDecorator: store => new SecureRecoverySessionStore(
                store,
                transportOutboxExecutor as IDisposable),
            requireE2eeTransport,
            transportOutboxExecutor);
    }
}
