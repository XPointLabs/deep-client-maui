using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Services;
using Deep.Client.Maui.Services;
using Microsoft.Maui.Storage;
using System.Security.Cryptography;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class AttachmentOpenServiceTests
{
    public AttachmentOpenServiceTests()
    {
        AttachmentOpenService.SetAccountScope($"account-{Guid.NewGuid():N}");
    }

    [Fact]
    public async Task DownloadToCacheAsync_DeduplicatesConcurrentRequests()
    {
        var attachment = CreateAttachment();
        var transport = new BlockingAttachmentTransport();
        var temporaryFilesBefore = GetTemporaryFiles();

        try
        {
            var first = AttachmentOpenService.DownloadToCacheAsync(attachment, transport);
            await transport.DownloadStarted.Task;
            var second = AttachmentOpenService.DownloadToCacheAsync(attachment, transport);

            Assert.Null(AttachmentOpenService.TryGetCachedFile(attachment));
            transport.ReleaseDownload();

            var files = await Task.WhenAll(first, second);

            Assert.Equal(1, transport.DownloadCalls);
            Assert.Equal(files[0].Path, files[1].Path);
            Assert.Equal("partialcomplete payload", await File.ReadAllTextAsync(files[0].Path));
            Assert.Equal(temporaryFilesBefore, GetTemporaryFiles());
        }
        finally
        {
            await AttachmentOpenService.PurgeCacheAsync();
        }
    }

    [Fact]
    public async Task DownloadToCacheAsync_CancelledConsumerDoesNotCancelSharedDownload()
    {
        var attachment = CreateAttachment();
        var transport = new BlockingAttachmentTransport();
        using var cancelledConsumer = new CancellationTokenSource();

        try
        {
            var cancelled = AttachmentOpenService.DownloadToCacheAsync(
                attachment,
                transport,
                cancelledConsumer.Token);
            await transport.DownloadStarted.Task;
            var survivor = AttachmentOpenService.DownloadToCacheAsync(attachment, transport);

            cancelledConsumer.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelled);
            Assert.Null(AttachmentOpenService.TryGetCachedFile(attachment));

            transport.ReleaseDownload();
            var file = await survivor;

            Assert.Equal(1, transport.DownloadCalls);
            Assert.False(transport.DownloadCancellationWasRequested);
            Assert.Equal("partialcomplete payload", await File.ReadAllTextAsync(file.Path));
        }
        finally
        {
            await AttachmentOpenService.PurgeCacheAsync();
        }
    }

    [Fact]
    public async Task PurgeCache_RemovesExistingFilesAndPreventsInFlightWritesAfterLogout()
    {
        var cachedPayload = "old account attachment"u8.ToArray();
        var cachedAttachment = CreateLocalAuthenticatedAttachment(cachedPayload);
        var sourcePath = Path.GetTempFileName();
        var inFlightAttachment = CreateAttachment();
        var transport = new BlockingAttachmentTransport();
        try
        {
            await File.WriteAllBytesAsync(sourcePath, cachedPayload);
            await AttachmentOpenService.CacheLocalCopyAsync(cachedAttachment, sourcePath);
            Assert.NotNull(await AttachmentOpenService.TryGetValidatedCachedFileAsync(
                cachedAttachment));

            await AttachmentOpenService.PurgeCacheAsync();

            Assert.Null(await AttachmentOpenService.TryGetValidatedCachedFileAsync(
                cachedAttachment));
            var temporaryFilesBeforeDownload = GetTemporaryFiles();
            var inFlight = AttachmentOpenService.DownloadToCacheAsync(inFlightAttachment, transport);
            await transport.DownloadStarted.Task;
            var purge = AttachmentOpenService.PurgeCacheAsync();
            await Task.Delay(50);
            Assert.False(purge.IsCompleted);
            transport.ReleaseDownload();

            await purge;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await inFlight);
            Assert.Null(AttachmentOpenService.TryGetCachedFile(inFlightAttachment));
            Assert.Equal(temporaryFilesBeforeDownload, GetTemporaryFiles());
        }
        finally
        {
            TryDelete(sourcePath);
            await AttachmentOpenService.PurgeCacheAsync();
        }
    }

    [Fact]
    public async Task DistinctDownloadsRunInParallelInsteadOfHoldingTheCommitGateDuringNetworkIo()
    {
        var attachments = Enumerable.Range(0, 3).Select(_ => CreateAttachment()).ToArray();
        var transport = new ParallelBlockingAttachmentTransport(attachments.Length);
        try
        {
            var downloads = attachments
                .Select(attachment => AttachmentOpenService.DownloadToCacheAsync(attachment, transport))
                .ToArray();

            await transport.AllStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(3, transport.PeakConcurrency);

            transport.ReleaseDownloads();
            await Task.WhenAll(downloads);
        }
        finally
        {
            transport.ReleaseDownloads();
            await AttachmentOpenService.PurgeCacheAsync();
        }
    }

    [Fact]
    public async Task CacheKeySeparatesSameIdAndNameByDigestAndEncryptionKey()
    {
        var id = $"shared-{Guid.NewGuid():N}";
        var first = CreateAttachment(id, Convert.ToBase64String(new byte[] { 1, 2, 3 }), Convert.ToBase64String(new byte[] { 4, 5, 6 }));
        var second = CreateAttachment(id, Convert.ToBase64String(new byte[] { 7, 8, 9 }), Convert.ToBase64String(new byte[] { 10, 11, 12 }));
        try
        {
            var firstFile = await AttachmentOpenService.DownloadToCacheAsync(first, new ImmediateAttachmentTransport("first"));
            var secondFile = await AttachmentOpenService.DownloadToCacheAsync(second, new ImmediateAttachmentTransport("second"));

            Assert.NotEqual(firstFile.Path, secondFile.Path);
            Assert.Equal("first", await File.ReadAllTextAsync(firstFile.Path));
            Assert.Equal("second", await File.ReadAllTextAsync(secondFile.Path));
            Assert.DoesNotContain(first.EncryptionKeyBase64!, firstFile.Path, StringComparison.Ordinal);
        }
        finally
        {
            await AttachmentOpenService.PurgeCacheAsync();
        }
    }

    [Fact]
    public async Task CacheIsScopedToTheActiveAccount()
    {
        var payload = "account-a"u8.ToArray();
        var attachment = CreateLocalAuthenticatedAttachment(payload);
        var sourcePath = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(sourcePath, payload);
            AttachmentOpenService.SetAccountScope("account-a");
            var first = await AttachmentOpenService.CacheLocalCopyAsync(attachment, sourcePath);

            AttachmentOpenService.SetAccountScope("account-b");
            Assert.Null(await AttachmentOpenService.TryGetValidatedCachedFileAsync(
                attachment));

            AttachmentOpenService.SetAccountScope("account-a");
            Assert.Equal(
                first.Path,
                (await AttachmentOpenService.TryGetValidatedCachedFileAsync(attachment))?.Path);
        }
        finally
        {
            TryDelete(sourcePath);
            await AttachmentOpenService.PurgeCacheAsync();
            AttachmentOpenService.ClearAccountScope();
        }
    }

    [Fact]
    public async Task CacheQuotaEvictsLeastRecentlyUsedFilesAndRemovesOrphanTemps()
    {
        await AttachmentOpenService.PurgeCacheAsync();
        Assert.Equal(256L * 1024 * 1024, AttachmentOpenService.AttachmentCacheByteQuota);
        var suffix = Guid.NewGuid().ToString("N");
        var oldest = Path.Combine(FileSystem.CacheDirectory, $"attachment-{suffix}-old.bin");
        var middle = Path.Combine(FileSystem.CacheDirectory, $"attachment-{suffix}-middle.bin");
        var newest = Path.Combine(FileSystem.CacheDirectory, $"attachment-{suffix}-new.bin");
        var orphan = Path.Combine(FileSystem.CacheDirectory, $".attachment-{suffix}.tmp");
        try
        {
            await File.WriteAllBytesAsync(oldest, new byte[6]);
            await File.WriteAllBytesAsync(middle, new byte[6]);
            await File.WriteAllBytesAsync(newest, new byte[6]);
            await File.WriteAllTextAsync(orphan, "orphan");
            File.SetLastAccessTimeUtc(oldest, DateTime.UtcNow - TimeSpan.FromHours(3));
            File.SetLastAccessTimeUtc(middle, DateTime.UtcNow - TimeSpan.FromHours(2));
            File.SetLastAccessTimeUtc(newest, DateTime.UtcNow - TimeSpan.FromHours(1));

            var retainedBytes = await AttachmentOpenService.EnforceCacheQuotaAsync(12);

            Assert.Equal(12, retainedBytes);
            Assert.False(File.Exists(oldest));
            Assert.True(File.Exists(middle));
            Assert.True(File.Exists(newest));
            Assert.False(File.Exists(orphan));
        }
        finally
        {
            TryDelete(oldest);
            TryDelete(middle);
            TryDelete(newest);
            TryDelete(orphan);
            await AttachmentOpenService.PurgeCacheAsync();
        }
    }

    [Fact]
    public async Task OversizedDownloadIsRejectedAndItsTemporaryFileIsClosedAndDeleted()
    {
        var attachment = CreateAttachment();
        var temporaryFilesBefore = GetTemporaryFiles();
        try
        {
            await Assert.ThrowsAsync<IOException>(() =>
                AttachmentOpenService.DownloadToCacheAsync(attachment, new OversizedAttachmentTransport()));

            Assert.Null(AttachmentOpenService.TryGetCachedFile(attachment));
            Assert.Equal(temporaryFilesBefore, GetTemporaryFiles());
        }
        finally
        {
            await AttachmentOpenService.PurgeCacheAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidatedCache_RemovesTamperAndDownloadsAuthenticatedPlaintextAgain(
        bool truncate)
    {
        var payload = "authenticated attachment payload"u8.ToArray();
        var attachment = CreateAuthenticatedAttachment(payload);
        var transport = new CountingAttachmentTransport(payload);
        try
        {
            var first = await AttachmentOpenService.DownloadToCacheAsync(
                attachment,
                transport);
            Assert.Equal(1, transport.DownloadCalls);

            var tampered = truncate
                ? payload[..^1]
                : payload.Select(static value => (byte)(value ^ 0x5a)).ToArray();
            await File.WriteAllBytesAsync(first.Path, tampered);

            var recovered = await AttachmentOpenService.DownloadToCacheAsync(
                attachment,
                transport);

            Assert.Equal(2, transport.DownloadCalls);
            Assert.Equal(payload, await File.ReadAllBytesAsync(recovered.Path));
            Assert.NotNull(await AttachmentOpenService.TryGetValidatedCachedFileAsync(
                attachment));
        }
        finally
        {
            await AttachmentOpenService.PurgeCacheAsync();
        }
    }

    [Fact]
    public async Task CacheLocalCopy_RejectsPlaintextThatDoesNotMatchAuthenticatedMetadata()
    {
        var expected = "expected local plaintext"u8.ToArray();
        var attachment = CreateAuthenticatedAttachment(expected);
        var sourcePath = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(sourcePath, "different local bytes"u8.ToArray());

            await Assert.ThrowsAsync<CryptographicException>(() =>
                AttachmentOpenService.CacheLocalCopyAsync(attachment, sourcePath));
            Assert.Null(await AttachmentOpenService.TryGetValidatedCachedFileAsync(
                attachment));
        }
        finally
        {
            TryDelete(sourcePath);
            await AttachmentOpenService.PurgeCacheAsync();
        }
    }

    [Fact]
    public async Task CacheLocalCopy_RejectsLocalMetadataWithoutPlaintextDigest()
    {
        var payload = "local plaintext without authentication"u8.ToArray();
        var attachment = new AttachmentMetadata(
            $"attachment-local-{Guid.NewGuid():N}",
            "payload.bin",
            "application/octet-stream",
            payload.LongLength,
            null,
            null,
            null);
        var sourcePath = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(sourcePath, payload);

            await Assert.ThrowsAsync<CryptographicException>(() =>
                AttachmentOpenService.CacheLocalCopyAsync(attachment, sourcePath));
            Assert.Null(await AttachmentOpenService.TryGetValidatedCachedFileAsync(
                attachment));
        }
        finally
        {
            TryDelete(sourcePath);
            await AttachmentOpenService.PurgeCacheAsync();
        }
    }

    [Fact]
    public async Task OpenPreparedFileAsync_ThrowsTypedFailureWhenOperatingSystemRejectsRequest()
    {
        var path = Path.GetTempFileName();
        try
        {
            var file = new PreparedAttachmentFile(
                "attachment.bin",
                "application/octet-stream",
                path);

            await Assert.ThrowsAsync<AttachmentOpenRejectedException>(() =>
                AttachmentOpenService.OpenPreparedFileAsync(
                    file,
                    static _ => Task.FromResult(false)));
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static AttachmentMetadata CreateAuthenticatedAttachment(byte[] payload) =>
        new(
            $"attachment-authenticated-{Guid.NewGuid():N}",
            "payload.bin",
            "application/octet-stream",
            payload.LongLength,
            new Uri("https://attachments.invalid/file"),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            Convert.ToBase64String(SHA256.HashData(payload)));

    private static AttachmentMetadata CreateLocalAuthenticatedAttachment(byte[] payload) =>
        new(
            $"attachment-local-authenticated-{Guid.NewGuid():N}",
            "payload.bin",
            "application/octet-stream",
            payload.LongLength,
            null,
            null,
            Convert.ToBase64String(SHA256.HashData(payload)));

    private static AttachmentMetadata CreateAttachment(
        string? id = null,
        string? key = null,
        string? digest = null) => new(
        id ?? $"attachment-test-{Guid.NewGuid():N}",
        "payload.bin",
        "application/octet-stream",
        16,
        new Uri("https://attachments.invalid/file"),
        key,
        digest);

    private static HashSet<string> GetTemporaryFiles() =>
        Directory.Exists(FileSystem.CacheDirectory)
            ? Directory.EnumerateFiles(FileSystem.CacheDirectory, ".attachment-*.tmp")
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

    private static void TryDelete(string? path)
    {
        if (path is not null && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed class BlockingAttachmentTransport : IAttachmentFileTransport
    {
        private readonly TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DownloadCalls;

        public bool DownloadCancellationWasRequested { get; private set; }

        public bool IsEnabled => true;

        public Task<AttachmentMetadata> UploadAsync(
            AttachmentFileUpload upload,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AttachmentFileDownload> DownloadAsync(
            AttachmentMetadata metadata,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<AttachmentFileDownloadInfo> DownloadToAsync(
            AttachmentMetadata metadata,
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref DownloadCalls);
            await destination.WriteAsync("partial"u8.ToArray(), CancellationToken.None);
            DownloadStarted.TrySetResult(true);
            await release.Task;
            DownloadCancellationWasRequested = cancellationToken.IsCancellationRequested;
            await destination.WriteAsync("complete payload"u8.ToArray(), CancellationToken.None);
            return new AttachmentFileDownloadInfo(metadata.FileName, metadata.ContentType);
        }

        public void ReleaseDownload() => release.TrySetResult(true);
    }

    private sealed class ImmediateAttachmentTransport(string payload) : IAttachmentFileTransport
    {
        public bool IsEnabled => true;

        public Task<AttachmentMetadata> UploadAsync(AttachmentFileUpload upload, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AttachmentFileDownload> DownloadAsync(AttachmentMetadata metadata, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<AttachmentFileDownloadInfo> DownloadToAsync(
            AttachmentMetadata metadata,
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            await destination.WriteAsync(System.Text.Encoding.UTF8.GetBytes(payload), cancellationToken);
            return new AttachmentFileDownloadInfo(metadata.FileName, metadata.ContentType);
        }
    }

    private sealed class CountingAttachmentTransport(byte[] payload) : IAttachmentFileTransport
    {
        public int DownloadCalls { get; private set; }

        public bool IsEnabled => true;

        public Task<AttachmentMetadata> UploadAsync(
            AttachmentFileUpload upload,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AttachmentFileDownload> DownloadAsync(
            AttachmentMetadata metadata,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<AttachmentFileDownloadInfo> DownloadToAsync(
            AttachmentMetadata metadata,
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            DownloadCalls++;
            await destination.WriteAsync(payload, cancellationToken);
            return new AttachmentFileDownloadInfo(metadata.FileName, metadata.ContentType);
        }
    }

    private sealed class ParallelBlockingAttachmentTransport(int expectedDownloads) : IAttachmentFileTransport
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int active;
        private int started;
        private int peak;

        public bool IsEnabled => true;
        public int PeakConcurrency => Volatile.Read(ref peak);
        public TaskCompletionSource AllStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AttachmentMetadata> UploadAsync(AttachmentFileUpload upload, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AttachmentFileDownload> DownloadAsync(AttachmentMetadata metadata, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<AttachmentFileDownloadInfo> DownloadToAsync(
            AttachmentMetadata metadata,
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            var current = Interlocked.Increment(ref active);
            UpdatePeak(current);
            if (Interlocked.Increment(ref started) == expectedDownloads)
            {
                AllStarted.TrySetResult();
            }

            try
            {
                await release.Task.WaitAsync(cancellationToken);
                await destination.WriteAsync("payload"u8.ToArray(), cancellationToken);
                return new AttachmentFileDownloadInfo(metadata.FileName, metadata.ContentType);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }

        public void ReleaseDownloads() => release.TrySetResult();

        private void UpdatePeak(int candidate)
        {
            var observed = Volatile.Read(ref peak);
            while (candidate > observed)
            {
                var previous = Interlocked.CompareExchange(ref peak, candidate, observed);
                if (previous == observed)
                {
                    return;
                }

                observed = previous;
            }
        }
    }

    private sealed class OversizedAttachmentTransport : IAttachmentFileTransport
    {
        public bool IsEnabled => true;

        public Task<AttachmentMetadata> UploadAsync(AttachmentFileUpload upload, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AttachmentFileDownload> DownloadAsync(AttachmentMetadata metadata, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AttachmentFileDownloadInfo> DownloadToAsync(
            AttachmentMetadata metadata,
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            destination.SetLength(AttachmentOpenService.AttachmentCacheByteQuota + 1);
            return Task.FromResult(new AttachmentFileDownloadInfo(metadata.FileName, metadata.ContentType));
        }
    }
}
