using Deep.Client.Shared.Services.ContactV1;
using Deep.Client.Shared.Domain.ContactV1;
using Deep.Client.Shared.Persistence.ContactV1;

namespace Deep.Client.Maui.Core.Services;

/// <summary>
/// Opens the account-scoped local ContactV1 graph. Reading the permanent Deep
/// ID never requires network/bootstrap services. Import-and-resolve always
/// persists the canonical address before it inspects network prerequisites.
/// </summary>
public interface IDeepContactRuntimeAccessor
{
    Task<string> GetPermanentDeepIdAsync(
        CancellationToken cancellationToken = default);

    Task<ContactImportAndResolveResult> ImportAndEnqueueResolveAsync(
        string input,
        CancellationToken cancellationToken = default);
}

public enum ContactResolveQueueState
{
    PendingRetry = 1,
    Verified = 2,
    Terminal = 3,
    FailClosed = 4,
}

public sealed record ContactImportAndResolveResult(
    PendingContactAddressWriteDisposition ImportDisposition,
    ContactResolveQueueState QueueState,
    string Title,
    string Message,
    bool CanRetry,
    ContactResolverDisposition? ResolverDisposition = null,
    ContactResolverRetryClassification? ResolverRetry = null,
    VerifiedDirectConversationTarget? VerifiedConversation = null);

/// <summary>
/// Opaque navigation handoff minted only after ContactV1 linked the verified
/// relationship. It is deliberately not a legacy SessionId or a route.
/// </summary>
public sealed class VerifiedDirectConversationTarget
{
    internal VerifiedDirectConversationTarget(
        ContactRelationshipId32 relationshipId,
        ContactConversationId32 conversationId)
    {
        ArgumentNullException.ThrowIfNull(relationshipId);
        ArgumentNullException.ThrowIfNull(conversationId);
        RelationshipId = ContactRelationshipId32.FromBytes(relationshipId.ToArray());
        ConversationId = ContactConversationId32.FromBytes(conversationId.ToArray());
    }

    public ContactRelationshipId32 RelationshipId { get; }

    public ContactConversationId32 ConversationId { get; }
}

public static class ContactResolvePendingStatus
{
    public const string Title = "Контакт сохранён";
    public const string GenesisPinUnavailable =
        "Контакт сохранён локально. Проверка отложена: в этой сборке отсутствует закреплённый genesis XPoint Network.";
    public const string MonotonicClockUnavailable =
        "Контакт сохранён локально. Проверка отложена: защищённые монотонные часы недоступны.";
    public const string PrivacyRouteUnavailable =
        "Контакт сохранён локально. Проверка отложена: приватный маршрут Contact Resolver недоступен.";
    public const string AuthoritySourceUnavailable =
        "Контакт сохранён локально. Проверка отложена: проверяющий источник Contact Resolver недоступен.";
    public const string DirectorySourceUnavailable =
        "Контакт сохранён локально. Проверка отложена: источник подтверждённых данных каталога недоступен.";
    public const string MalformedConfiguration =
        "Контакт сохранён локально. Проверка отложена: конфигурация Contact Resolver некорректна.";
    public const string CrossNetworkConfiguration =
        "Контакт сохранён локально. Проверка отложена: Contact Resolver настроен для другой XPoint Network.";
    public const string RetryableFailure =
        "Контакт сохранён локально. Проверка временно недоступна; повторите добавление контакта позже.";
    public const string VerificationFailed =
        "Контакт сохранён локально, но проверка доказательств завершилась отказом. Небезопасный контакт не создан.";
}
