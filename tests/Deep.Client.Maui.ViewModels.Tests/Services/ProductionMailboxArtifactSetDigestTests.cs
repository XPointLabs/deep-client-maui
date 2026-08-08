using Deep.Client.Maui.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class ProductionMailboxArtifactSetDigestTests
{
    [Fact]
    public async Task DigestCommitsAllFilesLogicalNamesAndSignerLineage()
    {
        var root = Path.Combine(Path.GetTempPath(),
            $"deep-artifact-set-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var basePath = Path.Combine(root, "base.apk");
            var splitPath = Path.Combine(root, "split.apk");
            await File.WriteAllBytesAsync(basePath, [1, 2, 3]);
            await File.WriteAllBytesAsync(splitPath, [4, 5, 6]);
            ReadOnlyMemory<byte>[] lineage = [Fill(0x11), Fill(0x22)];
            ProductionMailboxArtifactFile[] files =
            [
                new("android/split/config.en.apk", splitPath),
                new("android/base.apk", basePath)
            ];

            var first = await ProductionMailboxArtifactSetDigest.ComputeAsync(
                "network.xpoint.deep", lineage, files);
            var reordered = await ProductionMailboxArtifactSetDigest.ComputeAsync(
                "network.xpoint.deep", lineage, files.Reverse().ToArray());
            var changedName = await ProductionMailboxArtifactSetDigest.ComputeAsync(
                "network.xpoint.deep", lineage,
                [new("android/split/config.ru.apk", splitPath), files[1]]);
            var changedLineage = await ProductionMailboxArtifactSetDigest.ComputeAsync(
                "network.xpoint.deep", lineage.Reverse().ToArray(), files);
            await File.WriteAllBytesAsync(splitPath, [4, 5, 7]);
            var changedBytes = await ProductionMailboxArtifactSetDigest.ComputeAsync(
                "network.xpoint.deep", lineage, files);

            Assert.Equal(first, reordered);
            Assert.NotEqual(first, changedName);
            Assert.NotEqual(first, changedLineage);
            Assert.NotEqual(first, changedBytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DuplicateLogicalNameFailsClosed()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [1]);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ProductionMailboxArtifactSetDigest.ComputeAsync(
                    "Deep.Client.Maui",
                    [Fill(0x31)],
                    [new("windows/app.dll", path), new("windows/app.dll", path)]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AddedOrRemovedArtifactChangesCompleteInventoryDigest()
    {
        var root = CreateRoot();
        try
        {
            var firstPath = Path.Combine(root, "base.apk");
            var secondPath = Path.Combine(root, "feature.apk");
            await File.WriteAllBytesAsync(firstPath, [1, 2, 3]);
            await File.WriteAllBytesAsync(secondPath, [4, 5, 6]);
            var one = await ProductionMailboxArtifactSetDigest.ComputeAsync(
                "network.xpoint.deep", [Fill(0x11)],
                [new("android/base.apk", firstPath)]);
            var two = await ProductionMailboxArtifactSetDigest.ComputeAsync(
                "network.xpoint.deep", [Fill(0x11)],
                [new("android/base.apk", firstPath), new("android/feature.apk", secondPath)]);

            Assert.NotEqual(one, two);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DuplicateSignerLineageFailsClosed()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [1]);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ProductionMailboxArtifactSetDigest.ComputeAsync(
                    "network.xpoint.deep", [Fill(0x11), Fill(0x11)],
                    [new("android/base.apk", path)]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ArtifactCountAboveBoundFailsBeforeAnyFileOpen()
    {
        var artifacts = Enumerable.Range(0, 16_385)
            .Select(index => new ProductionMailboxArtifactFile(
                $"windows/{index:D5}.dll", "does-not-exist"))
            .ToArray();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ProductionMailboxArtifactSetDigest.ComputeAsync(
                "Deep.Client.Maui", [Fill(0x11)], artifacts));
    }

    [Fact]
    public async Task SameLengthMutationAttemptAtHashBoundaryIsBlocked()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.GetTempFileName();
        try
        {
            var original = Enumerable.Repeat((byte)0x41, 4096).ToArray();
            await File.WriteAllBytesAsync(path, original);
            var mutationWasBlocked = false;

            _ = await ProductionMailboxArtifactSetDigest.ComputeAsync(
                "Deep.Client.Maui", [Fill(0x11)], [new("windows/app.dll", path)],
                faultInjector: (point, openedPath) =>
                {
                    Assert.Equal(
                        ProductionMailboxArtifactDigestFaultPoint.AfterOpenBeforeHash,
                        point);
                    var replacement = Enumerable.Repeat((byte)0x42, original.Length).ToArray();
                    try
                    {
                        File.WriteAllBytes(openedPath, replacement);
                    }
                    catch (IOException)
                    {
                        mutationWasBlocked = true;
                    }
                });

            Assert.True(mutationWasBlocked);
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReparsePointAncestorFailsClosed()
    {
        var root = CreateRoot();
        var target = CreateRoot();
        try
        {
            var file = Path.Combine(target, "app.dll");
            await File.WriteAllBytesAsync(file, [1, 2, 3]);
            var link = Path.Combine(root, "linked");
            try
            {
                Directory.CreateSymbolicLink(link, target);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or
                PlatformNotSupportedException)
            {
                return;
            }

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ProductionMailboxArtifactSetDigest.ComputeAsync(
                    "Deep.Client.Maui", [Fill(0x11)],
                    [new("windows/app.dll", Path.Combine(link, "app.dll"))]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(target, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(),
            $"deep-artifact-set-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static byte[] Fill(byte value) => Enumerable.Repeat(value, 32).ToArray();
}
