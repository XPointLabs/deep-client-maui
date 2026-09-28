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
    public void MissingOrHostileScopeRejectsBeforeAnyFilesystemMutation()
    {
        var network = Enumerable.Repeat((byte)1, 16).ToArray();
        Assert.Throws<ArgumentException>(() => Did2ProbeStorageScope.ResolveCanaryRoot("", network));
        Assert.Throws<ArgumentException>(() => Did2ProbeStorageScope.ResolveCanaryRoot("relative", network));
        Assert.Throws<ArgumentException>(() => Did2ProbeStorageScope.ResolveCanaryRoot(Path.GetTempPath(), new byte[16]));
        Assert.Throws<ArgumentException>(() => Did2ProbeStorageScope.ResolveCanaryRoot(Path.GetTempPath(), new byte[32]));
    }
}
