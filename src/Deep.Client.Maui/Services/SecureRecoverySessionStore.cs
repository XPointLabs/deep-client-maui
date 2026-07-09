using System.Runtime.CompilerServices;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Microsoft.Maui.Storage;
using DomainContact = Deep.Client.Shared.Domain.Contact;

namespace Deep.Client.Maui.Services;

internal sealed class SecureRecoverySessionStore(ILocalSessionStore inner) : ILocalSessionStore, IOneToOneConversationOpenRepository, IDisposable
{
    private const string SecureRecoveryPhraseKey = "deep.account.recovery-phrase.v1";

    public Task UpsertAsync(Conversation conversation, CancellationToken cancellationToken = default) =>
        inner.UpsertAsync(conversation, cancellationToken);

    public Task<Conversation?> GetAsync(ConversationId id, CancellationToken cancellationToken = default) =>
        ((IConversationRepository)inner).GetAsync(id, cancellationToken);

    public async IAsyncEnumerable<Conversation> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in ((IConversationRepository)inner).ListAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    public Task UpsertAsync(DomainContact contact, CancellationToken cancellationToken = default) =>
        inner.UpsertAsync(contact, cancellationToken);

    public Task<DomainContact?> GetAsync(SessionId id, CancellationToken cancellationToken = default) =>
        inner.GetAsync(id, cancellationToken);

    async IAsyncEnumerable<DomainContact> IContactRepository.ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in ((IContactRepository)inner).ListAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    public Task UpsertAsync(Group group, CancellationToken cancellationToken = default) =>
        inner.UpsertAsync(group, cancellationToken);

    Task<Group?> IGroupRepository.GetAsync(ConversationId id, CancellationToken cancellationToken) =>
        ((IGroupRepository)inner).GetAsync(id, cancellationToken);

    async IAsyncEnumerable<Group> IGroupRepository.ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in ((IGroupRepository)inner).ListAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    public Task AppendAsync(Message message, CancellationToken cancellationToken = default) =>
        inner.AppendAsync(message, cancellationToken);

    public Task UpdateAsync(Message message, CancellationToken cancellationToken = default) =>
        inner.UpdateAsync(message, cancellationToken);

    public Task DeleteAsync(MessageId id, CancellationToken cancellationToken = default) =>
        inner.DeleteAsync(id, cancellationToken);

    public Task<Message?> GetAsync(MessageId id, CancellationToken cancellationToken = default) =>
        inner.GetAsync(id, cancellationToken);

    async IAsyncEnumerable<Message> IMessageRepository.ListForConversationAsync(
        ConversationId conversationId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in ((IMessageRepository)inner).ListForConversationAsync(conversationId, cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    async IAsyncEnumerable<Message> IMessageRepository.ListRecentForConversationAsync(
        ConversationId conversationId,
        int limit,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in ((IMessageRepository)inner).ListRecentForConversationAsync(conversationId, limit, cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    async IAsyncEnumerable<Message> IMessageRepository.ListBeforeForConversationAsync(
        ConversationId conversationId,
        DateTimeOffset beforeCreatedAt,
        int limit,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in ((IMessageRepository)inner).ListBeforeForConversationAsync(conversationId, beforeCreatedAt, limit, cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    Task<int> IMessageRepository.CountUnreadForConversationAsync(
        ConversationId conversationId,
        DateTimeOffset? readCursor,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ((IMessageRepository)inner).CountUnreadForConversationAsync(conversationId, readCursor, now, cancellationToken);

    public Task<IReadOnlyDictionary<ConversationId, ConversationListSummary>> GetConversationSummariesAsync(
        IReadOnlyCollection<ConversationId> conversationIds,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        ((IConversationListSummaryRepository)inner).GetConversationSummariesAsync(conversationIds, now, cancellationToken);

    public Task<OneToOneConversationOpenSnapshot?> OpenOneToOneConversationAsync(
        SessionId recipient,
        string? displayName,
        int messageLimit,
        DateTimeOffset now,
        CancellationToken cancellationToken = default,
        bool markAsRead = true) =>
        inner is IOneToOneConversationOpenRepository repository
            ? repository.OpenOneToOneConversationAsync(recipient, displayName, messageLimit, now, cancellationToken, markAsRead)
            : Task.FromException<OneToOneConversationOpenSnapshot?>(
                new NotSupportedException("The wrapped session store does not support optimized one-to-one conversation opening."));

    public async Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        if (!IsRecoveryPhraseKey(key))
        {
            await inner.SetAsync(key, value, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (value is not string phrase)
        {
            throw new InvalidOperationException("Recovery phrase must be stored as a string.");
        }

        await SecureStorage.SetAsync(SecureRecoveryPhraseKey, phrase).ConfigureAwait(false);
        await inner.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        if (!IsRecoveryPhraseKey(key))
        {
            return await inner.GetAsync<T>(key, cancellationToken).ConfigureAwait(false);
        }

        if (typeof(T) != typeof(string))
        {
            throw new InvalidOperationException("Recovery phrase must be read as a string.");
        }

        var phrase = await SecureStorage.GetAsync(SecureRecoveryPhraseKey).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(phrase))
        {
            return (T?)(object)phrase;
        }

        var legacyPhrase = await inner.GetAsync<string>(key, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(legacyPhrase))
        {
            return default;
        }

        await SecureStorage.SetAsync(SecureRecoveryPhraseKey, legacyPhrase).ConfigureAwait(false);
        await inner.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        return (T?)(object)legacyPhrase;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        if (IsRecoveryPhraseKey(key))
        {
            SecureStorage.Remove(SecureRecoveryPhraseKey);
        }

        return inner.DeleteAsync(key, cancellationToken);
    }

    public Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default) =>
        inner.GetSchemaVersionAsync(cancellationToken);

    public Task SetSchemaVersionAsync(int version, CancellationToken cancellationToken = default) =>
        inner.SetSchemaVersionAsync(version, cancellationToken);

    public Task SetSchemaValueAsync(string key, string value, CancellationToken cancellationToken = default) =>
        inner.SetSchemaValueAsync(key, value, cancellationToken);

    public Task<string?> GetSchemaValueAsync(string key, CancellationToken cancellationToken = default) =>
        inner.GetSchemaValueAsync(key, cancellationToken);

    private static bool IsRecoveryPhraseKey(string key) =>
        string.Equals(key, SessionAccountService.ActiveRecoveryPhraseKey, StringComparison.Ordinal);

    public void Dispose()
    {
        if (inner is IDisposable disposableInner)
        {
            disposableInner.Dispose();
        }
    }
}
