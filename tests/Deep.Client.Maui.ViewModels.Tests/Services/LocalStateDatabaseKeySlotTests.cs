using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class LocalStateDatabaseKeySlotTests
{
    [Fact]
    public void LaneNamesAreStableDistinctAndOrdinaryNameIsUnchanged()
    {
        Assert.Equal(
            "client-state.sqlcipher-key.v1",
            LocalStateDatabaseKeySlot.NameForLane(LocalStateDatabaseLane.Ordinary));
        Assert.Equal(
            "client-state.sqlcipher-key.physical-e2e.v1",
            LocalStateDatabaseKeySlot.NameForLane(LocalStateDatabaseLane.PhysicalE2E));
        Assert.NotEqual(
            LocalStateDatabaseKeySlot.OrdinarySlotName,
            LocalStateDatabaseKeySlot.PhysicalE2ESlotName);
#if DEEP_PHYSICAL_E2E
        Assert.Equal(
            LocalStateDatabaseKeySlot.PhysicalE2ESlotName,
            LocalStateDatabaseKeySlot.ActiveSlotName);
#else
        Assert.Equal(
            LocalStateDatabaseKeySlot.OrdinarySlotName,
            LocalStateDatabaseKeySlot.ActiveSlotName);
#endif
    }

    [Fact]
    public async Task PhysicalResetReplacesOnlyPhysicalKey()
    {
        var storage = new RecordingKeyStorage();
        var ordinary = LocalStateDatabaseKeySlot.ForLane(
            LocalStateDatabaseLane.Ordinary, storage);
        var physical = LocalStateDatabaseKeySlot.ForLane(
            LocalStateDatabaseLane.PhysicalE2E, storage);
        var ordinaryKey = await ordinary.ResolveAsync(CancellationToken.None);
        var firstPhysicalKey = await physical.ResolveAsync(CancellationToken.None);

        var resetPhysicalKey = await physical.ResetAsync(CancellationToken.None);

        Assert.NotEqual(firstPhysicalKey, resetPhysicalKey);
        Assert.Equal(ordinaryKey, await storage.GetAsync(ordinary.Name));
        Assert.Equal(resetPhysicalKey, await storage.GetAsync(physical.Name));
        Assert.Equal([physical.Name], storage.RemovedKeys);
    }

    [Fact]
    public async Task ReopenInSameLaneUsesStableKeyWithoutRewrite()
    {
        var storage = new RecordingKeyStorage();
        var firstProcess = LocalStateDatabaseKeySlot.ForLane(
            LocalStateDatabaseLane.PhysicalE2E, storage);
        var firstKey = await firstProcess.ResolveAsync(CancellationToken.None);
        var reopenedProcess = LocalStateDatabaseKeySlot.ForLane(
            LocalStateDatabaseLane.PhysicalE2E, storage);

        var reopenedKey = await reopenedProcess.ResolveAsync(CancellationToken.None);

        Assert.Equal(firstKey, reopenedKey);
        Assert.Equal(1, storage.SetCount);
        Assert.Empty(storage.RemovedKeys);
    }

    [Fact]
    public async Task DatabaseCreatedInOneLaneRejectsOtherLaneKeyAndReopensWithOriginalKey()
    {
        var storage = new RecordingKeyStorage();
        var ordinaryKey = await LocalStateDatabaseKeySlot.ForLane(
                LocalStateDatabaseLane.Ordinary, storage)
            .ResolveAsync(CancellationToken.None);
        var physicalKey = await LocalStateDatabaseKeySlot.ForLane(
                LocalStateDatabaseLane.PhysicalE2E, storage)
            .ResolveAsync(CancellationToken.None);
        var statePath = TemporaryDatabasePath();

        try
        {
            using (var created = new SqliteSessionStore(
                       new SqliteSessionStoreOptions(statePath, physicalKey)))
            {
                await created.SetAsync("lane", "physical");
            }

            var exception = Assert.Throws<LocalStateResetRequiredException>(
                () => new SqliteSessionStore(
                    new SqliteSessionStoreOptions(statePath, ordinaryKey)));
            Assert.Equal(LocalStateResetRequiredReason.UnreadableOrWrongKey, exception.Reason);

            using var reopened = new SqliteSessionStore(
                new SqliteSessionStoreOptions(statePath, physicalKey));
            Assert.Equal("physical", await reopened.GetAsync<string>("lane"));
        }
        finally
        {
            DeleteDatabase(statePath);
        }
    }

    private static ClientRuntime CreatePersistentTestRuntime(string path, string key) =>
        new(
            new SqliteSessionStore(new SqliteSessionStoreOptions(path, key)),
            Deep.Client.Shared.Features.ClientFeatureFlags.Defaults,
            new SystemClock(),
            new StubSessionBackend());

    private static string TemporaryDatabasePath() =>
        Path.Combine(
            Path.GetTempPath(),
            $"deep-local-state-key-slot-{Guid.NewGuid():N}.db");

    private static void DeleteDatabase(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
        {
            if (File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }
    }

    private sealed class RecordingKeyStorage : ILocalStateDatabaseKeyStorage
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

        public int SetCount { get; private set; }

        public List<string> RemovedKeys { get; } = [];

        public Task<string?> GetAsync(string key) =>
            Task.FromResult(values.TryGetValue(key, out var value) ? value : null);

        public Task SetAsync(string key, string value)
        {
            values[key] = value;
            SetCount++;
            return Task.CompletedTask;
        }

        public bool Remove(string key)
        {
            RemovedKeys.Add(key);
            return values.Remove(key);
        }
    }
}
