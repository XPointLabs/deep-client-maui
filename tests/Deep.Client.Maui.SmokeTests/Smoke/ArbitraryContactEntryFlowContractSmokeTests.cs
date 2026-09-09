namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class ArbitraryContactEntryFlowContractSmokeTests
{
    [Fact]
    public void EntryPagesUseContactV1AndNeverParseLegacySessionIds()
    {
        var newPage = Read("src", "Deep.Client.Maui", "Pages",
            "NewConversationPage.xaml.cs");
        var startPage = Read("src", "Deep.Client.Maui", "Pages",
            "StartConversationPage.xaml.cs");
        var viewModel = Read("src", "Deep.Client.Maui.Core", "ViewModels",
            "NewConversationViewModel.cs");
        var activation = Read("src", "Deep.Client.Maui.Core", "Navigation",
            "IVerifiedDirectConversationActivationTarget.cs");

        Assert.Contains("NewConversationViewModel", newPage, StringComparison.Ordinal);
        Assert.Contains("IVerifiedDirectConversationActivationTarget", newPage,
            StringComparison.Ordinal);
        Assert.Contains("IDeepContactRuntimeAccessor", viewModel, StringComparison.Ordinal);
        Assert.Contains("ContactResolveQueueState.PendingRetry", viewModel,
            StringComparison.Ordinal);
        Assert.Contains("VerifiedDirectConversationTarget", activation,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SessionId.Parse", newPage, StringComparison.Ordinal);
        Assert.DoesNotContain("SessionId.Parse", startPage, StringComparison.Ordinal);
        Assert.DoesNotContain("SessionIdContactMailboxOnboarding", newPage,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SessionIdContactMailboxOnboarding", startPage,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ShellRouteCatalog.Chat", newPage,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PendingAndRetryRemainVisibleInsteadOfClosingThePage()
    {
        var xaml = Read("src", "Deep.Client.Maui", "Pages",
            "NewConversationPage.xaml");
        var code = Read("src", "Deep.Client.Maui", "Pages",
            "NewConversationPage.xaml.cs");

        Assert.Contains("AutomationId=\"NewConversation.Status\"", xaml,
            StringComparison.Ordinal);
        Assert.Contains("AutomationId=\"NewConversation.SessionId\"", xaml,
            StringComparison.Ordinal);
        Assert.Contains("x:DataType=\"vm:NewConversationViewModel\"", xaml,
            StringComparison.Ordinal);
        Assert.Contains("AutomationId=\"NewConversation.Retry\"", xaml,
            StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding AddressInput, Mode=TwoWay}\"", xaml,
            StringComparison.Ordinal);
        Assert.Contains("viewModel.ReportDirectRuntimeUnavailable()", code,
            StringComparison.Ordinal);
        Assert.DoesNotContain("DisplayAlertAsync", code, StringComparison.Ordinal);
        Assert.DoesNotContain("await NavigateBackToConversationsAsync();\n        }\n        catch",
            code.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([WorkspaceRoot(), .. parts]));

    private static string WorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "Deep.Client.Maui.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
