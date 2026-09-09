using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Services;

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

#if DEEP_TEST_INTERNALS
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
#endif

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
                : _ => CreateUnavailableCallSignaling(callSignalingBaseUrl);
        return new(
            avatar,
            attachment,
            push,
            calls,
            serviceTransportFactory,
            serviceClientOptions);
    }

    private static ICallSignalingTransport CreateUnavailableCallSignaling(
        string configuredOrigin)
    {
        if (!Uri.TryCreate(configuredOrigin, UriKind.Absolute, out var origin)
            || origin.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(origin.UserInfo)
            || !string.IsNullOrEmpty(origin.Query)
            || !string.IsNullOrEmpty(origin.Fragment)
            || origin.AbsolutePath != "/")
        {
            throw new ArgumentException(
                "Call signaling base URL must be an absolute HTTPS service origin.",
                nameof(configuredOrigin));
        }

        return new CleanBreakCallSignalingTransportUnavailable();
    }

    private sealed class CleanBreakCallSignalingTransportUnavailable :
        ICallSignalingTransport,
        ICallIceConfigurationProvider
    {
        private const string Message =
            "Legacy recovery-phrase call signaling is disabled. " +
            "Typed ratcheted Deep call signaling is not composed yet.";

        public Task SendAsync(
            CallSignalEnvelope envelope,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new NotSupportedException(Message));

        public Task<IReadOnlyList<CallSignalEnvelope>> ReceiveAsync(
            SessionId recipient,
            CancellationToken cancellationToken = default) =>
            Task.FromException<IReadOnlyList<CallSignalEnvelope>>(
                new NotSupportedException(Message));

        public Task<CallIceConfiguration> GetAsync(
            SessionId recipient,
            CancellationToken cancellationToken = default) =>
            Task.FromException<CallIceConfiguration>(
                new NotSupportedException(Message));
    }
}
