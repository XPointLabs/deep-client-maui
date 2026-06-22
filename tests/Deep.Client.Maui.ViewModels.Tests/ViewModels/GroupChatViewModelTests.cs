using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.ViewModels.Tests.ViewModels;

public sealed class GroupChatViewModelTests
{
    [Fact]
    public async Task SendCommandAppendsGroupMessage()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var group = await runtime.Conversations.CreateGroupScaffoldAsync(owner.SessionId, "Test Group", [], CancellationToken.None);

        var viewModel = new GroupChatViewModel(runtime);
        await viewModel.OpenFromRouteAsync(group.Id.Value, group.Name);
        viewModel.Draft = "hello group";

        await viewModel.SendCommand.ExecuteAsync();

        Assert.Single(viewModel.Messages);
        Assert.Equal("hello group", viewModel.Messages[0].Body);
        Assert.True(string.IsNullOrWhiteSpace(viewModel.Draft));
    }

    [Fact]
    public async Task PickAttachmentsCommandAllowsAttachmentOnlyGroupMessage()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var group = await runtime.Conversations.CreateGroupScaffoldAsync(owner.SessionId, "Files", [], CancellationToken.None);
        var picker = new FakeAttachmentPicker();

        var viewModel = new GroupChatViewModel(runtime, picker);
        await viewModel.OpenFromRouteAsync(group.Id.Value, group.Name);

        await viewModel.PickAttachmentsAsync();

        Assert.True(viewModel.HasStagedAttachments);
        Assert.Equal("group-note.txt", viewModel.StagedAttachmentSummary);
        Assert.True(viewModel.SendCommand.CanExecute(null));

        await viewModel.SendAsync();

        Assert.Single(viewModel.Messages);
        Assert.Equal("[Вложение]", viewModel.Messages[0].Body);
        Assert.True(viewModel.Messages[0].HasAttachments);
        Assert.False(viewModel.HasStagedAttachments);
    }

    [Fact]
    public async Task AddPromoteAndRemoveMemberUpdatesGroupMembers()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var group = await runtime.Conversations.CreateGroupScaffoldAsync(owner.SessionId, "Admins", [], CancellationToken.None);
        var member = "05" + new string('b', 64);

        var viewModel = new GroupChatViewModel(runtime);
        await viewModel.OpenFromRouteAsync(group.Id.Value, group.Name);

        viewModel.MemberSessionId = member;
        await viewModel.AddMemberCommand.ExecuteAsync();
        Assert.Contains(viewModel.Members, item => item.SessionId.Value == member && item.Role == Deep.Client.Shared.Domain.GroupMemberRole.Standard);

        viewModel.MemberSessionId = member;
        await viewModel.PromoteMemberCommand.ExecuteAsync();
        Assert.Contains(viewModel.Members, item => item.SessionId.Value == member && item.Role == Deep.Client.Shared.Domain.GroupMemberRole.Admin);

        viewModel.MemberSessionId = member;
        await viewModel.RemoveMemberCommand.ExecuteAsync();
        Assert.DoesNotContain(viewModel.Members, item => item.SessionId.Value == member);
    }

    [Fact]
    public async Task DemoteLastAdminShowsStatusError()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var group = await runtime.Conversations.CreateGroupScaffoldAsync(owner.SessionId, "Admins", [], CancellationToken.None);

        var viewModel = new GroupChatViewModel(runtime);
        await viewModel.OpenFromRouteAsync(group.Id.Value, group.Name);

        viewModel.MemberSessionId = owner.SessionId.Value;
        await viewModel.DemoteMemberCommand.ExecuteAsync();

        Assert.Equal("Нельзя понизить последнего администратора.", viewModel.StatusMessage);
        Assert.True(viewModel.IsStatusError);
    }

    [Fact]
    public async Task RefreshWithoutGroupChangesDoesNotResetMessagesOrMembers()
    {
        var runtime = ClientRuntime.CreateStubbed(clock: new FrozenClock(DateTimeOffset.Parse("2026-05-28T00:00:00Z")));
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var group = await runtime.Conversations.CreateGroupScaffoldAsync(owner.SessionId, "Stable", [], CancellationToken.None);

        var viewModel = new GroupChatViewModel(runtime);
        await viewModel.OpenFromRouteAsync(group.Id.Value, group.Name);
        var messageChanges = 0;
        var memberChanges = 0;
        viewModel.Messages.CollectionChanged += (_, _) => messageChanges++;
        viewModel.Members.CollectionChanged += (_, _) => memberChanges++;

        await viewModel.RefreshAsync();

        Assert.Equal(0, messageChanges);
        Assert.Equal(0, memberChanges);
        Assert.Single(viewModel.Members);
    }

    private sealed class FakeAttachmentPicker : IAttachmentPickerService
    {
        public Task<IReadOnlyList<AttachmentMetadata>> PickAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AttachmentMetadata>>(
            [
                AttachmentMetadata.Local("group-note.txt", "text/plain", 256)
            ]);
    }
}
