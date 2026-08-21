using System.Collections.Concurrent;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Services;

/// <summary>App-private, compare/exchange PMA/PMR/PMT LKG. No authority bytes or secrets live here.</summary>
internal sealed class ProtectedProductionMailboxTrustStateStore :
    IProductionMailboxTrustStateStore
{
    internal const string StateFileName = "control-plane-lkg.pml1";
    private const string LockFileName = "control-plane-lkg.lock";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
    private readonly string root;
    private readonly string statePath;
    private readonly string lockPath;
    private readonly SemaphoreSlim gate;

    internal static ProtectedProductionMailboxTrustStateStore OpenOrCreate(
        string protectedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedRoot);
        var fullRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(protectedRoot));
        if (!Directory.Exists(fullRoot))
        {
            Directory.CreateDirectory(fullRoot);
            if (OperatingSystem.IsWindows())
                WindowsMailboxAccessControl.ProtectNewDirectory(fullRoot);
        }
        return new ProtectedProductionMailboxTrustStateStore(fullRoot);
    }

    public ProtectedProductionMailboxTrustStateStore(string protectedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedRoot);
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectedRoot));
        if (!Directory.Exists(root) || IsReparse(root))
            throw new InvalidDataException("Production mailbox protected root is invalid.");
        if (OperatingSystem.IsWindows()) WindowsMailboxAccessControl.ValidateTree(root);
        statePath = Child(StateFileName);
        lockPath = Child(LockFileName);
        gate = Gates.GetOrAdd(root, static _ => new SemaphoreSlim(1, 1));
    }

    public async Task<ProductionMailboxTrustState?> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLock = OpenProcessLock();
            return ReadCurrent();
        }
        finally { gate.Release(); }
    }

    public async Task CommitAsync(
        ulong expectedRevision,
        ProductionMailboxTrustState replacement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var encoded = ProductionMailboxTrustStateCodec.Encode(replacement);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            using var processLock = OpenProcessLock();
            var current = ReadCurrent();
            if ((current?.Revision ?? 0) != expectedRevision ||
                replacement.Revision != checked(expectedRevision + 1))
                throw new InvalidOperationException("Production mailbox LKG changed concurrently.");
            cancellationToken.ThrowIfCancellationRequested();
            temporary = Child($".{StateFileName}.{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            ProtectFile(temporary);
            if (File.Exists(statePath))
                File.Move(temporary, statePath, overwrite: true);
            else
                File.Move(temporary, statePath);
            temporary = null;
            ProtectFile(statePath);
            if (OperatingSystem.IsWindows()) WindowsMailboxAccessControl.ValidateTree(root);
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
            gate.Release();
        }
    }

    private ProductionMailboxTrustState? ReadCurrent()
    {
        if (!File.Exists(statePath)) return null;
        if (IsReparse(statePath))
            throw new InvalidDataException("Production mailbox LKG path is unsafe.");
        var info = new FileInfo(statePath);
        if (info.Length != ProductionMailboxTrustStateCodec.EncodedLength)
            throw new InvalidDataException("Production mailbox LKG length is invalid.");
        var bytes = new byte[ProductionMailboxTrustStateCodec.EncodedLength];
        using var stream = new FileStream(
            statePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.SequentialScan);
        stream.ReadExactly(bytes);
        return ProductionMailboxTrustStateCodec.Decode(bytes);
    }

    private FileStream OpenProcessLock()
    {
        var stream = new FileStream(
            lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            1, FileOptions.WriteThrough);
        ProtectFile(lockPath);
        return stream;
    }

    private string Child(string name)
    {
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains(Path.DirectorySeparatorChar) ||
            name.Contains(Path.AltDirectorySeparatorChar))
            throw new InvalidDataException("Production mailbox state filename is invalid.");
        var path = Path.GetFullPath(Path.Combine(root, name));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
            throw new InvalidDataException("Production mailbox state escaped its protected root.");
        return path;
    }

    private static bool IsReparse(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void ProtectFile(string path)
    {
        if (OperatingSystem.IsWindows())
            WindowsMailboxAccessControl.ProtectNewFile(path);
        else
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
