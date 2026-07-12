using System.Security.Cryptography;
using System.Text;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;

namespace Deep.Client.Maui.Services;

public sealed class IncomingMessageNotificationCoordinator
{
    private readonly Func<int, CancellationToken, Task<IReadOnlyList<PendingIncomingMessageNotification>>> listPendingAsync;
    private readonly Func<IReadOnlyCollection<MessageId>, CancellationToken, Task> acknowledgeAsync;
    private readonly IActiveConversationTracker activeConversationTracker;
    private readonly Func<string, CancellationToken, Task> presentAsync;
    private readonly Action rearmPendingWork;
    private readonly int batchLimit;

    public IncomingMessageNotificationCoordinator(
        Func<int, CancellationToken, Task<IReadOnlyList<PendingIncomingMessageNotification>>> listPendingAsync,
        Func<IReadOnlyCollection<MessageId>, CancellationToken, Task> acknowledgeAsync,
        IActiveConversationTracker activeConversationTracker,
        Func<string, CancellationToken, Task> presentAsync,
        Action rearmPendingWork,
        int batchLimit = IncomingMessageNotificationLimits.MaxBatchCount)
    {
        this.listPendingAsync = listPendingAsync ?? throw new ArgumentNullException(nameof(listPendingAsync));
        this.acknowledgeAsync = acknowledgeAsync ?? throw new ArgumentNullException(nameof(acknowledgeAsync));
        this.activeConversationTracker = activeConversationTracker
            ?? throw new ArgumentNullException(nameof(activeConversationTracker));
        this.presentAsync = presentAsync ?? throw new ArgumentNullException(nameof(presentAsync));
        this.rearmPendingWork = rearmPendingWork ?? throw new ArgumentNullException(nameof(rearmPendingWork));
        if (batchLimit is <= 0 or > IncomingMessageNotificationLimits.MaxBatchCount)
        {
            throw new ArgumentOutOfRangeException(nameof(batchLimit));
        }

        this.batchLimit = batchLimit;
    }

    public async Task<int> PresentPendingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var pending = await listPendingAsync(batchLimit, cancellationToken).ConfigureAwait(false);
            if (pending.Count == 0)
            {
                return 0;
            }

            var presentedCount = 0;
            var notificationPresented = false;
            while (pending.Count > 0)
            {
                var state = activeConversationTracker.Snapshot;
                var suppressedIds = pending
                    .Where(item => state.ShouldSuppressNotification(item.ConversationId))
                    .Select(static item => item.MessageId)
                    .ToArray();
                var presentationIds = pending
                    .Where(item => !state.ShouldSuppressNotification(item.ConversationId))
                    .Select(static item => item.MessageId)
                    .ToArray();

                if (presentationIds.Length > 0)
                {
                    if (!notificationPresented)
                    {
                        await presentAsync(
                            CreateNotificationBatchId(presentationIds),
                            cancellationToken).ConfigureAwait(false);
                        notificationPresented = true;
                    }

                    await acknowledgeAsync(presentationIds, cancellationToken).ConfigureAwait(false);
                    presentedCount += presentationIds.Length;
                }

                if (pending.Count < batchLimit)
                {
                    break;
                }

                // Active-chat messages stay durable until MarkConversationAsRead removes them.
                // Re-listing a full batch containing those entries would otherwise spin forever.
                if (suppressedIds.Length > 0)
                {
                    break;
                }

                pending = await listPendingAsync(batchLimit, cancellationToken).ConfigureAwait(false);
            }

            return presentedCount;
        }
        catch
        {
            rearmPendingWork();
            throw;
        }
    }

    internal static string CreateNotificationBatchId(IEnumerable<MessageId> messageIds)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        var canonicalIds = string.Join(
            '\n',
            messageIds.Select(static id => id.Value).Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalIds)));
    }
}
