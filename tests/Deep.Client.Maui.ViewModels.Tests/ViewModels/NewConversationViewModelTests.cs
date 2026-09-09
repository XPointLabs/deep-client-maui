using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Persistence.ContactV1;
using Deep.Client.Shared.Services.ContactV1;

namespace Deep.Client.Maui.ViewModels.Tests.ViewModels;

public sealed class NewConversationViewModelTests
{
    [Fact]
    public void VerifiedNavigationTargetCannotBeMintedByAnExternalCaller()
    {
        Assert.Empty(typeof(VerifiedDirectConversationTarget).GetConstructors());
    }

    [Fact]
    public async Task OfflineUnavailable_PreservesExactInputAndExposesRetry()
    {
        const string address = "deep1exact-canonical-placeholder";
        var runtime = new StubRuntime(_ => Pending());
        var viewModel = new NewConversationViewModel(runtime) { AddressInput = address };

        await viewModel.ResolveAsync();

        Assert.Equal(address, viewModel.AddressInput);
        Assert.Equal(ArbitraryContactUiState.PendingRetry, viewModel.State);
        Assert.True(viewModel.CanRetry);
        Assert.True(viewModel.HasStatus);
        Assert.Null(viewModel.VerifiedConversation);
        Assert.Equal([address], runtime.Inputs);
    }

    [Fact]
    public async Task Retry_ReusesExactInputAndAcceptsOnlyTypedVerifiedTarget()
    {
        const string invitation = "deepinvite:exact-canonical-placeholder";
        var target = Target();
        var responses = new Queue<ContactImportAndResolveResult>(
            [Pending(), Verified(target)]);
        var runtime = new StubRuntime(_ => responses.Dequeue());
        var viewModel = new NewConversationViewModel(runtime) { AddressInput = invitation };

        await viewModel.ResolveAsync();
        await viewModel.ResolveAsync();

        Assert.Equal([invitation, invitation], runtime.Inputs);
        Assert.Equal(ArbitraryContactUiState.Verified, viewModel.State);
        Assert.Same(target, viewModel.VerifiedConversation);
        Assert.True(viewModel.HasVerifiedConversation);
        Assert.False(viewModel.CanResolve);
        Assert.False(viewModel.CanRetry);
    }

    [Fact]
    public async Task InvalidCanonicalInput_IsRejectedWithoutInventingConversation()
    {
        var runtime = new StubRuntime(_ => throw new ContactAddressImportException(
            ContactAddressImportFailure.NonCanonicalOrUnsupported,
            "invalid"));
        var viewModel = new NewConversationViewModel(runtime) { AddressInput = "05legacy" };

        await viewModel.ResolveAsync();

        Assert.Equal(ArbitraryContactUiState.InputRejected, viewModel.State);
        Assert.Contains("canonical permanent deep1", viewModel.ErrorMessage,
            StringComparison.Ordinal);
        Assert.Null(viewModel.VerifiedConversation);
    }

    [Fact]
    public async Task UnverifiedOutcomeCannotExposeConversationTarget()
    {
        var malformed = Pending() with { VerifiedConversation = Target() };
        var viewModel = new NewConversationViewModel(
            new StubRuntime(_ => malformed))
        {
            AddressInput = "deep1exact-canonical-placeholder",
        };

        await viewModel.ResolveAsync();

        Assert.Equal(ArbitraryContactUiState.FailClosed, viewModel.State);
        Assert.Null(viewModel.VerifiedConversation);
        Assert.Contains("несогласованное состояние ContactV1", viewModel.StatusMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangingInputClearsTerminalOutcomeForANewCanonicalAttempt()
    {
        var runtime = new StubRuntime(_ => new ContactImportAndResolveResult(
            PendingContactAddressWriteDisposition.Added,
            ContactResolveQueueState.Terminal,
            "Не найдено",
            "Контакт не найден.",
            CanRetry: false,
            ContactResolverDisposition.NotFound,
            ContactResolverRetryClassification.Terminal));
        var viewModel = new NewConversationViewModel(runtime)
        {
            AddressInput = "deep1first-canonical-placeholder",
        };

        await viewModel.ResolveAsync();
        Assert.Equal(ArbitraryContactUiState.Terminal, viewModel.State);
        Assert.False(viewModel.CanResolve);

        viewModel.AddressInput = "deep1second-canonical-placeholder";

        Assert.Equal(ArbitraryContactUiState.Idle, viewModel.State);
        Assert.True(viewModel.CanResolve);
        Assert.False(viewModel.HasStatus);
    }

    [Fact]
    public async Task MissingLocalStoreIsNonNetworkStartupError()
    {
        var viewModel = new NewConversationViewModel(
            new StubRuntime(_ => throw new IOException("unavailable")))
        {
            AddressInput = "deep1exact-canonical-placeholder",
        };

        await viewModel.ResolveAsync();

        Assert.Equal(ArbitraryContactUiState.LocalUnavailable, viewModel.State);
        Assert.Contains("Интернет для запуска аккаунта не требуется", viewModel.ErrorMessage,
            StringComparison.Ordinal);
        Assert.Null(viewModel.VerifiedConversation);
    }

    [Fact]
    public async Task DirectRuntimeUnavailableKeepsVerifiedTarget()
    {
        var target = Target();
        var viewModel = new NewConversationViewModel(new StubRuntime(_ => Verified(target)))
        {
            AddressInput = "deep1exact-canonical-placeholder",
        };

        await viewModel.ResolveAsync();

        viewModel.ReportDirectRuntimeUnavailable();

        Assert.Equal(ArbitraryContactUiState.Verified, viewModel.State);
        Assert.Same(target, viewModel.VerifiedConversation);
        Assert.Contains("новый модуль личных сообщений не активирован", viewModel.StatusMessage,
            StringComparison.Ordinal);
    }

    private static ContactImportAndResolveResult Pending() => new(
        PendingContactAddressWriteDisposition.Added,
        ContactResolveQueueState.PendingRetry,
        ContactResolvePendingStatus.Title,
        ContactResolvePendingStatus.PrivacyRouteUnavailable,
        CanRetry: true,
        ContactResolverDisposition.TransportUnavailable,
        ContactResolverRetryClassification.RetrySameExactRequest);

    private static ContactImportAndResolveResult Verified(
        VerifiedDirectConversationTarget target) => new(
        PendingContactAddressWriteDisposition.Added,
        ContactResolveQueueState.Verified,
        "Контакт подтверждён",
        "Контакт подтверждён.",
        CanRetry: false,
        ContactResolverDisposition.Verified,
        ContactResolverRetryClassification.None,
        target);

    private static VerifiedDirectConversationTarget Target() => new(
        Deep.Client.Shared.Domain.ContactV1.ContactRelationshipId32.FromBytes(
            Enumerable.Repeat((byte)0x31, 32).ToArray()),
        Deep.Client.Shared.Domain.ContactV1.ContactConversationId32.FromBytes(
            Enumerable.Repeat((byte)0x42, 32).ToArray()));

    private sealed class StubRuntime(
        Func<string, ContactImportAndResolveResult> resolve) : IDeepContactRuntimeAccessor
    {
        public List<string> Inputs { get; } = [];

        public Task<string> GetPermanentDeepIdAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult("deep1unused");

        public Task<ContactImportAndResolveResult> ImportAndEnqueueResolveAsync(
            string input,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Inputs.Add(input);
            return Task.FromResult(resolve(input));
        }
    }
}
