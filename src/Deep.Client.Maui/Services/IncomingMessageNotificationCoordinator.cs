using System.Security.Cryptography;
using System.Text;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;

namespace Deep.Client.Maui.Services;

public sealed class IncomingMessageNotificationCoordinator
{
    private readonly Func<int, CancellationToken, Task<IReadOnlyList<MessageId>>> listPendingAsync;
    private readonly Func<IReadOnlyCollection<MessageId>, CancellationToken, Task> markPresentedAsync;
    private readonly Func<string, CancellationToken, Task> presentAsync;
    private readonly Action rearmPendingWork;
    private readonly int batchLimit;

    public IncomingMessageNotificationCoordinator(
        Func<int, CancellationToken, Task<IReadOnlyList<MessageId>>> listPendingAsync,
        Func<IReadOnlyCollection<MessageId>, CancellationToken, Task> markPresentedAsync,
        Func<string, CancellationToken, Task> presentAsync,
        Action rearmPendingWork,
        int batchLimit = IncomingMessageNotificationLimits.MaxBatchCount)
    {
        this.listPendingAsync = listPendingAsync ?? throw new ArgumentNullException(nameof(listPendingAsync));
        this.markPresentedAsync = markPresentedAsync ?? throw new ArgumentNullException(nameof(markPresentedAsync));
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
            var messageIds = await listPendingAsync(batchLimit, cancellationToken).ConfigureAwait(false);
            if (messageIds.Count == 0)
            {
                return 0;
            }

            await presentAsync(CreateNotificationBatchId(messageIds), cancellationToken).ConfigureAwait(false);
            var presentedCount = 0;
            while (messageIds.Count > 0)
            {
                await markPresentedAsync(messageIds, cancellationToken).ConfigureAwait(false);
                presentedCount += messageIds.Count;
                if (messageIds.Count < batchLimit)
                {
                    break;
                }

                messageIds = await listPendingAsync(batchLimit, cancellationToken).ConfigureAwait(false);
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
