using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Features;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.ViewModels.Tests.ViewModels;

public sealed class GroupsViewModelTests
{
    [Fact]
    public async Task GroupsRemainBehindFeatureFlag()
    {
        var runtime = ClientRuntime.CreateStubbed(
            new ClientFeatureFlags(GroupsV2Enabled: false),
            new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var viewModel = new GroupsViewModel(runtime) { GroupName = "Disabled" };

        var group = await viewModel.CreateGroupAsync(owner.SessionId, []);

        Assert.Null(group);
        Assert.Contains("feature flag", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task CreateGroupIsConsistentAcrossClientRuntimes()
    {
        var frozen = new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z"));

        var windowsRuntime = ClientRuntime.CreateStubbed(clock: frozen);
        var windowsOwner = await windowsRuntime.Accounts.RegisterAsync("WindowsOwner");
        var windowsVm = new GroupsViewModel(windowsRuntime) { GroupName = "Core team" };

        var androidRuntime = ClientRuntime.CreateStubbed(clock: frozen);
        var androidOwner = await androidRuntime.Accounts.RegisterAsync("AndroidOwner");
        var androidVm = new GroupsViewModel(androidRuntime) { GroupName = "Core team" };

        var windowsGroup = await windowsVm.CreateGroupAsync(windowsOwner.SessionId, []);
        var androidGroup = await androidVm.CreateGroupAsync(androidOwner.SessionId, []);

        Assert.NotNull(windowsGroup);
        Assert.NotNull(androidGroup);
        Assert.StartsWith("03", windowsGroup!.Id.Value);
        Assert.StartsWith("03", androidGroup!.Id.Value);
        Assert.Equal(1, windowsVm.Groups.Single().MemberCount);
        Assert.Equal(1, androidVm.Groups.Single().MemberCount);
        Assert.Equal(1, windowsVm.Groups.Single().AdminCount);
        Assert.Equal(1, androidVm.Groups.Single().AdminCount);
    }

    [Fact]
    public async Task RemoveMemberUpdatesCollectionState()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var member = SessionId.CreateNew();

        var viewModel = new GroupsViewModel(runtime) { GroupName = "Admins" };
        var group = await viewModel.CreateGroupAsync(owner.SessionId, [member]);
        var removed = await viewModel.RemoveMemberAsync(group!.Id, owner.SessionId, member);

        Assert.NotNull(removed);
        Assert.Single(viewModel.Groups);
        Assert.Equal(1, viewModel.Groups[0].MemberCount);
    }

    [Fact]
    public async Task CreateCommandCreatesGroupForActiveAccount()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        await runtime.Accounts.RegisterAsync("Owner");

        var viewModel = new GroupsViewModel(runtime)
        {
            GroupName = "UI Group"
        };

        await viewModel.CreateCommand.ExecuteAsync();

        Assert.Single(viewModel.Groups);
        Assert.Equal("UI Group", viewModel.Groups[0].Name);
        Assert.True(string.IsNullOrWhiteSpace(viewModel.GroupName));
    }

    [Fact]
    public async Task CreateCommandIncludesDraftMembers()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        await runtime.Accounts.RegisterAsync("Owner");
        var member = "05" + new string('c', 64);

        var viewModel = new GroupsViewModel(runtime)
        {
            GroupName = "Peer Group",
            MemberSessionId = member
        };

        await viewModel.AddDraftMemberCommand.ExecuteAsync();
        await viewModel.CreateCommand.ExecuteAsync();

        Assert.Single(viewModel.Groups);
        Assert.Equal(2, viewModel.Groups[0].MemberCount);
        Assert.Empty(viewModel.DraftMembers);
    }

    [Fact]
    public async Task AddDraftMemberOnboardsInvitationBeforeMutatingDraft()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var recipient = SessionId.Parse("05" + new string('d', 64));
        var onboarding = new RecordingContactOnboarding("protected-invitation", recipient);
        var viewModel = new GroupsViewModel(runtime, onboarding)
        {
            MemberSessionId = "protected-invitation"
        };

        await viewModel.AddDraftMemberAsync();

        Assert.Equal(1, onboarding.PrepareCalls);
        Assert.Equal(recipient, Assert.Single(viewModel.DraftMembers).SessionId);
    }

    [Fact]
    public async Task AddDraftMemberOnboardingFailureDoesNotMutateDraft()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var onboarding = new RecordingContactOnboarding(
            "expired-invitation",
            SessionId.Parse("05" + new string('e', 64)),
            fail: true);
        var viewModel = new GroupsViewModel(runtime, onboarding)
        {
            MemberSessionId = "expired-invitation"
        };

        await viewModel.AddDraftMemberAsync();

        Assert.Equal(1, onboarding.PrepareCalls);
        Assert.Empty(viewModel.DraftMembers);
        Assert.True(viewModel.HasError);
    }

    [Fact]
    public async Task DraftMembersUseKnownDisplayNameAndShortUnknownFallback()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var knownMember = SessionId.Parse("05" + new string('1', 64));
        var unknownMember = SessionId.Parse("05" + new string('2', 64));
        await runtime.Conversations.GetOrCreateOneToOneAsync(knownMember, "Alice", approve: true);
        var viewModel = new GroupsViewModel(runtime);

        viewModel.MemberSessionId = knownMember.Value;
        await viewModel.AddDraftMemberAsync();
        viewModel.MemberSessionId = unknownMember.Value;
        await viewModel.AddDraftMemberAsync();

        Assert.Equal("Alice", viewModel.DraftMembers.Single(item => item.SessionId == knownMember).DisplayName);
        Assert.Equal("052222...2222", viewModel.DraftMembers.Single(item => item.SessionId == unknownMember).DisplayName);
    }

    [Fact]
    public async Task DraftMemberDisplayNameRefreshAppliesRepeatedRenames()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var member = SessionId.Parse("05" + new string('3', 64));
        await runtime.Conversations.GetOrCreateOneToOneAsync(member, "Initial", approve: true);
        var viewModel = new GroupsViewModel(runtime) { MemberSessionId = member.Value };
        await viewModel.AddDraftMemberAsync();

        await runtime.Conversations.UpdateContactDisplayNameAsync(member, "Renamed Once");
        await viewModel.RefreshContactDisplayNamesAsync();
        Assert.Equal("Renamed Once", Assert.Single(viewModel.DraftMembers).DisplayName);

        await runtime.Conversations.UpdateContactDisplayNameAsync(member, "Renamed Twice");
        await viewModel.RefreshAsync();
        Assert.Equal("Renamed Twice", Assert.Single(viewModel.DraftMembers).DisplayName);
    }

    [Fact]
    public async Task RefreshCommandLoadsPersistedGroups()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        await runtime.Conversations.CreateGroupScaffoldAsync(owner.SessionId, "Existing", [], CancellationToken.None);

        var viewModel = new GroupsViewModel(runtime);

        await viewModel.RefreshCommand.ExecuteAsync();

        Assert.Single(viewModel.Groups);
        Assert.Equal("Existing", viewModel.Groups[0].Name);
    }

    [Fact]
    public async Task RefreshWithoutGroupChangesDoesNotResetCollection()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        await runtime.Conversations.CreateGroupScaffoldAsync(owner.SessionId, "Existing", [], CancellationToken.None);
        var viewModel = new GroupsViewModel(runtime);

        await viewModel.RefreshAsync();
        var changeCount = 0;
        viewModel.Groups.CollectionChanged += (_, _) => changeCount++;

        await viewModel.RefreshAsync();

        Assert.Equal(0, changeCount);
        Assert.Single(viewModel.Groups);
    }

    private sealed class RecordingContactOnboarding(
        string acceptedInput,
        SessionId recipient,
        bool fail = false) : IContactMailboxOnboarding
    {
        public int PrepareCalls { get; private set; }

        public bool CanAccept(string contactInput) =>
            string.Equals(contactInput, acceptedInput, StringComparison.Ordinal);

        public Task<SessionId> PrepareAsync(
            string contactInput,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrepareCalls++;
            if (!CanAccept(contactInput) || fail)
            {
                throw new InvalidDataException("unverified contact");
            }

            return Task.FromResult(recipient);
        }
    }
}
