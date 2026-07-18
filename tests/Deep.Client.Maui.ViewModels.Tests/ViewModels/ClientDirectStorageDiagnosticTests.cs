using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Features;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.ViewModels.Tests.ViewModels;

public sealed class ClientDirectStorageDiagnosticTests
{
    [DirectStorageContractFact]
    public async Task DirectStorageTransportContract_IsDiagnosticAndCannotSatisfyTheRoutedLane()
    {
        Assert.False(
            string.Equals(
                Environment.GetEnvironmentVariable("DEEP_STRICT_LIVE"),
                "1",
                StringComparison.Ordinal),
            "Direct storage diagnostics cannot execute as routed release evidence.");

        var storageUrl = Environment.GetEnvironmentVariable("DEEP_STORAGE_URL");
        Assert.False(string.IsNullOrWhiteSpace(storageUrl));
        var aliceRuntime = CreateDirectStorageRuntime(storageUrl!);
        var bobRuntime = CreateDirectStorageRuntime(storageUrl!);
        var alice = await aliceRuntime.Accounts.RegisterAsync("Alice direct contract");
        var bob = await bobRuntime.Accounts.RegisterAsync("Bob direct contract");
        _ = await aliceRuntime.Conversations.GetOrCreateOneToOneAsync(bob.SessionId, "Bob");
        var body = $"direct-storage-contract-{Guid.NewGuid():N}";
        await aliceRuntime.Messages.SendOneToOneAsync(alice.SessionId, bob.SessionId, body);
        await bobRuntime.Messages.ReceiveAsync(bob.SessionId);
        var received = await bobRuntime.Messages.ListConversationMessagesAsync(
            ConversationId.ForOneToOne(alice.SessionId));
        Assert.Contains(received, message => message.Body == body);
    }

    private static ClientRuntime CreateDirectStorageRuntime(string storageUrl) =>
        new(
            new InMemorySessionStore(),
            ClientFeatureFlags.ReleaseDefaults,
            new SystemClock(),
            new SessionStorageMessageTransport(
                new HttpClient(),
                new SessionStorageMessageTransportOptions(storageUrl)),
            requireE2eeTransport: true);
}
