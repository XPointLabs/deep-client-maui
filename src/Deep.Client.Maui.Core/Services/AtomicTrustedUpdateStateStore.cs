using System.Text.Json;

namespace Deep.Client.Maui.Core.Services;

public sealed class AtomicTrustedUpdateStateStore : ITrustedUpdateStateStore
{
    private readonly string statePath;

    public AtomicTrustedUpdateStateStore(string statePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        this.statePath = Path.GetFullPath(statePath);
    }

    public async Task<TrustedUpdateState?> LoadAsync(CancellationToken cancellationToken)
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
}
