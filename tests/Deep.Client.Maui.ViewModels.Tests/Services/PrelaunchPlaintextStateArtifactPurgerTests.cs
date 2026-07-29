using Deep.Client.Maui.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class PrelaunchPlaintextStateArtifactPurgerTests
{
    [Fact]
    public async Task PurgeRemovesOnlyKnownPrelaunchPlaintextArtifacts()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"deep-prelaunch-plaintext-purge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var primaryPath = Path.Combine(directory, "client-state.json");
        var backupPath = primaryPath + ".migrated.bak";
        var temporaryPath = primaryPath + ".42.tmp";
        var unrelatedPath = Path.Combine(directory, "client-state.json.keep");

        try
        {
            await File.WriteAllTextAsync(primaryPath, "sensitive-primary");
            await File.WriteAllTextAsync(backupPath, "sensitive-backup");
            await File.WriteAllTextAsync(temporaryPath, "sensitive-temporary");
            await File.WriteAllTextAsync(unrelatedPath, "unrelated");

            await PrelaunchPlaintextStateArtifactPurger.PurgeAsync(directory);

            Assert.False(File.Exists(primaryPath));
            Assert.False(File.Exists(backupPath));
            Assert.False(File.Exists(temporaryPath));
            Assert.Equal("unrelated", await File.ReadAllTextAsync(unrelatedPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PurgeHonorsCancellationWithoutDeletingArtifacts()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"deep-prelaunch-plaintext-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var primaryPath = Path.Combine(directory, "client-state.json");

        try
        {
            await File.WriteAllTextAsync(primaryPath, "sensitive-primary");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => PrelaunchPlaintextStateArtifactPurger.PurgeAsync(
                    directory,
                    cancellation.Token));

            Assert.True(File.Exists(primaryPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
