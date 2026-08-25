using Deep.Client.Shared.Domain;

namespace Deep.Client.Maui.Core.ViewModels;

internal static class MessageDeliveryProgress
{
    internal static bool IsRegression(
        MessageDeliveryState current,
        MessageDeliveryState candidate)
    {
        var currentRank = SuccessfulRank(current);
        return currentRank > 0 && SuccessfulRank(candidate) < currentRank;
    }

    private static int SuccessfulRank(MessageDeliveryState state) => state switch
    {
        MessageDeliveryState.Sent => 1,
        MessageDeliveryState.Delivered => 2,
        MessageDeliveryState.Read => 3,
        _ => 0
    };

    internal static IReadOnlyList<T> MergeSnapshot<T>(
        IReadOnlyList<T> current,
        IReadOnlyList<T> candidate,
        IReadOnlySet<MessageId> locallyQueuedMessageIds,
        Func<T, MessageId> id,
        Func<T, MessageDeliveryState> state,
        Func<T, DateTimeOffset> createdAt)
    {
        if (current.Count == 0)
        {
            return candidate;
        }

        var currentById = current.ToDictionary(id);
        var candidateIds = new HashSet<MessageId>();
        List<T>? merged = null;
        for (var index = 0; index < candidate.Count; index++)
        {
            var item = candidate[index];
            var itemId = id(item);
            candidateIds.Add(itemId);
            if (!currentById.TryGetValue(itemId, out var existing)
                || !IsRegression(state(existing), state(item)))
            {
                continue;
            }

            merged ??= candidate.ToList();
            merged[index] = existing;
        }

        foreach (var existing in current)
        {
            var existingId = id(existing);
            if (!locallyQueuedMessageIds.Contains(existingId) || candidateIds.Contains(existingId))
            {
                continue;
            }

            merged ??= candidate.ToList();
            merged.Add(existing);
        }

        if (merged is null)
        {
            return candidate;
        }

        merged.Sort((left, right) =>
        {
            var createdComparison = createdAt(left).CompareTo(createdAt(right));
            return createdComparison != 0
                ? createdComparison
                : StringComparer.Ordinal.Compare(id(left).Value, id(right).Value);
        });
        return merged;
    }
}
