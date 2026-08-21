using Deep.Client.Maui.Services;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class ProtectedProductionMailboxTrustStateStoreTests
{
    [Fact]
    public async Task OpenOrCreate_FirstRunCreatesProtectedEmptyStore()
    {
        var parent = Path.Combine(Path.GetTempPath(),
            "deep-production-mailbox-parent-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "runtime");
        Directory.CreateDirectory(parent);
        try
        {
            var store = ProtectedProductionMailboxTrustStateStore.OpenOrCreate(root);

            Assert.True(Directory.Exists(root));
            Assert.Null(await store.ReadAsync());
            if (OperatingSystem.IsWindows())
                WindowsMailboxAccessControl.ValidateTree(root);
        }
        finally { TryDelete(parent); }
    }

    [Fact]
    public async Task Commit_RestartAndConcurrentWriter_AreFailClosed()
    {
        var root = NewRoot();
        try
        {
            var first = new ProtectedProductionMailboxTrustStateStore(root);
            var state = State(1, 0x21);
            await first.CommitAsync(0, state);

            var restarted = new ProtectedProductionMailboxTrustStateStore(root);
            Assert.Equal(1UL, (await restarted.ReadAsync())!.Revision);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                restarted.CommitAsync(0, state));

            var successor = State(2, 0x31);
            await restarted.CommitAsync(1, successor);
            Assert.Equal(2UL, (await first.ReadAsync())!.Revision);
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task TruncatedOrTamperedState_IsNeverTreatedAsMissing()
    {
        var root = NewRoot();
        try
        {
            var store = new ProtectedProductionMailboxTrustStateStore(root);
            await store.CommitAsync(0, State(1, 0x41));
            var path = Path.Combine(root,
                ProtectedProductionMailboxTrustStateStore.StateFileName);
            File.WriteAllBytes(path, [1, 2, 3]);
            if (OperatingSystem.IsWindows()) WindowsMailboxAccessControl.ProtectNewFile(path);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync());
        }
        finally { TryDelete(root); }
    }

    private static ProductionMailboxTrustState State(ulong revision, byte marker) => new(
        revision,
        Anchor(1, 0x11),
        Anchor(revision, marker),
        Anchor(checked(revision + 1), (byte)(marker + 8)));

    private static ProductionMailboxTrustAnchor Anchor(ulong generation, byte marker) => new(
        Fill(32, 0x11), Fill(16, 0x12), generation, Fill(32, marker), generation,
        Fill(32, (byte)(marker + 1)), Fill(32, (byte)(marker + 2)), generation,
        Fill(32, (byte)(marker + 3)));

    private static byte[] Fill(int length, byte value) =>
        Enumerable.Repeat(value, length).ToArray();

    private static string NewRoot()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "deep-production-mailbox-lkg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows()) WindowsMailboxAccessControl.ProtectNewDirectory(path);
        return path;
    }

    private static void TryDelete(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
