using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.Core.Services;

public sealed record ApplicationHttpTransportFactories(
    Func<IServiceProvider, IAvatarProfileTransport> Avatar,
    Func<IServiceProvider, IAttachmentFileTransport> Attachment,
    Func<IServiceProvider, IPushSubscriptionTransport> Push,
    Func<IServiceProvider, ICallSignalingTransport> Calls,
    HttpServiceTransportFactory ServiceTransportFactory,
    HttpServiceClientOptions ServiceClientOptions);

public static class ApplicationHttpTransportComposition
{
    public static ApplicationHttpTransportFactories Create(
        HttpServiceTransportFactory fileTransportFactory,
        HttpServiceTransportFactory serviceTransportFactory,
        string? fileBaseUrl,
        string? pushBaseUrl,
        string? callSignalingBaseUrl,
        HttpServiceClientOptions? fileClientOptions = null,
        HttpServiceClientOptions? serviceClientOptions = null)
        => CreateCore(
            fileTransportFactory,
            serviceTransportFactory,
            fileBaseUrl,
            pushBaseUrl,
            callSignalingBaseUrl,
            fileClientOptions,
            serviceClientOptions);

    internal static ApplicationHttpTransportFactories CreateBoundNetwork(
        HttpServiceTransportFactory transportFactory,
        HttpServiceNetworkHooks fileNetworkHooks,
        HttpServiceNetworkHooks serviceNetworkHooks,
        string? fileBaseUrl,
        string? pushBaseUrl,
        string? callSignalingBaseUrl,
        HttpServiceClientOptions? fileClientOptions = null,
        HttpServiceClientOptions? serviceClientOptions = null)
    {
        ArgumentNullException.ThrowIfNull(transportFactory);
        return CreateCore(
            transportFactory.BindNetwork(fileNetworkHooks),
            transportFactory.BindNetwork(serviceNetworkHooks),
            fileBaseUrl,
            pushBaseUrl,
            callSignalingBaseUrl,
            fileClientOptions,
            serviceClientOptions);
    }

    private static ApplicationHttpTransportFactories CreateCore(
        HttpServiceTransportFactory fileTransportFactory,
        HttpServiceTransportFactory serviceTransportFactory,
        string? fileBaseUrl,
        string? pushBaseUrl,
        string? callSignalingBaseUrl,
        HttpServiceClientOptions? fileClientOptions,
        HttpServiceClientOptions? serviceClientOptions)
    {
        ArgumentNullException.ThrowIfNull(fileTransportFactory);
        ArgumentNullException.ThrowIfNull(serviceTransportFactory);
        fileClientOptions ??= new HttpServiceClientOptions();
        serviceClientOptions ??= new HttpServiceClientOptions();

        Func<IServiceProvider, IAvatarProfileTransport> avatar =
            string.IsNullOrWhiteSpace(fileBaseUrl)
                ? _ => new DisabledAvatarProfileTransport()
                : _ => fileTransportFactory.CreateAvatar(
                    new HttpAvatarProfileTransportOptions(fileBaseUrl),
                    fileClientOptions);
        Func<IServiceProvider, IAttachmentFileTransport> attachment =
            string.IsNullOrWhiteSpace(fileBaseUrl)
                ? _ => new DisabledAttachmentFileTransport()
                : _ => fileTransportFactory.CreateAttachment(
                    new HttpAttachmentFileTransportOptions(fileBaseUrl),
                    fileClientOptions);
        Func<IServiceProvider, IPushSubscriptionTransport> push =
            string.IsNullOrWhiteSpace(pushBaseUrl)
                ? _ => new DisabledPushSubscriptionTransport()
                : _ => serviceTransportFactory.CreatePush(
                    new HttpPushSubscriptionTransportOptions(pushBaseUrl),
                    serviceClientOptions);
        Func<IServiceProvider, ICallSignalingTransport> calls =
            string.IsNullOrWhiteSpace(callSignalingBaseUrl)
                ? _ => new InMemoryCallSignalingTransport()
                : services => serviceTransportFactory.CreateCallSignaling(
                    new HttpCallSignalingTransportOptions(callSignalingBaseUrl),
                    new ServiceProviderCallRecoveryPhraseProvider(services),
                    clientOptions: serviceClientOptions);
        return new(
            avatar,
            attachment,
            push,
            calls,
            serviceTransportFactory,
            serviceClientOptions);
    }

    private sealed class ServiceProviderCallRecoveryPhraseProvider(
        IServiceProvider services) : ICallRecoveryPhraseProvider
    {
        public Task<string?> GetRecoveryPhraseAsync(
            CancellationToken cancellationToken = default)
        {
            var runtime = services.GetService(typeof(ClientRuntime))
                as ClientRuntime
                ?? throw new InvalidOperationException(
                    "Client runtime is required for call signaling.");
            return runtime.Accounts.GetRecoveryPhraseAsync(cancellationToken);
        }
    }
}
