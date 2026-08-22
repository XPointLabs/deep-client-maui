using Deep.Client.Shared.Domain;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Deep.Client.Shared.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

#if ANDROID
using System.Runtime.Versioning;
using Android.Content;
using Android.Provider;
using AndroidApplication = Android.App.Application;
using AndroidEnvironment = Android.OS.Environment;
#endif

namespace Deep.Client.Maui.Services;

public sealed record PreparedAttachmentFile(string FileName, string ContentType, string Path);

public static class AttachmentOpenService
{
    internal const long AttachmentCacheByteQuota = 256L * 1024 * 1024;
    private const int MaxConcurrentAttachmentIo = 3;
    private static readonly TimeSpan AttachmentCacheMaxAge = TimeSpan.FromDays(1);
    private static readonly SemaphoreSlim AttachmentIoConcurrency = new(MaxConcurrentAttachmentIo, MaxConcurrentAttachmentIo);
    private static readonly SemaphoreSlim CacheWriteGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, Lazy<Task<PreparedAttachmentFile>>> InFlightDownloads = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> ActiveTemporaryFiles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CachePurgeGate = new();
    private static readonly object AccountScopeGate = new();
    private const string AccountScopePreferenceKey = "attachments.account-scope.v1";
    private static int cacheCleanupQueued;
    private static int cacheEpoch;
    private static CancellationTokenSource cacheEpochCancellation = new();
    private static CachePurgeOperation? activeCachePurge;
    private static string? accountScopeHash;
    private static bool accountScopeLoaded;

    public static async Task OpenAsync(
        Page page,
        IReadOnlyList<AttachmentMetadata> attachments,
        IAttachmentFileTransport attachmentFiles,
        CancellationToken cancellationToken = default)
    {
        if (attachments.Count == 0)
        {
            return;
        }

        var attachment = attachments[0];

        var cached = TryGetCachedFile(attachment);
        if (cached is null && (!attachmentFiles.IsEnabled || attachment.RemoteUri is null))
        {
            await page.DisplayAlertAsync(
                "Вложение недоступно",
                "Файл не был загружен на сервер вложений и доступен только на устройстве отправителя.",
                "OK");
            return;
        }

        try
        {
            var file = cached
                ?? await DownloadToCacheAsync(attachment, attachmentFiles, cancellationToken).ConfigureAwait(false);
            await Launcher.Default.OpenAsync(new OpenFileRequest(
                file.FileName,
                new ReadOnlyFile(file.Path, file.ContentType)));
        }
        catch (Exception ex)
        {
            await page.DisplayAlertAsync("Не удалось открыть вложение", ex.Message, "OK");
        }
    }

    public static async Task ShareAsync(
        AttachmentMetadata attachment,
        IAttachmentFileTransport attachmentFiles,
        CancellationToken cancellationToken = default)
    {
        var file = await DownloadToCacheAsync(attachment, attachmentFiles, cancellationToken).ConfigureAwait(false);
        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = file.FileName,
            File = new ShareFile(file.Path, file.ContentType)
        }).ConfigureAwait(false);
    }

    public static async Task<string> SaveAsync(
        AttachmentMetadata attachment,
        IAttachmentFileTransport attachmentFiles,
        CancellationToken cancellationToken = default)
    {
        var file = await DownloadToCacheAsync(attachment, attachmentFiles, cancellationToken).ConfigureAwait(false);
        return await SavePreparedFileAsync(file, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<PreparedAttachmentFile> DownloadToCacheAsync(
        AttachmentMetadata attachment,
        IAttachmentFileTransport attachmentFiles,
        CancellationToken cancellationToken = default)
    {
        if (TryGetCachedFile(attachment) is { } cached)
        {
            return cached;
        }

        if (!attachmentFiles.IsEnabled || attachment.RemoteUri is null)
        {
            throw new InvalidOperationException(
                "Файл не был загружен на сервер вложений и доступен только на устройстве отправителя.");
        }

        QueueCacheCleanup();
        var cachePath = CachePathFor(attachment);
        var (expectedCacheEpoch, cacheCancellationToken) = GetCacheEpoch();
        var operation = InFlightDownloads.GetOrAdd(
            cachePath,
            _ => new Lazy<Task<PreparedAttachmentFile>>(
                () => DownloadToCacheCoreAsync(
                    attachment,
                    attachmentFiles,
                    cachePath,
                    expectedCacheEpoch,
                    cacheCancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        Task<PreparedAttachmentFile> sharedTask;
        try
        {
            sharedTask = operation.Value;
        }
        catch
        {
            RemoveInFlightDownload(cachePath, operation);
            throw;
        }

        _ = sharedTask.ContinueWith(
            _ => RemoveInFlightDownload(cachePath, operation),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return await sharedTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public static PreparedAttachmentFile? TryGetCachedFile(AttachmentMetadata attachment)
    {
        var cachePath = CachePathFor(attachment);
        if (!File.Exists(cachePath))
        {
            return null;
        }

        try
        {
            if (new FileInfo(cachePath).Length > AttachmentCacheByteQuota)
            {
                TryDelete(cachePath);
                return null;
            }
        }
        catch
        {
            return null;
        }

        QueueCacheCleanup();
        TouchCacheFile(cachePath);
        return File.Exists(cachePath)
            ? new PreparedAttachmentFile(attachment.FileName, attachment.ContentType, cachePath)
            : null;
    }

    public static async Task<PreparedAttachmentFile> CacheLocalCopyAsync(
        AttachmentMetadata attachment,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Attachment source file was not found.", sourcePath);
        }

        if (new FileInfo(sourcePath).Length > AttachmentCacheByteQuota)
        {
            throw new IOException("Attachment exceeds the preview cache byte quota.");
        }

        QueueCacheCleanup();
        var cachePath = CachePathFor(attachment);
        var (expectedCacheEpoch, _) = GetCacheEpoch();
        if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(cachePath), StringComparison.OrdinalIgnoreCase))
        {
            await AcquireAttachmentIoAsync(cancellationToken).ConfigureAwait(false);
            string? temporaryPath = null;
            try
            {
                temporaryPath = CreateTemporaryCachePath();
                await using (var source = File.OpenRead(sourcePath))
                await using (var destination = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var quotaDestination = new CacheQuotaWriteStream(destination, AttachmentCacheByteQuota);
                    await source.CopyToAsync(quotaDestination, cancellationToken).ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                await CommitTemporaryCacheFileAsync(
                    temporaryPath,
                    cachePath,
                    expectedCacheEpoch,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (temporaryPath is not null)
                {
                    TryDelete(temporaryPath);
                    ActiveTemporaryFiles.TryRemove(temporaryPath, out _);
                }

                AttachmentIoConcurrency.Release();
            }
        }
        else
        {
            TouchCacheFile(cachePath);
        }

        return new PreparedAttachmentFile(attachment.FileName, attachment.ContentType, cachePath);
    }

    public static void SetAccountScope(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var scope = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId.Trim())));
        lock (AccountScopeGate)
        {
            accountScopeHash = scope;
            accountScopeLoaded = true;
            Preferences.Default.Set(AccountScopePreferenceKey, scope);
        }
    }

    internal static void ClearAccountScope()
    {
        lock (AccountScopeGate)
        {
            accountScopeHash = null;
            accountScopeLoaded = true;
            Preferences.Default.Remove(AccountScopePreferenceKey);
        }
    }

    private static string CachePathFor(AttachmentMetadata attachment)
    {
        var material = string.Join(
            '\n',
            "deep.attachment-cache/v2",
            GetAccountScopeHash(),
            attachment.AttachmentId,
            attachment.FileName,
            attachment.ContentType,
            attachment.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            attachment.RemoteUri?.AbsoluteUri ?? string.Empty,
            attachment.DigestBase64 ?? string.Empty,
            attachment.EncryptionKeyBase64 ?? string.Empty);
        var cacheId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
        var extension = Path.GetExtension(SafeFileName(attachment.FileName));
        if (extension.Length > 16 || extension.Any(static character => !char.IsLetterOrDigit(character) && character != '.'))
        {
            extension = string.Empty;
        }

        return Path.Combine(FileSystem.CacheDirectory, $"attachment-{cacheId}{extension.ToLowerInvariant()}");
    }

    private static async Task<PreparedAttachmentFile> DownloadToCacheCoreAsync(
        AttachmentMetadata attachment,
        IAttachmentFileTransport attachmentFiles,
        string cachePath,
        int expectedCacheEpoch,
        CancellationToken cacheCancellationToken)
    {
        cacheCancellationToken.ThrowIfCancellationRequested();
        await AcquireAttachmentIoAsync(cacheCancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            temporaryPath = CreateTemporaryCachePath();
            AttachmentFileDownloadInfo downloaded;
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var quotaOutput = new CacheQuotaWriteStream(output, AttachmentCacheByteQuota);
                downloaded = await attachmentFiles.DownloadToAsync(attachment, quotaOutput, cacheCancellationToken)
                    .ConfigureAwait(false);
                await output.FlushAsync(cacheCancellationToken).ConfigureAwait(false);
            }

            await CommitTemporaryCacheFileAsync(
                temporaryPath,
                cachePath,
                expectedCacheEpoch,
                cacheCancellationToken).ConfigureAwait(false);

            return new PreparedAttachmentFile(downloaded.FileName, downloaded.ContentType, cachePath);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                TryDelete(temporaryPath);
                ActiveTemporaryFiles.TryRemove(temporaryPath, out _);
            }

            AttachmentIoConcurrency.Release();
        }
    }

    private static string GetAccountScopeHash()
    {
        lock (AccountScopeGate)
        {
            if (!accountScopeLoaded)
            {
                var stored = Preferences.Default.Get(AccountScopePreferenceKey, string.Empty);
                accountScopeHash = stored.Length == 64 &&
                                   stored.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
                    ? stored
                    : null;
                accountScopeLoaded = true;
            }

            return accountScopeHash ?? "unscoped";
        }
    }

    private static string CreateTemporaryCachePath()
    {
        Directory.CreateDirectory(FileSystem.CacheDirectory);
        while (true)
        {
            var path = Path.Combine(FileSystem.CacheDirectory, $".attachment-{Guid.NewGuid():N}.tmp");
            if (ActiveTemporaryFiles.TryAdd(path, 0))
            {
                return path;
            }
        }
    }

    private static async Task CommitTemporaryCacheFileAsync(
        string temporaryPath,
        string cachePath,
        int expectedCacheEpoch,
        CancellationToken cancellationToken)
    {
        var incomingBytes = new FileInfo(temporaryPath).Length;
        if (incomingBytes > AttachmentCacheByteQuota)
        {
            throw new IOException("Attachment exceeds the preview cache byte quota.");
        }

        await CacheWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfCachePurged(expectedCacheEpoch);
            CleanupOrphanTemporaryFilesLocked();
            EnsureCapacityForWriteLocked(cachePath, incomingBytes, AttachmentCacheByteQuota);
            File.Move(temporaryPath, cachePath, overwrite: true);
            TouchCacheFile(cachePath);
        }
        finally
        {
            CacheWriteGate.Release();
        }
    }

    private static void EnsureCapacityForWriteLocked(string cachePath, long incomingBytes, long byteQuota)
    {
        if (incomingBytes > byteQuota)
        {
            throw new IOException("Attachment exceeds the preview cache byte quota.");
        }

        var destination = Path.GetFullPath(cachePath);
        var entries = GetCacheEntries()
            .Where(entry => !string.Equals(Path.GetFullPath(entry.Path), destination, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static entry => entry.LastAccessTimeUtc)
            .ThenBy(static entry => entry.LastWriteTimeUtc)
            .ThenBy(static entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
        var existingBytes = SaturatingSum(entries.Select(static entry => entry.Length));
        foreach (var entry in entries)
        {
            if (existingBytes <= byteQuota - incomingBytes)
            {
                break;
            }

            if (TryDelete(entry.Path))
            {
                existingBytes = Math.Max(0, existingBytes - entry.Length);
            }
        }

        if (existingBytes > byteQuota - incomingBytes)
        {
            throw new IOException("Attachment preview cache quota could not be reserved.");
        }
    }

    private static long TrimFinalCacheLocked(long byteQuota)
    {
        var entries = GetCacheEntries()
            .OrderBy(static entry => entry.LastAccessTimeUtc)
            .ThenBy(static entry => entry.LastWriteTimeUtc)
            .ThenBy(static entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
        var totalBytes = SaturatingSum(entries.Select(static entry => entry.Length));
        foreach (var entry in entries)
        {
            if (totalBytes <= byteQuota)
            {
                break;
            }

            if (TryDelete(entry.Path))
            {
                totalBytes = Math.Max(0, totalBytes - entry.Length);
            }
        }

        return totalBytes;
    }

    private static IReadOnlyList<CacheFileEntry> GetCacheEntries()
    {
        if (!Directory.Exists(FileSystem.CacheDirectory))
        {
            return [];
        }

        string[] paths;
        try
        {
            paths = Directory.EnumerateFiles(FileSystem.CacheDirectory, "attachment-*").ToArray();
        }
        catch
        {
            return [];
        }

        var entries = new List<CacheFileEntry>(paths.Length);
        foreach (var path in paths)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists)
                {
                    entries.Add(new CacheFileEntry(
                        path,
                        info.Length,
                        info.LastAccessTimeUtc,
                        info.LastWriteTimeUtc));
                }
            }
            catch
            {
            }
        }

        return entries;
    }

    private static long SaturatingSum(IEnumerable<long> values)
    {
        var total = 0L;
        foreach (var value in values)
        {
            total = value > long.MaxValue - total ? long.MaxValue : total + value;
        }

        return total;
    }

    private static void CleanupOrphanTemporaryFilesLocked()
    {
        if (!Directory.Exists(FileSystem.CacheDirectory))
        {
            return;
        }

        string[] paths;
        try
        {
            paths = Directory.EnumerateFiles(FileSystem.CacheDirectory, ".attachment-*.tmp").ToArray();
        }
        catch
        {
            return;
        }

        foreach (var path in paths)
        {
            if (!ActiveTemporaryFiles.ContainsKey(path))
            {
                TryDelete(path);
            }
        }
    }

    private static void TouchCacheFile(string path)
    {
        try
        {
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
        }
        catch
        {
        }
    }

    private static void RemoveInFlightDownload(
        string cachePath,
        Lazy<Task<PreparedAttachmentFile>> operation)
    {
        ((ICollection<KeyValuePair<string, Lazy<Task<PreparedAttachmentFile>>>>)InFlightDownloads)
            .Remove(new KeyValuePair<string, Lazy<Task<PreparedAttachmentFile>>>(cachePath, operation));
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return !File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private sealed record CacheFileEntry(
        string Path,
        long Length,
        DateTime LastAccessTimeUtc,
        DateTime LastWriteTimeUtc);

    private sealed class CacheQuotaWriteStream(Stream inner, long byteQuota) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set
            {
                EnsurePosition(value);
                inner.Position = value;
            }
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
        {
            var position = inner.Seek(offset, origin);
            EnsurePosition(position);
            return position;
        }

        public override void SetLength(long value)
        {
            EnsurePosition(value);
            inner.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureWrite(count);
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureWrite(buffer.Length);
            inner.Write(buffer);
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            EnsureWrite(count);
            return inner.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            EnsureWrite(buffer.Length);
            return inner.WriteAsync(buffer, cancellationToken);
        }

        public override void WriteByte(byte value)
        {
            EnsureWrite(1);
            inner.WriteByte(value);
        }

        private void EnsureWrite(int count)
        {
            var finalPosition = checked(inner.Position + count);
            if (Math.Max(inner.Length, finalPosition) > byteQuota)
            {
                throw new IOException("Attachment exceeds the preview cache byte quota.");
            }
        }

        private void EnsurePosition(long position)
        {
            if (position < 0 || position > byteQuota)
            {
                throw new IOException("Attachment exceeds the preview cache byte quota.");
            }
        }
    }

    private static async Task<string> SavePreparedFileAsync(
        PreparedAttachmentFile file,
        CancellationToken cancellationToken)
    {
#if ANDROID
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            return await SavePreparedFileToMediaStoreAsync(file, cancellationToken).ConfigureAwait(false);
        }

        var publicFolder = file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? AndroidEnvironment.DirectoryPictures
            : AndroidEnvironment.DirectoryDownloads;
        var publicDirectory = AndroidEnvironment.GetExternalStoragePublicDirectory(publicFolder)?.AbsolutePath
            ?? AppDataPath.Resolve();
        var destinationDirectory = Path.Combine(publicDirectory, "Deep");
        Directory.CreateDirectory(destinationDirectory);
        var destination = UniqueFilePath(destinationDirectory, file.FileName);
        await CopyFileAsync(file.Path, destination, cancellationToken).ConfigureAwait(false);
        return destination;
#elif WINDOWS
        var destinationDirectory = await ResolveWindowsDownloadsDirectoryAsync(cancellationToken)
            .ConfigureAwait(false);
        var destination = await destinationDirectory
            .CreateFileAsync(file.FileName, Windows.Storage.CreationCollisionOption.GenerateUniqueName)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        await using (var source = File.OpenRead(file.Path))
        await using (var output = await destination.OpenStreamForWriteAsync().ConfigureAwait(false))
        {
            output.SetLength(0);
            await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return destination.Path;
#else
        var downloads = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "Downloads");
        if (string.IsNullOrWhiteSpace(downloads) || !Directory.Exists(downloads))
        {
            downloads = AppDataPath.Resolve();
        }

        var destinationDirectory = Path.Combine(downloads, "Deep");
        Directory.CreateDirectory(destinationDirectory);
        var destination = UniqueFilePath(destinationDirectory, file.FileName);
        await CopyFileAsync(file.Path, destination, cancellationToken).ConfigureAwait(false);
        return destination;
#endif
    }

#if WINDOWS
    private static async Task<Windows.Storage.StorageFolder> ResolveWindowsDownloadsDirectoryAsync(
        CancellationToken cancellationToken)
    {
        const string folderName = "Deep";
        var downloadsPath = Windows.Storage.UserDataPaths.GetDefault().Downloads;
        if (string.IsNullOrWhiteSpace(downloadsPath))
        {
            throw new IOException("The Windows downloads directory is unavailable.");
        }
        var downloads = await Windows.Storage.StorageFolder
            .GetFolderFromPathAsync(downloadsPath)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        var existing = await downloads
            .TryGetItemAsync(folderName)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        if (existing is Windows.Storage.StorageFolder existingFolder)
        {
            return existingFolder;
        }
        if (existing is not null)
        {
            throw new IOException("The Deep downloads destination is not a directory.");
        }

        try
        {
            return await downloads
                .CreateFolderAsync(folderName, Windows.Storage.CreationCollisionOption.FailIfExists)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception creationFailure) when (!cancellationToken.IsCancellationRequested)
        {
            // A concurrent save may have created the exact folder after our read.
            // Accept only that exact directory; do not generate an alternate name.
            var raced = await downloads
                .TryGetItemAsync(folderName)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            return raced as Windows.Storage.StorageFolder
                ?? throw new IOException(
                    "The Deep downloads directory could not be created or reopened.",
                    creationFailure);
        }
    }
#endif

#if ANDROID
    [SupportedOSPlatform("android29.0")]
    private static async Task<string> SavePreparedFileToMediaStoreAsync(
        PreparedAttachmentFile file,
        CancellationToken cancellationToken)
    {
        var resolver = AndroidApplication.Context.ContentResolver;
        if (resolver is null)
        {
            throw new InvalidOperationException("Android content resolver is not available.");
        }

        var contentType = string.IsNullOrWhiteSpace(file.ContentType)
            ? "application/octet-stream"
            : file.ContentType;
        var isImage = contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        var isVideo = contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
        var collection = isImage
            ? MediaStore.Images.Media.ExternalContentUri
            : isVideo
                ? MediaStore.Video.Media.ExternalContentUri
                : MediaStore.Downloads.ExternalContentUri;
        if (collection is null)
        {
            throw new InvalidOperationException("Android MediaStore collection is not available.");
        }

        var relativePath = isImage
            ? $"{AndroidEnvironment.DirectoryPictures}/Deep"
            : isVideo
                ? $"{AndroidEnvironment.DirectoryMovies}/Deep"
                : $"{AndroidEnvironment.DirectoryDownloads}/Deep";

        using var values = new ContentValues();
        values.Put(MediaStore.IMediaColumns.DisplayName, file.FileName);
        values.Put(MediaStore.IMediaColumns.MimeType, contentType);
        values.Put(MediaStore.IMediaColumns.RelativePath, relativePath);
        values.Put(MediaStore.IMediaColumns.IsPending, 1);

        var uri = resolver.Insert(collection, values)
            ?? throw new InvalidOperationException("Android MediaStore did not create an output URI.");
        try
        {
            await using (var source = File.OpenRead(file.Path))
            await using (var destination = resolver.OpenOutputStream(uri)
                ?? throw new InvalidOperationException("Android MediaStore did not open an output stream."))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            values.Clear();
            values.Put(MediaStore.IMediaColumns.IsPending, 0);
            resolver.Update(uri, values, null, null);
            return uri.ToString() ?? file.FileName;
        }
        catch
        {
            resolver.Delete(uri, null, null);
            throw;
        }
    }
#endif

    private static async Task CopyFileAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        await using var sourceStream = File.OpenRead(source);
        await using var destinationStream = File.Create(destination);
        await sourceStream.CopyToAsync(destinationStream, cancellationToken).ConfigureAwait(false);
        await destinationStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string UniqueFilePath(string directory, string fileName)
    {
        var safeName = SafeFileName(fileName);
        var candidate = Path.Combine(directory, safeName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var name = Path.GetFileNameWithoutExtension(safeName);
        var extension = Path.GetExtension(safeName);
        for (var index = 2; index < 10_000; index++)
        {
            candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{name}-{Guid.NewGuid():N}{extension}");
    }

    private static string SafeFileName(string fileName)
    {
        var safe = string.IsNullOrWhiteSpace(fileName)
            ? "attachment"
            : fileName;

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            safe = safe.Replace(invalid, '_');
        }

        return safe;
    }

    private static async Task AcquireAttachmentIoAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task? purgeTask;
            lock (CachePurgeGate)
            {
                purgeTask = activeCachePurge?.Completion.Task;
            }

            if (purgeTask is not null)
            {
                await purgeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            await AttachmentIoConcurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (CachePurgeGate)
            {
                if (activeCachePurge is null)
                {
                    return;
                }
            }

            AttachmentIoConcurrency.Release();
        }
    }

    private static void QueueCacheCleanup()
    {
        if (Interlocked.Exchange(ref cacheCleanupQueued, 1) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await CacheWriteGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    CleanupOldAttachmentCacheLocked();
                }
                finally
                {
                    CacheWriteGate.Release();
                }
            }
            catch
            {
                // Cache cleanup is opportunistic.
            }
            finally
            {
                Volatile.Write(ref cacheCleanupQueued, 0);
            }
        });
    }

    private static void CleanupOldAttachmentCacheLocked()
    {
        CleanupOrphanTemporaryFilesLocked();
        var cutoff = DateTime.UtcNow - AttachmentCacheMaxAge;
        foreach (var entry in GetCacheEntries())
        {
            if (entry.LastAccessTimeUtc < cutoff && entry.LastWriteTimeUtc < cutoff)
            {
                TryDelete(entry.Path);
            }
        }

        _ = TrimFinalCacheLocked(AttachmentCacheByteQuota);
    }

    internal static async Task<long> EnforceCacheQuotaAsync(
        long byteQuota,
        CancellationToken cancellationToken = default)
    {
        if (byteQuota < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteQuota));
        }

        await CacheWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CleanupOrphanTemporaryFilesLocked();
            return TrimFinalCacheLocked(byteQuota);
        }
        finally
        {
            CacheWriteGate.Release();
        }
    }

    internal static Task PurgeCacheAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        CachePurgeOperation operation;
        var runOperation = false;
        lock (CachePurgeGate)
        {
            if (activeCachePurge is null)
            {
                Interlocked.Increment(ref cacheEpoch);
                var previousCancellation = cacheEpochCancellation;
                cacheEpochCancellation = new CancellationTokenSource();
                InFlightDownloads.Clear();
                activeCachePurge = new CachePurgeOperation(
                    previousCancellation,
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                runOperation = true;
            }

            operation = activeCachePurge;
        }

        if (runOperation)
        {
            _ = RunCachePurgeAsync(operation);
        }

        return cancellationToken.CanBeCanceled
            ? operation.Completion.Task.WaitAsync(cancellationToken)
            : operation.Completion.Task;
    }

    private static async Task RunCachePurgeAsync(CachePurgeOperation operation)
    {
        var acquiredPermits = 0;
        Exception? error = null;
        try
        {
            operation.PreviousCancellation.Cancel();
            for (; acquiredPermits < MaxConcurrentAttachmentIo; acquiredPermits++)
            {
                await AttachmentIoConcurrency.WaitAsync().ConfigureAwait(false);
            }

            await CacheWriteGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Directory.Exists(FileSystem.CacheDirectory))
                {
                    string[] paths;
                    try
                    {
                        paths = Directory.EnumerateFiles(FileSystem.CacheDirectory).ToArray();
                    }
                    catch
                    {
                        paths = [];
                    }

                    foreach (var path in paths.Where(static path =>
                             {
                                 var name = Path.GetFileName(path);
                                 return name.StartsWith("attachment-", StringComparison.Ordinal)
                                     || name.StartsWith(".attachment-", StringComparison.Ordinal)
                                     || name.StartsWith("media-", StringComparison.Ordinal);
                             }))
                    {
                        TryDelete(path);
                    }
                }

                ActiveTemporaryFiles.Clear();
            }
            finally
            {
                CacheWriteGate.Release();
            }
        }
        catch (Exception exception)
        {
            error = exception;
        }
        finally
        {
            if (acquiredPermits != 0)
            {
                AttachmentIoConcurrency.Release(acquiredPermits);
            }

            operation.PreviousCancellation.Dispose();
            lock (CachePurgeGate)
            {
                if (ReferenceEquals(activeCachePurge, operation))
                {
                    activeCachePurge = null;
                }
            }

            if (error is null)
            {
                operation.Completion.TrySetResult();
            }
            else
            {
                operation.Completion.TrySetException(error);
            }
        }
    }

    private static (int Epoch, CancellationToken CancellationToken) GetCacheEpoch()
    {
        lock (CachePurgeGate)
        {
            return (cacheEpoch, cacheEpochCancellation.Token);
        }
    }

    private static void ThrowIfCachePurged(int expectedCacheEpoch)
    {
        if (expectedCacheEpoch != Volatile.Read(ref cacheEpoch))
        {
            throw new OperationCanceledException("Attachment cache was cleared during logout.");
        }
    }

    private sealed record CachePurgeOperation(
        CancellationTokenSource PreviousCancellation,
        TaskCompletionSource Completion);
}
