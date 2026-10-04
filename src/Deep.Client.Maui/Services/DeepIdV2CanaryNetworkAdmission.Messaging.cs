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
    private static async Task<T> RunConversationAsync<T>(DeepIdV2AccountService accounts,
        Func<DeepIdV2ContactPathAuthoritySource, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        var origin = ValidatedOrigin();
        var (genesis, authority, head, pin) = await ReadBootstrapAsync(ct);
        var floor = await accounts.OpenDirectoryLkgStoreAsync(authority, head, pin, ct);
        var factory = new HttpServiceTransportFactory(HttpServiceEndpointPolicy.Production);
        using var verifier = DeepMlDsa65CandidateVerifierFactory.OpenForCurrentProcess();
        var clock = new CanaryMonotonicClock();
        using var proofs = factory.CreateDeepIdV2DirectoryProofClient(origin, clock, verifier, floor, clientOptions: DiagnosticHttpOptions());
        using var artifacts = factory.CreateDeepIdV2NetworkClosureArtifactSource(origin, clientOptions: DiagnosticHttpOptions());
        var networkFloor = await accounts.OpenNetworkLkgStoreAsync(genesis, ct);
        var source = new DeepIdV2ContactPathAuthoritySource(genesis, accounts, proofs, artifacts, networkFloor, clock);
        return await action(source, ct);
    }
    public Task<DeepIdV2ContactStartResult> StartAsync(DeepIdV2AccountService accounts, DeepPermanentIdV2 address,
        ReadOnlyMemory<byte> intent, CancellationToken ct = default)
        => RunConversationAsync(accounts, (source, token) => accounts.StartContactAsync(address, intent, source, token), ct);
    public Task<IReadOnlyList<DeepIdV2ConversationSnapshot>> ListAsync(DeepIdV2AccountService accounts, CancellationToken ct = default)
        => accounts.ListConversationsAsync(ct);
    public async Task AcceptAsync(DeepIdV2AccountService accounts, DeepIdV2Conversation conversation, ReadOnlyMemory<byte> op, CancellationToken ct = default)
        => _ = await RunConversationAsync(accounts, (source, token) => accounts.AcceptContactAsync(conversation, op, source, token), ct);
    public async Task SendTextAsync(DeepIdV2AccountService accounts, DeepIdV2Conversation conversation,
        ReadOnlyMemory<byte> op, string text, CancellationToken ct = default)
        => _ = await RunConversationAsync(accounts, (source, token) => accounts.SendTextAsync(conversation, op, text, source, token), ct);
    public Task<IReadOnlyList<DirectMessageCreateSnapshot>> MessagesAsync(DeepIdV2AccountService accounts,
        DeepIdV2Conversation conversation, CancellationToken ct = default)
        => accounts.ListMessagesAsync(conversation, ct);
    public Task<DeepIdV2MailboxSynchronizationResult> SynchronizeAsync(DeepIdV2AccountService accounts, CancellationToken ct = default)
        => RunConversationAsync(accounts, accounts.SynchronizeOwnMailboxAsync, ct);
}
#endif
