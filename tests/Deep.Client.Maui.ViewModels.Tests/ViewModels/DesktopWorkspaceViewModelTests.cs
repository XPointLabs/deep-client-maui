using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;

namespace Deep.Client.Maui.ViewModels.Tests.ViewModels;

public sealed class DesktopWorkspaceViewModelTests
{
    [Fact]
    public async Task SplitViewKeepsConversationListAndDetailVisibleAt680Dip()
    {
        var runtime = ClientRuntime.CreateStubbed(
            clock: new FrozenClock(DateTimeOffset.Parse("2026-07-13T00:00:00Z")));
        await runtime.Accounts.RegisterAsync("Owner");
        var remote = SessionId.CreateNew();
        await runtime.Conversations.GetOrCreateOneToOneAsync(remote, "Alice");
        var workspace = CreateWorkspace(runtime);

        await workspace.InitializeAsync();
        workspace.UpdateWindowWidth(DesktopWorkspaceViewModel.SplitBreakpoint);
        var activated = await workspace.ActivateConversationAsync(ConversationId.ForOneToOne(remote));

        Assert.True(activated);
        Assert.True(workspace.IsSplitView);
        Assert.True(workspace.IsConversationListVisible);
        Assert.True(workspace.IsDetailPaneVisible);
        Assert.True(workspace.IsDirectDetail);
    }

    [Fact]
    public async Task NarrowBackAndResizeRetainSelectionAndViewModelInstances()
    {
        var runtime = ClientRuntime.CreateStubbed();
        await runtime.Accounts.RegisterAsync("Owner");
        var remote = SessionId.CreateNew();
        await runtime.Conversations.GetOrCreateOneToOneAsync(remote, "Alice");
        var workspace = CreateWorkspace(runtime);

        await workspace.InitializeAsync();
        workspace.UpdateWindowWidth(679);
        await workspace.ActivateConversationAsync(ConversationId.ForOneToOne(remote));
        var direct = workspace.DirectChat;
        var group = workspace.GroupChat;

        Assert.False(workspace.IsConversationListVisible);
        Assert.True(workspace.IsDetailPaneVisible);
        Assert.True(workspace.IsBackButtonVisible);

        workspace.UpdateWindowWidth(680);
        workspace.UpdateWindowWidth(679);

        Assert.Equal(ConversationId.ForOneToOne(remote), workspace.SelectedConversation?.Id);
        Assert.Same(direct, workspace.DirectChat);
        Assert.Same(group, workspace.GroupChat);

        workspace.ShowConversationList();

        Assert.True(workspace.IsConversationListVisible);
        Assert.False(workspace.IsDetailPaneVisible);
        Assert.Equal(ConversationId.ForOneToOne(remote), workspace.SelectedConversation?.Id);
    }

    [Fact]
    public async Task PerConversationComposerStateDoesNotLeakAcrossDirectChats()
    {
        var runtime = ClientRuntime.CreateStubbed();
        await runtime.Accounts.RegisterAsync("Owner");
        var alice = SessionId.CreateNew();
        var bob = SessionId.CreateNew();
        var aliceConversation = await runtime.Conversations.GetOrCreateOneToOneAsync(alice, "Alice");
        var bobConversation = await runtime.Conversations.GetOrCreateOneToOneAsync(bob, "Bob");
        var workspace = CreateWorkspace(runtime);
        await workspace.InitializeAsync();

        await workspace.ActivateConversationAsync(aliceConversation.Id);
        var aliceChat = workspace.DirectChat;
        aliceChat.Draft = "alice draft";
        aliceChat.StageAttachment(AttachmentMetadata.Local("alice.txt", "text/plain", 12));

        await workspace.ActivateConversationAsync(bobConversation.Id);
        var bobChat = workspace.DirectChat;

        Assert.NotSame(aliceChat, bobChat);
        Assert.Empty(bobChat.Draft);
        Assert.False(bobChat.HasStagedAttachments);

        await workspace.ActivateConversationAsync(aliceConversation.Id);
        Assert.Same(aliceChat, workspace.DirectChat);
        Assert.Equal("alice draft", workspace.DirectChat.Draft);
        Assert.True(workspace.DirectChat.HasStagedAttachments);
    }

    [Fact]
    public async Task ResetSessionImmediatelyDropsSelectedConversationAndComposerState()
    {
        var runtime = ClientRuntime.CreateStubbed();
        await runtime.Accounts.RegisterAsync("Owner");
        var remote = SessionId.CreateNew();
        var conversation = await runtime.Conversations.GetOrCreateOneToOneAsync(remote, "Alice");
        var workspace = CreateWorkspace(runtime);
        await workspace.InitializeAsync();
        await workspace.ActivateConversationAsync(conversation.Id);
        workspace.DirectChat.Draft = "private draft";

        workspace.ResetSession();

        Assert.Null(workspace.SelectedConversation);
        Assert.True(workspace.IsEmptyDetail);
        Assert.Empty(workspace.ConversationList.Conversations);
        Assert.Empty(workspace.DirectChat.Draft);
        Assert.False(workspace.DirectChat.HasStagedAttachments);
    }

    [Fact]
    public async Task DirectAndGroupSwitchingPreservesLeftListAndPerConversationDraft()
    {
        var runtime = ClientRuntime.CreateStubbed();
        var owner = await runtime.Accounts.RegisterAsync("Owner");
        var remote = SessionId.CreateNew();
        var directConversation = await runtime.Conversations.GetOrCreateOneToOneAsync(remote, "Alice");
        var groupConversation = await runtime.Conversations.CreateGroupScaffoldAsync(
            owner.SessionId,
            "Team",
            [],
            CancellationToken.None);
        var workspace = CreateWorkspace(runtime);

        await workspace.InitializeAsync();
        workspace.UpdateWindowWidth(900);
        await workspace.ActivateConversationAsync(directConversation.Id);
        workspace.DirectChat.Draft = "direct draft";

        await workspace.ActivateConversationAsync(groupConversation.Id);
        workspace.GroupChat.Draft = "group draft";
        await workspace.ActivateConversationAsync(directConversation.Id);

        Assert.True(workspace.IsConversationListVisible);
        Assert.True(workspace.IsDetailPaneVisible);
        Assert.True(workspace.IsDirectDetail);
        Assert.Equal("direct draft", workspace.DirectChat.Draft);
        Assert.Equal(groupConversation.Id, workspace.ConversationList.Conversations.Single(item => item.Kind == ConversationKind.GroupV2).Id);
    }

    private static DesktopWorkspaceViewModel CreateWorkspace(ClientRuntime runtime)
        => new(
            runtime,
            () => new ConversationsViewModel(runtime),
            () => new ChatViewModel(runtime),
            () => new GroupChatViewModel(runtime));
}
