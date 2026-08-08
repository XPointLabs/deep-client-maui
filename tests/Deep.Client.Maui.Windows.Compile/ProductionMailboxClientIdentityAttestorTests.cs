using Deep.Client.Maui.Services;

namespace Deep.Client.WindowsCompileTests;

public sealed class ProductionMailboxClientIdentityAttestorTests
{
    [Fact]
    public void BoundedInventoryRejectsReparseDirectoryBeforeTraversal()
    {
        var root = CreateRoot();
        var target = CreateRoot();
        try
        {
            File.WriteAllBytes(Path.Combine(target, "outside.dll"), [1]);
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

            Assert.Throws<InvalidDataException>(() =>
                ProductionMailboxClientIdentityAttestor.CaptureWindowsInventory(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public void BoundedInventoryRejectsMoreThanMaximumEntries()
    {
        var root = CreateRoot();
        try
        {
            for (var index = 0;
                 index <= ProductionMailboxClientIdentityAttestor.MaximumWindowsInventoryEntries;
                 index++)
                using (File.Create(Path.Combine(root, $"{index:D5}.bin"))) { }

            Assert.Throws<InvalidDataException>(() =>
                ProductionMailboxClientIdentityAttestor.CaptureWindowsInventory(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SignatureReadRejectsOversizedInputBeforeAllocation()
    {
        var path = Path.GetTempFileName();
        try
        {
            await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
                stream.SetLength(
                    ProductionMailboxClientIdentityAttestor.MaximumWindowsSignatureBytes + 1L);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ProductionMailboxClientIdentityAttestor.ReadBoundedFileAsync(
                    path,
                    ProductionMailboxClientIdentityAttestor.MaximumWindowsSignatureBytes,
                    CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(),
            $"deep-windows-inventory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
