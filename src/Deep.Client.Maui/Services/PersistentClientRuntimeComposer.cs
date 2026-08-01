using Deep.Client.Shared.Features;
using Deep.Client.Maui.Core.Services;
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
        string sqlCipherKey,
        bool requireE2eeTransport,
        IMailboxDeliveryPolicy mailboxDeliveryPolicy,
        IExternalTransportOutboxExecutor? transportOutboxExecutor,
        DeferredVerifiedMembershipRouteCatalogProvider? membershipRouteCatalogProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDbPath);
        ArgumentNullException.ThrowIfNull(featureFlags);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(messageTransport);
        ArgumentNullException.ThrowIfNull(avatarProfiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlCipherKey);
        ArgumentNullException.ThrowIfNull(mailboxDeliveryPolicy);

        SecureRecoverySessionStore? secureStore = null;
        var runtime = ClientRuntime.CreatePersistent(
            stateDbPath,
            featureFlags,
            clock,
            messageTransport,
            groupSyncTransport: null,
            avatarProfiles,
            sqlCipherKey,
            storeDecorator: store =>
            {
                secureStore = new SecureRecoverySessionStore(
                    store,
                    transportOutboxExecutor as IDisposable);
                return secureStore;
            },
            requireE2eeTransport,
            transportOutboxExecutor,
            mailboxDeliveryPolicy);
        try
        {
            if (membershipRouteCatalogProvider is not null)
            {
                membershipRouteCatalogProvider.Bind(
                    secureStore ??
                    throw new InvalidOperationException(
                        "The persistent runtime did not construct its secured store."));
            }
            return runtime;
        }
        catch
        {
            runtime.Dispose();
            throw;
        }
    }
}
