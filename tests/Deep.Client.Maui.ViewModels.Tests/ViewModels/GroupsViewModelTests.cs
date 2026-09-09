using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Core.ViewModels;

namespace Deep.Client.Maui.ViewModels.Tests.ViewModels;

public sealed class GroupsViewModelTests
{
    private const string DidAlice = "deep1alicecanonical";
    private const string DidBob = "deep1bobcanonical";

    [Fact]
    public async Task UnavailableComposerDisablesMutationsAndShowsPreciseStatus()
    {
        var composer = new RecordingComposer
        {
            Readiness = new GroupV1ComposerReadiness(
                GroupV1ComposerAvailability.ContactVerificationUnavailable,
                "ContactV1 verifier unavailable")
        };
        var viewModel = new GroupsViewModel(composer)
        {
            GroupName = "Closed",
            MemberAddress = DidAlice
        };

        await viewModel.InitializeAsync();

        Assert.False(viewModel.CanCreateGroups);
        Assert.False(viewModel.CanCreateGroup);
        Assert.False(viewModel.CanAddMember);
        Assert.Equal("ContactV1 verifier unavailable", viewModel.ComposerStatus);

        await viewModel.CreateGroupFromComposerAsync();
        Assert.Equal("ContactV1 verifier unavailable", viewModel.ErrorMessage);
        Assert.Equal(0, composer.CreateCalls);
    }

    [Fact]
    public async Task DraftMemberIsAddedOnlyAfterComposerVerification()
    {
        var composer = new RecordingComposer();
        composer.Verified[DidAlice] = new GroupV1DraftMember(DidAlice, "Alice");
        var viewModel = new GroupsViewModel(composer);
        await viewModel.InitializeAsync();
        viewModel.MemberAddress = DidAlice;

        await viewModel.AddDraftMemberAsync();

        Assert.Equal([DidAlice], composer.VerifyInputs);
        var member = Assert.Single(viewModel.DraftMembers);
        Assert.Equal(DidAlice, member.CanonicalAddress);
        Assert.Equal("Alice", member.DisplayName);
        Assert.Equal("Участник проверен через ContactV1 и добавлен в черновик.",
            viewModel.OperationStatus);
    }

    [Fact]
    public async Task DuplicateCanonicalAddressDoesNotDuplicateDraft()
    {
        var composer = new RecordingComposer();
        composer.Verified[DidAlice] = new GroupV1DraftMember(DidAlice, "Alice");
        var viewModel = new GroupsViewModel(composer);
        await viewModel.InitializeAsync();

        viewModel.MemberAddress = DidAlice;
        await viewModel.AddDraftMemberAsync();
        viewModel.MemberAddress = DidAlice;
        await viewModel.AddDraftMemberAsync();

        Assert.Single(viewModel.DraftMembers);
        Assert.Equal(2, composer.VerifyInputs.Count);
    }

    [Fact]
    public async Task ContactVerificationFailureDoesNotMutateDraft()
    {
        var composer = new RecordingComposer
        {
            VerifyFailure = new GroupV1ComposerException(
                "contact-not-active",
                "Контакт не проверен")
        };
        var viewModel = new GroupsViewModel(composer);
        await viewModel.InitializeAsync();
        viewModel.MemberAddress = DidAlice;

        await viewModel.AddDraftMemberAsync();

        Assert.Empty(viewModel.DraftMembers);
        Assert.Equal("Контакт не проверен", viewModel.ErrorMessage);
        Assert.Equal(DidAlice, viewModel.MemberAddress);
    }

    [Fact]
    public async Task CreateUsesVerifiedDraftAndLeavesExplicitFanoutHandoff()
    {
        var composer = new RecordingComposer();
        composer.Verified[DidAlice] = new GroupV1DraftMember(DidAlice, "Alice");
        composer.Verified[DidBob] = new GroupV1DraftMember(DidBob, "Bob");
        composer.CreateResult = new GroupV1ComposerGroup(
            new string('a', 64), "Core", 1, 2, false);
        var viewModel = new GroupsViewModel(composer);
        await viewModel.InitializeAsync();
        viewModel.MemberAddress = DidAlice;
        await viewModel.AddDraftMemberAsync();
        viewModel.MemberAddress = DidBob;
        await viewModel.AddDraftMemberAsync();
        viewModel.GroupName = " Core ";

        var created = await viewModel.CreateGroupFromComposerAsync();

        Assert.NotNull(created);
        Assert.Equal("Core", composer.CreatedName);
        Assert.Equal([DidAlice, DidBob], composer.CreatedMembers);
        Assert.Empty(viewModel.DraftMembers);
        Assert.Empty(viewModel.GroupName);
        Assert.Contains("ожидают GroupV1 fanout", viewModel.OperationStatus);
        Assert.Contains("проверено участников: 2", viewModel.OperationStatus);
        Assert.Equal(2, Assert.Single(viewModel.Groups).PendingInvitationCount);
    }

    [Fact]
    public async Task CreateFailurePreservesNameAndVerifiedDraft()
    {
        var composer = new RecordingComposer
        {
            CreateFailure = new GroupV1ComposerException(
                "custody-unavailable",
                "Custody недоступен")
        };
        composer.Verified[DidAlice] = new GroupV1DraftMember(DidAlice, "Alice");
        var viewModel = new GroupsViewModel(composer);
        await viewModel.InitializeAsync();
        viewModel.MemberAddress = DidAlice;
        await viewModel.AddDraftMemberAsync();
        viewModel.GroupName = "Core";

        var created = await viewModel.CreateGroupFromComposerAsync();

        Assert.Null(created);
        Assert.Equal("Custody недоступен", viewModel.ErrorMessage);
        Assert.Equal("Core", viewModel.GroupName);
        Assert.Single(viewModel.DraftMembers);
    }

    [Fact]
    public async Task RefreshDoesNotResetUnchangedGroupProjection()
    {
        var composer = new RecordingComposer
        {
            Groups = [new GroupV1ComposerGroup(new string('b', 64), "Existing", 3, 0, false)]
        };
        var viewModel = new GroupsViewModel(composer);
        await viewModel.InitializeAsync();
        var changes = 0;
        viewModel.Groups.CollectionChanged += (_, _) => changes++;

        await viewModel.RefreshAsync();

        Assert.Equal(0, changes);
        Assert.Equal("Existing", Assert.Single(viewModel.Groups).Name);
    }

    [Fact]
    public void ComposerSourceHasNoLegacySessionIdentityPath()
    {
        var root = FindRepositoryRoot();
        var sources = new[]
        {
            Path.Combine(root, "src", "Deep.Client.Maui.Core", "ViewModels", "GroupsViewModel.cs"),
            Path.Combine(root, "src", "Deep.Client.Maui", "Pages", "GroupsPage.xaml"),
            Path.Combine(root, "src", "Deep.Client.Maui", "Pages", "GroupsPage.xaml.cs"),
            Path.Combine(root, "src", "Deep.Client.Maui", "Services", "GroupV1", "AccountScopedGroupV1Composer.cs")
        };
        var text = string.Join('\n', sources.Select(File.ReadAllText));

        Assert.DoesNotContain("SessionId", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SessionIdContactMailboxOnboarding", text, StringComparison.Ordinal);
        Assert.DoesNotContain("IContactMailboxOnboarding", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateGroupScaffoldAsync", text, StringComparison.Ordinal);
        Assert.DoesNotContain("MemberSessionId", text, StringComparison.Ordinal);
        Assert.Contains("IDeepGroupV1Runtime", text, StringComparison.Ordinal);
        Assert.Contains("ContactResolverTrustedVerifier", text, StringComparison.Ordinal);
        Assert.Contains("group-v1-invitation-transport-unavailable", text,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))
                && Directory.Exists(Path.Combine(directory.FullName, "src", "Deep.Client.Maui")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("deep-client-maui repository root was not found.");
    }

    private sealed class RecordingComposer : IGroupV1Composer
    {
        public GroupV1ComposerReadiness Readiness { get; set; } = new(
            GroupV1ComposerAvailability.Ready,
            "GroupV1 ready");
        public Dictionary<string, GroupV1DraftMember> Verified { get; } =
            new(StringComparer.Ordinal);
        public List<string> VerifyInputs { get; } = [];
        public Exception? VerifyFailure { get; set; }
        public Exception? CreateFailure { get; set; }
        public GroupV1ComposerGroup CreateResult { get; set; } = new(
            new string('c', 64), "Group", 1, 0, false);
        public IReadOnlyList<GroupV1ComposerGroup> Groups { get; set; } = [];
        public int CreateCalls { get; private set; }
        public string? CreatedName { get; private set; }
        public IReadOnlyList<string> CreatedMembers { get; private set; } = [];

        public ValueTask<GroupV1ComposerReadiness> GetReadinessAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Readiness);
        }

        public ValueTask<GroupV1DraftMember> VerifyMemberAsync(
            string canonicalAddress,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifyInputs.Add(canonicalAddress);
            if (VerifyFailure is not null)
            {
                return ValueTask.FromException<GroupV1DraftMember>(VerifyFailure);
            }
            return ValueTask.FromResult(Verified[canonicalAddress]);
        }

        public ValueTask<GroupV1ComposerGroup> CreateAsync(
            string groupName,
            IReadOnlyList<GroupV1DraftMember> members,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCalls++;
            CreatedName = groupName;
            CreatedMembers = members.Select(static member => member.CanonicalAddress).ToArray();
            return CreateFailure is null
                ? ValueTask.FromResult(CreateResult)
                : ValueTask.FromException<GroupV1ComposerGroup>(CreateFailure);
        }

        public ValueTask<IReadOnlyList<GroupV1ComposerGroup>> ReadAllAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Groups);
        }
    }
}
