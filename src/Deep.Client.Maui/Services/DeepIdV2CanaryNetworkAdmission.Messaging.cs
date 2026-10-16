#if DEEP_DID2_HTTPS_ADMISSION
using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.Services.ContactV2;
using Deep.Protocol.Identity;
using Deep.Protocol.ApplicationCore;

namespace Deep.Client.Maui.Services;

internal sealed partial class DeepIdV2CanaryNetworkAdmission : IDeepIdV2ConversationRuntime
{
    // Network mutations recreate fresh proof/authority. Read-only history
    // goes straight to the protected local owner, without bootstrap HTTP.
    private static Task<T> RunConversationAsync<T>(DeepIdV2AccountService accounts, string workStage,
        Func<DeepIdV2ContactPathAuthoritySource, CancellationToken, Task<T>> action, CancellationToken ct)
        => Task.Run(async () =>
    {
        // The domain owns synchronous native crypto/SQL work between awaits.
        // Keep it off the UI thread, without moving ViewModel mutations or
        // releasing the actual foreground lane before the delegate drains.
        using var work = Did2QaWorkDiagnostics.Begin(workStage, MauiProgram.ObserveDid2QaWork);
        var origin = ValidatedOrigin();
        var factory = await CreateHttpFactoryAsync(ct);
        var (genesis, authority, head, pin) = await ReadBootstrapAsync(factory, origin, ct);
        var floor = await accounts.OpenDirectoryLkgStoreAsync(authority, head, pin, ct);
        using var verifier = DeepMlDsa65CandidateVerifierFactory.OpenForCurrentProcess();
        var clock = new CanaryMonotonicClock();
        using var proofs = factory.CreateDeepIdV2DirectoryProofClient(origin, clock, verifier, floor, clientOptions: DiagnosticHttpOptions());
        using var artifacts = factory.CreateDeepIdV2NetworkClosureArtifactSource(origin, clientOptions: DiagnosticHttpOptions());
        var networkFloor = await accounts.OpenNetworkLkgStoreAsync(genesis, ct);
        var source = new DeepIdV2ContactPathAuthoritySource(genesis, accounts, proofs, artifacts, networkFloor, clock);
        return await action(source, ct);
    }, ct);
    public Task<DeepIdV2ContactStartResult> StartAsync(DeepIdV2AccountService accounts, DeepPermanentIdV2 address,
        ReadOnlyMemory<byte> intent, CancellationToken ct = default)
        => RunConversationAsync(accounts, "ContactStartTotal", (source, token) => accounts.StartContactAsync(address, intent, source, token), ct);
    public Task<IReadOnlyList<DeepIdV2ConversationSnapshot>> ListAsync(DeepIdV2AccountService accounts, CancellationToken ct = default)
        => Task.Run(() => accounts.ListConversationsAsync(ct), ct);
    public async Task StopContactRetryAsync(DeepIdV2AccountService accounts, ReadOnlyMemory<byte> intent, CancellationToken ct = default)
        => _ = await RunConversationAsync(accounts, "ContactStopTotal", async (source, token) =>
        { await accounts.CancelOwnInitialContactAsync(intent, source, token); return true; }, ct);
    public async Task AcceptAsync(DeepIdV2AccountService accounts, DeepIdV2Conversation conversation, ReadOnlyMemory<byte> op, CancellationToken ct = default)
        => _ = await RunConversationAsync(accounts, "ContactAcceptTotal", (source, token) => accounts.AcceptContactAsync(conversation, op, source, token), ct);
    public Task<ReadOnlyMemory<byte>> QueueTextAsync(DeepIdV2AccountService accounts, DeepIdV2Conversation conversation,
        string text, CancellationToken ct = default)
        => RunConversationAsync(accounts, "TextQueueTotal", (source, token) => accounts.QueueTextAsync(conversation, text, source, token), ct);
    public async Task RetryTextAsync(DeepIdV2AccountService accounts, DeepIdV2Conversation conversation,
        ReadOnlyMemory<byte> op, string text, CancellationToken ct = default)
        => _ = await RunConversationAsync(accounts, "TextRetryTotal", (source, token) => accounts.RetryTextAsync(conversation, op, text, source, token), ct);
    public Task<IReadOnlyList<DirectMessageCreateSnapshot>> MessagesAsync(DeepIdV2AccountService accounts,
        DeepIdV2Conversation conversation, CancellationToken ct = default)
        => Task.Run(() => accounts.ListMessagesAsync(conversation, ct), ct);
    public Task<DeepIdV2MailboxSynchronizationResult> SynchronizeAsync(DeepIdV2AccountService accounts, CancellationToken ct = default)
        => RunConversationAsync(accounts, "MailboxSyncTotal", accounts.SynchronizeOwnMailboxAsync, ct);
}
#endif
