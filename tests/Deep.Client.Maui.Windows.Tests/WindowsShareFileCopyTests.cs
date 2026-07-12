namespace Deep.Client.Maui.Windows.Tests;

public sealed class WindowsShareFileCopyTests
{
    [Fact]
    public async Task SuccessfulCopyPromotesOnlyTheCompleteDestination()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var destination = Path.Combine(directory, "shared.bin");
            var content = Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray();
            await using var source = new MemoryStream(content);

            var written = await WindowsShareFileCopy.CopyBoundedAsync(
                source,
                destination,
                content.Length,
                CancellationToken.None);

            Assert.Equal(content.Length, written);
            Assert.Equal(content, await File.ReadAllBytesAsync(destination));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.partial"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OversizedCopyDeletesBytesWrittenBeforeTheLimitWasExceeded()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var destination = Path.Combine(directory, "oversized.bin");
            await using var source = new MemoryStream(new byte[(64 * 1024) + 1]);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WindowsShareFileCopy.CopyBoundedAsync(
                    source,
                    destination,
                    64 * 1024,
                    CancellationToken.None));

            AssertNoCopyArtifacts(directory, destination);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CanceledCopyDeletesBytesWrittenBeforeCancellation()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var destination = Path.Combine(directory, "canceled.bin");
            using var cancellation = new CancellationTokenSource();
            await using var source = new CancelOnSecondReadStream(new byte[128 * 1024], cancellation);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                WindowsShareFileCopy.CopyBoundedAsync(
                    source,
                    destination,
                    128 * 1024,
                    cancellation.Token));

            AssertNoCopyArtifacts(directory, destination);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FailedSourceReadDeletesBytesWrittenBeforeTheFailure()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var destination = Path.Combine(directory, "failed.bin");
            await using var source = new ThrowOnSecondReadStream(new byte[128 * 1024]);

            await Assert.ThrowsAsync<IOException>(() =>
                WindowsShareFileCopy.CopyBoundedAsync(
                    source,
                    destination,
                    128 * 1024,
                    CancellationToken.None));

            AssertNoCopyArtifacts(directory, destination);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "deep-windows-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void AssertNoCopyArtifacts(string directory, string destination)
    {
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.EnumerateFiles(directory));
    }

    private sealed class CancelOnSecondReadStream(
        byte[] content,
        CancellationTokenSource cancellation) : MemoryStream(content)
    {
        private int readCount;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref readCount) == 2)
            {
                cancellation.Cancel();
                return ValueTask.FromCanceled<int>(cancellation.Token);
            }

            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class ThrowOnSecondReadStream(byte[] content) : MemoryStream(content)
    {
        private int readCount;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref readCount) == 2)
            {
                throw new IOException("simulated source failure");
            }

            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
