using System.Buffers;

namespace Deep.Client.Maui.Services;

internal static class PrelaunchPlaintextStateArtifactPurger
{
    private const int WipeBufferSize = 64 * 1024;
    private const string StateFileName = "client-state.json";

    public static async Task PurgeAsync(
        string appDataDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);

        var root = Path.GetFullPath(appDataDirectory);
        if (!Directory.Exists(root))
        {
            return;
        }

        var primaryPath = Path.Combine(root, StateFileName);
        var artifacts = new List<string>
        {
            primaryPath,
            primaryPath + ".migrated.bak"
        };
        artifacts.AddRange(Directory.EnumerateFiles(
            root,
            StateFileName + ".*.tmp",
            SearchOption.TopDirectoryOnly));

        var failures = new List<Exception>();
        foreach (var artifactPath in artifacts.Distinct(PathComparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await OverwriteAndDeleteAsync(artifactPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures.Add(new IOException(
                    $"Unable to remove a known pre-launch plaintext state artifact '{artifactPath}'.",
                    exception));
            }
        }

        if (failures.Count != 0)
        {
            throw new AggregateException(
                "One or more known pre-launch plaintext state artifacts could not be removed.",
                failures);
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static async Task OverwriteAndDeleteAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return;
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(WipeBufferSize);
            try
            {
                Array.Clear(buffer);
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.None,
                    WipeBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var remaining = stream.Length;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(remaining, buffer.Length);
                    await stream.WriteAsync(
                            buffer.AsMemory(0, count),
                            cancellationToken)
                        .ConfigureAwait(false);
                    remaining -= count;
                }

                stream.SetLength(0);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            finally
            {
                Array.Clear(buffer);
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        File.Delete(path);
    }
}
