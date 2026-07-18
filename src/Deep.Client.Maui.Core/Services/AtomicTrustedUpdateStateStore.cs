using System.Collections.Concurrent;
using System.Text.Json;

namespace Deep.Client.Maui.Core.Services;

public sealed class AtomicTrustedUpdateStateStore : ITrustedUpdateStateStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathGates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string statePath;
    private readonly SemaphoreSlim pathGate;

    public AtomicTrustedUpdateStateStore(string statePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        this.statePath = Path.GetFullPath(statePath);
        pathGate = PathGates.GetOrAdd(this.statePath, _ => new SemaphoreSlim(1, 1));
    }

    public async Task<TrustedUpdateState?> LoadAsync(CancellationToken cancellationToken)
    {
        await pathGate.WaitAsync(cancellationToken);
        try
        {
            return await LoadUnsafeAsync(cancellationToken);
        }
        finally
        {
            pathGate.Release();
        }
    }

    private async Task<TrustedUpdateState?> LoadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(statePath))
        {
            return null;
        }

        await using var stream = new FileStream(
            statePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        TrustedUpdateState? state;
        try
        {
            state = await JsonSerializer.DeserializeAsync<TrustedUpdateState>(
                stream,
                cancellationToken: cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Persisted update-trust state is corrupt.",
                exception);
        }
        PortableUpdateMetadataVerifier.ValidateTrustedState(state);
        return state;
    }

    public async Task SaveAsync(TrustedUpdateState state, CancellationToken cancellationToken)
    {
        PortableUpdateMetadataVerifier.ValidateTrustedState(state);
        await pathGate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadUnsafeAsync(cancellationToken);
            if (current is not null)
            {
                RequireMonotonicAdvance(current, state);
            }

            var directory = Path.GetDirectoryName(statePath)
                ?? throw new InvalidOperationException("Trusted update state path has no directory.");
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(
                directory,
                $".{Path.GetFileName(statePath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

            try
            {
                await using (var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        state,
                        cancellationToken: cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporary, statePath, overwrite: true);
            }
            finally
            {
                try
                {
                    File.Delete(temporary);
                }
                catch
                {
                    // Successful replace removes the temporary file.
                }
            }
        }
        finally
        {
            pathGate.Release();
        }
    }

    private static void RequireMonotonicAdvance(
        TrustedUpdateState current,
        TrustedUpdateState candidate)
    {
        var rootAdvanced =
            current.TrustedRoot.Version < int.MaxValue &&
            candidate.TrustedRoot.Version == current.TrustedRoot.Version + 1;
        var rootUnchanged = candidate.TrustedRoot == current.TrustedRoot;
        var versionsAdvance =
            candidate.Versions.Timestamp >= current.Versions.Timestamp &&
            candidate.Versions.Snapshot >= current.Versions.Snapshot &&
            candidate.Versions.Targets >= current.Versions.Targets &&
            candidate.Versions.AndroidRelease >= current.Versions.AndroidRelease;
        if ((!rootAdvanced && !rootUnchanged) || !versionsAdvance)
        {
            throw new InvalidDataException(
                "Trusted update state rollback or conflicting root write was rejected.");
        }
    }
}
