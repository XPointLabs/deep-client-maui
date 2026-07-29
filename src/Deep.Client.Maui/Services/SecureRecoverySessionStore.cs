using System.Runtime.CompilerServices;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Microsoft.Maui.Storage;
using DomainContact = Deep.Client.Shared.Domain.Contact;

namespace Deep.Client.Maui.Services;

internal sealed class SecureRecoverySessionStore(
    ILocalSessionStore inner,
    IDisposable? ownedLifetime = null) :
    ILocalSessionStore,
    IOneToOneConversationOpenRepository,
    IMessageSyncRepository,
    ITransportOutboxRepository,
    IMembershipTrustRepository,
    IDisposable
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

    public Task DeleteAsync(SessionId id, CancellationToken cancellationToken = default) =>
        inner.DeleteAsync(id, cancellationToken);

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
        DateTimeOffset now,
        int limit,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in ((IMessageRepository)inner).ListRecentForConversationAsync(conversationId, now, limit, cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    async IAsyncEnumerable<Message> IMessageRepository.ListBeforeForConversationAsync(
        ConversationId conversationId,
        DateTimeOffset beforeCreatedAt,
        MessageId beforeMessageId,
        DateTimeOffset now,
        int limit,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in ((IMessageRepository)inner).ListBeforeForConversationAsync(conversationId, beforeCreatedAt, beforeMessageId, now, limit, cancellationToken).ConfigureAwait(false))
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

    Task<bool> IMessageSyncRepository.ContainsServerHashAsync(
        ConversationId conversationId,
        string serverHash,
        CancellationToken cancellationToken) =>
        RequireMessageSyncRepository().ContainsServerHashAsync(conversationId, serverHash, cancellationToken);

    Task<bool> IMessageSyncRepository.ContainsMatchingSelfOutgoingAsync(
        ConversationId conversationId,
        SessionId account,
        DateTimeOffset createdAt,
        string body,
        IReadOnlyList<AttachmentMetadata> attachments,
        CancellationToken cancellationToken) =>
        RequireMessageSyncRepository().ContainsMatchingSelfOutgoingAsync(
            conversationId,
            account,
            createdAt,
            body,
            attachments,
            cancellationToken);

    Task<int> IMessageSyncRepository.DeleteDuplicateSelfIncomingAsync(
        ConversationId conversationId,
        SessionId account,
        CancellationToken cancellationToken) =>
        RequireMessageSyncRepository().DeleteDuplicateSelfIncomingAsync(conversationId, account, cancellationToken);

    Task<IReadOnlyList<Message>> IMessageSyncRepository.ListPendingOutgoingAsync(
        SessionId sender,
        CancellationToken cancellationToken) =>
        RequireMessageSyncRepository().ListPendingOutgoingAsync(sender, cancellationToken);

    Task<ConversationReadResult> IMessageSyncRepository.MarkConversationReadIfUnreadAsync(
        ConversationId conversationId,
        DateTimeOffset readAt,
        CancellationToken cancellationToken) =>
        RequireMessageSyncRepository().MarkConversationReadIfUnreadAsync(conversationId, readAt, cancellationToken);

    Task<int> IMessageSyncRepository.ApplyReadCursorAsync(
        ConversationId conversationId,
        DateTimeOffset readAt,
        CancellationToken cancellationToken) =>
        RequireMessageSyncRepository().ApplyReadCursorAsync(conversationId, readAt, cancellationToken);

    private IMessageSyncRepository RequireMessageSyncRepository() =>
        inner as IMessageSyncRepository
        ?? throw new InvalidOperationException("The secured session store requires indexed message synchronization support.");

    public Task MarkConversationReadAsync(
        ConversationId conversationId,
        DateTimeOffset readAt,
        CancellationToken cancellationToken = default) =>
        ((IConversationReadRepository)inner).MarkConversationReadAsync(conversationId, readAt, cancellationToken);

    public Task AppendMessageAndTouchConversationAsync(
        Message message,
        Conversation conversation,
        CancellationToken cancellationToken = default) =>
        ((IMessageConversationPersistenceRepository)inner).AppendMessageAndTouchConversationAsync(
            message,
            conversation,
            cancellationToken);

    public Task<IReadOnlyList<PendingIncomingMessageNotification>> ListPendingIncomingMessageNotificationIdsAsync(
        int limit,
        CancellationToken cancellationToken = default) =>
        inner.ListPendingIncomingMessageNotificationIdsAsync(limit, cancellationToken);

    public Task<IReadOnlyList<PendingIncomingMessageNotification>> ListPendingIncomingMessageNotificationIdsAsync(
        int limit,
        IReadOnlyCollection<ConversationId> excludedConversationIds,
        CancellationToken cancellationToken = default) =>
        inner.ListPendingIncomingMessageNotificationIdsAsync(
            limit,
            excludedConversationIds,
            cancellationToken);

    public Task MarkIncomingMessageNotificationsPresentedAsync(
        IReadOnlyCollection<MessageId> ids,
        CancellationToken cancellationToken = default) =>
        inner.MarkIncomingMessageNotificationsPresentedAsync(ids, cancellationToken);

    public Task<int> DeleteExpiredMessagesAsync(
        ConversationId conversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        ((IMessageConversationPersistenceRepository)inner).DeleteExpiredMessagesAsync(
            conversationId,
            now,
            cancellationToken);

    public Task<int> ClearConversationMessagesAsync(
        ConversationId conversationId,
        CancellationToken cancellationToken = default) =>
        ((IMessageConversationPersistenceRepository)inner).ClearConversationMessagesAsync(
            conversationId,
            cancellationToken);

    public Task<MessageReplayClaimResult> TryClaimAsync(
        SessionId sender,
        MessageId messageId,
        string envelopeDigest,
        DateTimeOffset protocolExpiresAt,
        CancellationToken cancellationToken = default) =>
        ((IMessageReplayRepository)inner).TryClaimAsync(
            sender,
            messageId,
            envelopeDigest,
            protocolExpiresAt,
            cancellationToken);

    public Task<int> PruneExpiredAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        ((IMessageReplayRepository)inner).PruneExpiredAsync(now, cancellationToken);

    public Task<string?> GetInboxCursorAsync(
        DurableInboxScope scope,
        CancellationToken cancellationToken = default) =>
        ((IDurableInboxRepository)inner).GetInboxCursorAsync(scope, cancellationToken);

    public Task<DurableInboxStageResult> StageInboxBatchAsync(
        DurableInboxScope scope,
        string? expectedCursor,
        string? nextCursor,
        IReadOnlyList<DurableInboxWireEntry> entries,
        CancellationToken cancellationToken = default) =>
        ((IDurableInboxRepository)inner).StageInboxBatchAsync(
            scope,
            expectedCursor,
            nextCursor,
            entries,
            cancellationToken);

    public Task<IReadOnlyList<DurableInboxItem>> ListStagedInboxItemsAsync(
        DurableInboxScope scope,
        int limit,
        CancellationToken cancellationToken = default) =>
        ((IDurableInboxRepository)inner).ListStagedInboxItemsAsync(scope, limit, cancellationToken);

    public Task<DurableInboxPrepareResult> PrepareInboxItemAsync(
        DurableInboxScope scope,
        string serverHash,
        DurableInboxDecodedMetadata decoded,
        CancellationToken cancellationToken = default) =>
        ((IDurableInboxRepository)inner).PrepareInboxItemAsync(scope, serverHash, decoded, cancellationToken);

    public Task<IReadOnlyList<DurableInboxItem>> ListDecodedInboxItemsAsync(
        DurableInboxScope scope,
        DurableInboxItemKind kind,
        string? routeKey,
        int limit,
        CancellationToken cancellationToken = default) =>
        ((IDurableInboxRepository)inner).ListDecodedInboxItemsAsync(scope, kind, routeKey, limit, cancellationToken);

    public Task<DurableInboxAckResult> AcknowledgeInboxItemAsync(
        DurableInboxScope scope,
        string serverHash,
        CancellationToken cancellationToken = default) =>
        ((IDurableInboxRepository)inner).AcknowledgeInboxItemAsync(scope, serverHash, cancellationToken);

    public Task DiscardInboxItemAsync(
        DurableInboxScope scope,
        string serverHash,
        CancellationToken cancellationToken = default) =>
        ((IDurableInboxRepository)inner).DiscardInboxItemAsync(scope, serverHash, cancellationToken);

    public Task<int> CountPendingInboxItemsAsync(
        DurableInboxScope scope,
        CancellationToken cancellationToken = default) =>
        ((IDurableInboxRepository)inner).CountPendingInboxItemsAsync(scope, cancellationToken);

    public Task<int> DiscardDecodedInboxItemsOutsideRoutesAsync(
        DurableInboxScope scope,
        DurableInboxItemKind kind,
        IReadOnlySet<string> retainedRouteKeys,
        CancellationToken cancellationToken = default) =>
        ((IDurableInboxRepository)inner).DiscardDecodedInboxItemsOutsideRoutesAsync(
            scope,
            kind,
            retainedRouteKeys,
            cancellationToken);

    public async Task PurgeAccountDataAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await ((IAccountDataPurger)inner).PurgeAccountDataAsync(cancellationToken).ConfigureAwait(false);
        SecureStorage.Remove(SecureRecoveryPhraseKey);
    }

    public Task PersistGroupStateAsync(
        Group group,
        Conversation conversation,
        DateTimeOffset updatedAt,
        GroupStateOutboxItem? outboxItem,
        CancellationToken cancellationToken = default) =>
        ((IGroupStatePersistenceRepository)inner).PersistGroupStateAsync(
            group,
            conversation,
            updatedAt,
            outboxItem,
            cancellationToken);

    public Task<IReadOnlyList<GroupStateOutboxItem>> ListPendingGroupStatePublishesAsync(
        int limit,
        CancellationToken cancellationToken = default) =>
        ((IGroupStatePersistenceRepository)inner).ListPendingGroupStatePublishesAsync(limit, cancellationToken);

    public Task AcknowledgeGroupStatePublishAsync(
        string operationId,
        CancellationToken cancellationToken = default) =>
        ((IGroupStatePersistenceRepository)inner).AcknowledgeGroupStatePublishAsync(operationId, cancellationToken);

    public Task<IReadOnlyDictionary<ConversationId, ConversationListSummary>> GetConversationSummariesAsync(
        IReadOnlyCollection<ConversationId> conversationIds,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        ((IConversationListSummaryRepository)inner).GetConversationSummariesAsync(conversationIds, now, cancellationToken);

    public Task<ConversationListOpenSnapshot> OpenConversationListAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        ((IConversationListOpenRepository)inner).OpenConversationListAsync(now, cancellationToken);

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

    public Task<GroupConversationOpenSnapshot?> OpenGroupConversationAsync(
        ConversationId groupId,
        int messageLimit,
        DateTimeOffset now,
        CancellationToken cancellationToken = default,
        bool markAsRead = true) =>
        ((IGroupConversationOpenRepository)inner).OpenGroupConversationAsync(
            groupId,
            messageLimit,
            now,
            cancellationToken,
            markAsRead);

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

    public Task<AtomicBoundedSettingReadOutcome> ReadAtomicBoundedSettingAsync(
        string key,
        int maximumValueUtf8Bytes,
        CancellationToken cancellationToken = default) =>
        inner.ReadAtomicBoundedSettingAsync(key, maximumValueUtf8Bytes, cancellationToken);

    public Task<AtomicBoundedSettingMutationResult> CreateAtomicBoundedSettingAsync(
        string key,
        ReadOnlyMemory<byte> utf8Json,
        int maximumValueUtf8Bytes,
        CancellationToken cancellationToken = default) =>
        inner.CreateAtomicBoundedSettingAsync(
            key,
            utf8Json,
            maximumValueUtf8Bytes,
            cancellationToken);

    public Task<AtomicBoundedSettingMutationResult> ReplaceAtomicBoundedSettingAsync(
        string key,
        AtomicBoundedSettingRevision expectedRevision,
        ReadOnlyMemory<byte> utf8Json,
        int maximumValueUtf8Bytes,
        CancellationToken cancellationToken = default) =>
        inner.ReplaceAtomicBoundedSettingAsync(
            key,
            expectedRevision,
            utf8Json,
            maximumValueUtf8Bytes,
            cancellationToken);

    public Task<AtomicBoundedSettingMutationResult> DeleteAtomicBoundedSettingAsync(
        string key,
        AtomicBoundedSettingRevision expectedRevision,
        int maximumValueUtf8Bytes,
        CancellationToken cancellationToken = default) =>
        inner.DeleteAtomicBoundedSettingAsync(
            key,
            expectedRevision,
            maximumValueUtf8Bytes,
            cancellationToken);

    public Task<TransportOutboxCommitResult> PrepareTransportOutboxAsync(
        TransportOutboxPreparedItem item,
        CancellationToken cancellationToken = default) =>
        RequireTransportOutboxRepository().PrepareTransportOutboxAsync(item, cancellationToken);

    public Task<TransportOutboxReadSnapshot> ReadTransportOutboxAsync(
        OutboxAccountScope accountScope,
        OutboxLogicalId logicalId,
        CancellationToken cancellationToken = default) =>
        RequireTransportOutboxRepository().ReadTransportOutboxAsync(
            accountScope,
            logicalId,
            cancellationToken);

    public Task<TransportOutboxCommitResult> ApplyTransportOutboxTransitionAsync(
        OutboxAccountScope accountScope,
        TransportOutboxTransition transition,
        CancellationToken cancellationToken = default) =>
        RequireTransportOutboxRepository().ApplyTransportOutboxTransitionAsync(
            accountScope,
            transition,
            cancellationToken);

    public Task<IReadOnlyList<TransportOutboxItemSnapshot>> ListReadyTransportOutboxAsync(
        OutboxAccountScope accountScope,
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken = default) =>
        RequireTransportOutboxRepository().ListReadyTransportOutboxAsync(
            accountScope,
            now,
            limit,
            cancellationToken);

    public Task<int> ExpireDueTransportOutboxAsync(
        OutboxAccountScope accountScope,
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken = default) =>
        RequireTransportOutboxRepository().ExpireDueTransportOutboxAsync(
            accountScope,
            now,
            limit,
            cancellationToken);

    public Task PurgeTransportOutboxScopeAsync(
        OutboxAccountScope accountScope,
        CancellationToken cancellationToken = default) =>
        RequireTransportOutboxRepository().PurgeTransportOutboxScopeAsync(
            accountScope,
            cancellationToken);

    private ITransportOutboxRepository RequireTransportOutboxRepository() =>
        inner as ITransportOutboxRepository
        ?? throw new InvalidOperationException(
            "The secured session store requires transport outbox persistence support.");

    public Task<MembershipTrustReadSnapshot> ReadMembershipTrustAsync(
        string opaqueProfileKey,
        MembershipTrustDomain domain,
        CancellationToken cancellationToken = default) =>
        RequireMembershipTrustRepository().ReadMembershipTrustAsync(
            opaqueProfileKey,
            domain,
            cancellationToken);

    public Task<MembershipTrustCommitResult> CommitMembershipTrustAsync(
        MembershipTrustRecord record,
        ulong? expectedHeadRevision,
        CancellationToken cancellationToken = default) =>
        RequireMembershipTrustRepository().CommitMembershipTrustAsync(
            record,
            expectedHeadRevision,
            cancellationToken);

    public Task<MembershipTrustClockReadSnapshot> ReadMembershipTrustClockAsync(
        string opaqueProfileKey,
        CancellationToken cancellationToken = default) =>
        RequireMembershipTrustRepository().ReadMembershipTrustClockAsync(
            opaqueProfileKey,
            cancellationToken);

    public Task<MembershipTrustClockCommitResult> CommitMembershipTrustClockAsync(
        MembershipTrustClockRecord record,
        ulong? expectedRevision,
        CancellationToken cancellationToken = default) =>
        RequireMembershipTrustRepository().CommitMembershipTrustClockAsync(
            record,
            expectedRevision,
            cancellationToken);

    private IMembershipTrustRepository RequireMembershipTrustRepository() =>
        inner as IMembershipTrustRepository
        ?? throw new InvalidOperationException(
            "The secured session store requires membership trust persistence support.");

    private static bool IsRecoveryPhraseKey(string key) =>
        string.Equals(key, SessionAccountService.ActiveRecoveryPhraseKey, StringComparison.Ordinal);

    public void Dispose()
    {
        try
        {
            if (inner is IDisposable disposableInner)
            {
                disposableInner.Dispose();
            }
        }
        finally
        {
            ownedLifetime?.Dispose();
        }
    }
}
