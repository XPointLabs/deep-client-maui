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
}
