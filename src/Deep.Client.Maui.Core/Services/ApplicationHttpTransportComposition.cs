using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.Core.Services;

public sealed record ApplicationHttpTransportFactories(
    Func<IServiceProvider, IAvatarProfileTransport> Avatar,
    Func<IServiceProvider, IAttachmentFileTransport> Attachment,
    Func<IServiceProvider, IPushSubscriptionTransport> Push,
    Func<IServiceProvider, ICallSignalingTransport> Calls);

public static class ApplicationHttpTransportComposition
{
    public static ApplicationHttpTransportFactories Create(
        HttpServiceTransportFactory transportFactory,
        string? fileBaseUrl,
        string? pushBaseUrl,
        string? callSignalingBaseUrl,
        Func<HttpClient> createFileHttpClient,
        Func<HttpClient> createServiceHttpClient)
    {
        ArgumentNullException.ThrowIfNull(transportFactory);
        ArgumentNullException.ThrowIfNull(createFileHttpClient);
        ArgumentNullException.ThrowIfNull(createServiceHttpClient);

        Func<IServiceProvider, IAvatarProfileTransport> avatar =
            string.IsNullOrWhiteSpace(fileBaseUrl)
                ? _ => new DisabledAvatarProfileTransport()
                : _ => transportFactory.CreateAvatar(
                    createFileHttpClient(),
                    new HttpAvatarProfileTransportOptions(fileBaseUrl));
        Func<IServiceProvider, IAttachmentFileTransport> attachment =
            string.IsNullOrWhiteSpace(fileBaseUrl)
                ? _ => new DisabledAttachmentFileTransport()
                : _ => transportFactory.CreateAttachment(
                    createFileHttpClient(),
                    new HttpAttachmentFileTransportOptions(fileBaseUrl));
        Func<IServiceProvider, IPushSubscriptionTransport> push =
            string.IsNullOrWhiteSpace(pushBaseUrl)
                ? _ => new DisabledPushSubscriptionTransport()
                : _ => transportFactory.CreatePush(
                    createServiceHttpClient(),
                    new HttpPushSubscriptionTransportOptions(pushBaseUrl));
        Func<IServiceProvider, ICallSignalingTransport> calls =
            string.IsNullOrWhiteSpace(callSignalingBaseUrl)
                ? _ => new InMemoryCallSignalingTransport()
                : services => transportFactory.CreateCallSignaling(
                    createServiceHttpClient(),
                    new HttpCallSignalingTransportOptions(callSignalingBaseUrl),
                    cancellationToken =>
                    {
                        var runtime = services.GetService(typeof(ClientRuntime))
                            as ClientRuntime
                            ?? throw new InvalidOperationException(
                                "Client runtime is required for call signaling.");
                        return runtime.Accounts.GetRecoveryPhraseAsync(cancellationToken);
                    });
        return new(avatar, attachment, push, calls);
    }
}
