namespace Deep.Client.Maui.Services;

public static class PushTokenBridge
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Tokens = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<TaskCompletionSource<string>>> Waiters = new(StringComparer.OrdinalIgnoreCase);

    public static void Set(string provider, string token)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        TaskCompletionSource<string>[] waiters;
        var normalizedProvider = provider.Trim();
        var normalizedToken = token.Trim();
        lock (Gate)
        {
            Tokens[normalizedProvider] = normalizedToken;
            waiters = Waiters.Remove(normalizedProvider, out var pending)
                ? pending.ToArray()
                : [];
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetResult(normalizedToken);
        }
    }

    public static string? Take(string provider) => Get(provider);

    public static string? Get(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return null;
        }

        lock (Gate)
        {
            return Tokens.TryGetValue(provider.Trim(), out var token) ? token : null;
        }
    }

    public static async Task<string?> WaitForTokenAsync(
        string provider,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var normalizedProvider = provider.Trim();
        TaskCompletionSource<string> waiter;
        lock (Gate)
        {
            if (Tokens.TryGetValue(normalizedProvider, out var token))
            {
                return token;
            }

            waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!Waiters.TryGetValue(normalizedProvider, out var waiters))
            {
                waiters = [];
                Waiters[normalizedProvider] = waiters;
            }

            waiters.Add(waiter);
        }

        try
        {
            return await waiter.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            lock (Gate)
            {
                if (Waiters.TryGetValue(normalizedProvider, out var waiters))
                {
                    waiters.Remove(waiter);
                    if (waiters.Count == 0)
                    {
                        Waiters.Remove(normalizedProvider);
                    }
                }
            }
        }
    }

    internal static void Clear()
    {
        TaskCompletionSource<string>[] waiters;
        lock (Gate)
        {
            Tokens.Clear();
            waiters = Waiters.Values.SelectMany(static pending => pending).ToArray();
            Waiters.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetCanceled();
        }
    }
}
