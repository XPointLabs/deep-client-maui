using Deep.Client.Maui.Services;

namespace Deep.Client.Maui.Clean.Tests;

public sealed class Did2ProbeStorageScopeTests
{
    [Fact]
    public void CanaryIsStablePerNetworkAndDistinctFromLocalProbe()
    {
        var root = Path.GetFullPath(Path.GetTempPath());
        var network = Enumerable.Repeat((byte)0xab, 16).ToArray();
        var first = Did2ProbeStorageScope.ResolveCanaryRoot(root, network);
        Assert.Equal(first, Did2ProbeStorageScope.ResolveCanaryRoot(root, network.ToArray()));
        network[0] ^= 1;
        Assert.NotEqual(first, Did2ProbeStorageScope.ResolveCanaryRoot(root, network));
        Assert.Equal(Path.Combine(root, "XPointLabs", "DeepDid2CanaryProbe",
            string.Concat(Enumerable.Repeat("ab", 16))), first);
        Assert.NotEqual(Path.Combine(root, "XPointLabs", "DeepDid2AccountProbe"), first);
    }

    [Fact]
    public void PhysicalCustodyIsStablePerNetworkAndSeparateFromBothProbes()
    {
        var root = Path.GetFullPath(Path.GetTempPath());
        var network = Enumerable.Repeat((byte)0xab, 16).ToArray();
        var physical = Did2ProbeStorageScope.ResolvePhysicalRoot(root, network);
        Assert.Equal(physical, Did2ProbeStorageScope.ResolvePhysicalRoot(root, network.ToArray()));
        Assert.NotEqual(physical, Did2ProbeStorageScope.ResolveCanaryRoot(root, network));
        Assert.NotEqual(physical, Path.Combine(root, "XPointLabs", "DeepDid2AccountProbe"));
        Assert.Contains(Path.Combine("XPointLabs", "DeepDid2Physical"), physical);
        network[0] ^= 1;
        Assert.NotEqual(physical, Did2ProbeStorageScope.ResolvePhysicalRoot(root, network));
        Assert.Throws<ArgumentException>(() => Did2ProbeStorageScope.ResolvePhysicalRoot(root, new byte[16]));
        Assert.Throws<ArgumentException>(() => Did2ProbeStorageScope.ResolvePhysicalRoot("relative", network));
    }

    [Fact]
    public void MissingOrHostileScopeRejectsBeforeAnyFilesystemMutation()
    {
        var network = Enumerable.Repeat((byte)1, 16).ToArray();
        Assert.Throws<ArgumentException>(() => Did2ProbeStorageScope.ResolveCanaryRoot("", network));
        Assert.Throws<ArgumentException>(() => Did2ProbeStorageScope.ResolveCanaryRoot("relative", network));
        Assert.Throws<ArgumentException>(() => Did2ProbeStorageScope.ResolveCanaryRoot(Path.GetTempPath(), new byte[16]));
        Assert.Throws<ArgumentException>(() => Did2ProbeStorageScope.ResolveCanaryRoot(Path.GetTempPath(), new byte[32]));
    }

    [Fact]
    public void OwnedPhysicalGuardAcceptsOnlyDescendantsAndDoesNotCreateThem()
    {
        using var scope = new OwnedPathScope();
        var physical = Did2ProbeStorageScope.ResolvePhysicalRoot(scope.Anchor, Enumerable.Repeat((byte)1, 16).ToArray());
        Did2ProbeStorageScope.RequireOwnedPhysicalPath(scope.Anchor, physical);
        Assert.False(Directory.Exists(physical));
        Directory.CreateDirectory(physical);
        Did2ProbeStorageScope.RequireOwnedPhysicalPath(scope.Anchor, physical);
        Assert.Throws<InvalidOperationException>(() => Did2ProbeStorageScope.RequireOwnedPhysicalPath(scope.Anchor, scope.Anchor));
        Assert.Throws<InvalidOperationException>(() => Did2ProbeStorageScope.RequireOwnedPhysicalPath(scope.Anchor, Path.Combine(scope.Root, "files-sibling", "state")));
        Assert.Throws<InvalidOperationException>(() => Did2ProbeStorageScope.RequireOwnedPhysicalPath(scope.Anchor, Path.Combine(scope.Anchor, "..", "outside", "state")));
        Assert.Throws<ArgumentException>(() => Did2ProbeStorageScope.RequireOwnedPhysicalPath("relative", physical));
    }

    [Fact]
    public void OwnedPhysicalGuardRejectsFileComponentsBeforeDirectoryCreation()
    {
        using var scope = new OwnedPathScope();
        var file = Path.Combine(scope.Anchor, "not-a-directory");
        File.WriteAllText(file, "synthetic test fixture");
        Assert.Throws<InvalidOperationException>(() => Did2ProbeStorageScope.RequireOwnedPhysicalPath(scope.Anchor, Path.Combine(file, "state")));
    }

    [Fact]
    public void OwnedPhysicalGuardRejectsExistingAndDanglingDirectoryRedirects()
    {
        using var scope = new OwnedPathScope();
        var target = Path.Combine(scope.Root, "outside"); Directory.CreateDirectory(target);
        var link = Path.Combine(scope.Anchor, "redirect");
        if (OperatingSystem.IsWindows())
        {
            // Junctions need no symlink privilege. Both endpoints belong to
            // this fresh fixture; no account or production directory is used.
            if ((link + target).IndexOfAny(['&', '|', '<', '>', '^', '%', '!', '"', '\r', '\n']) >= 0)
                throw new InvalidOperationException("The fixture path is not safe for the fixed junction command.");
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start)!;
            if (!process.WaitForExit(10_000)) { process.Kill(); throw new TimeoutException("Fixture junction creation timed out."); }
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, target);
        try
        {
            Assert.Throws<InvalidOperationException>(() => Did2ProbeStorageScope.RequireOwnedPhysicalPath(scope.Anchor, Path.Combine(link, "state")));
            Directory.Delete(target);
            Assert.Throws<InvalidOperationException>(() => Did2ProbeStorageScope.RequireOwnedPhysicalPath(scope.Anchor, Path.Combine(link, "state")));
        }
        finally { Directory.Delete(link); }
    }

    private sealed class OwnedPathScope : IDisposable
    {
        internal string Root { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "deep-did2-owned-path-" + Guid.NewGuid().ToString("N")));
        internal string Anchor => Path.Combine(Root, "files");
        internal OwnedPathScope() => Directory.CreateDirectory(Anchor);
        public void Dispose()
        {
            if (!string.Equals(Path.GetDirectoryName(Root), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(Root).StartsWith("deep-did2-owned-path-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the exact owned fixture.");
            Directory.Delete(Root, recursive: true);
        }
    }
}
