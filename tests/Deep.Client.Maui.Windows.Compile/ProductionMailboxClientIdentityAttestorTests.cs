using Deep.Client.Maui.Services;

namespace Deep.Client.WindowsCompileTests;

public sealed class ProductionMailboxClientIdentityAttestorTests
{
    [Fact]
    public void PhysicalUatWindowsIdentityRequiresExactIsolatedPackageAndSigner()
    {
        var values = PhysicalUatWindowsValues();

        Assert.True(ProductionMailboxBuildTrustFloor.TryParsePhysicalUatWindowsIdentity(
            values, out var identity));
        Assert.NotNull(identity);
        Assert.Equal("network.xpoint.deep.e2e", identity!.InstalledPackageName);
        Assert.Equal("CN=XPoint Labs Development", identity.Publisher);
        Assert.Equal("Deep.Client.Maui.exe", identity.ApplicationIdentity);

        ProductionMailboxBuildTrustFloor.VerifyInstalledPhysicalUatWindowsTuple(
            identity,
            "network.xpoint.deep.e2e",
            "CN=XPoint Labs Development",
            "Deep.Client.Maui.exe",
            Fill(32, 0x71));
    }

    [Fact]
    public void PhysicalUatWindowsIdentityRejectsProductionPackagePartialOrWrongTuple()
    {
        var partial = PhysicalUatWindowsValues();
        partial.Remove(ProductionMailboxBuildTrustFloor.PhysicalUatWindowsSignerKey);
        var production = PhysicalUatWindowsValues();
        production[ProductionMailboxBuildTrustFloor.PhysicalUatWindowsPackageNameKey] =
            "network.xpoint.deep";

        foreach (var values in new[] { partial, production })
            Assert.Equal("production-credentials-unavailable",
                Assert.Throws<InvalidOperationException>(() =>
                    ProductionMailboxBuildTrustFloor.TryParsePhysicalUatWindowsIdentity(
                        values, out _)).Message);

        Assert.True(ProductionMailboxBuildTrustFloor.TryParsePhysicalUatWindowsIdentity(
            PhysicalUatWindowsValues(), out var identity));
        Assert.NotNull(identity);
        Assert.Throws<InvalidDataException>(() =>
            ProductionMailboxBuildTrustFloor.VerifyInstalledPhysicalUatWindowsTuple(
                identity!,
                "network.xpoint.deep.e2e",
                "CN=XPoint Labs Development",
                "Deep.Client.Maui.exe",
                Fill(32, 0x72)));
    }

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

    private static Dictionary<string, string?> PhysicalUatWindowsValues() =>
        new(StringComparer.Ordinal)
        {
            [ProductionMailboxBuildTrustFloor.PhysicalUatWindowsPackageNameKey] =
                "network.xpoint.deep.e2e",
            [ProductionMailboxBuildTrustFloor.PhysicalUatWindowsPublisherKey] =
                "CN=XPoint Labs Development",
            [ProductionMailboxBuildTrustFloor.PhysicalUatWindowsSignerKey] =
                Convert.ToHexStringLower(Fill(32, 0x71))
        };

    private static byte[] Fill(int count, byte value) =>
        Enumerable.Repeat(value, count).ToArray();
}
