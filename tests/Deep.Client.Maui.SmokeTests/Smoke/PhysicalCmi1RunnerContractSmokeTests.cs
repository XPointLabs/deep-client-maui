namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class PhysicalCmi1RunnerContractSmokeTests
{
    [Fact]
    public void InvitationAutomationSurfaceExistsOnlyInPhysicalBuildAndDoesNotLog()
    {
        var page = Read("src", "Deep.Client.Maui", "Pages",
            "StartConversationPage.xaml.cs");
        var guard = page.IndexOf("#if DEEP_PHYSICAL_E2E", StringComparison.Ordinal);
        var visible = page.IndexOf("AccountIdLabel.IsVisible", guard,
            StringComparison.Ordinal);
        var fallback = page.IndexOf("#else", visible, StringComparison.Ordinal);

        Assert.True(guard >= 0);
        Assert.True(visible > guard);
        Assert.True(fallback > visible);
        Assert.DoesNotContain("Console.", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Debug.Write", page, StringComparison.Ordinal);
    }

    [Fact]
    public void PayloadGroupAndRestartUseVerifiedCmi1AndExactCounts()
    {
        var runner = Read("tests", "Deep.Client.Maui.UiTests",
            "StrictCrossPlatformUiTests.cs");

        Assert.Contains("ContactMailboxInvitationService.ParseAndVerify(", runner,
            StringComparison.Ordinal);
        Assert.Contains("ReadAndroidInvitation(android, options)", runner,
            StringComparison.Ordinal);
        Assert.Contains("ReadWindowsInvitation(windows)", runner,
            StringComparison.Ordinal);
        Assert.Contains("AddAndroidContact(android, options, windowsInvitation.Text)",
            runner, StringComparison.Ordinal);
        Assert.Contains("AddWindowsContact(windows, androidInvitation.Text)", runner,
            StringComparison.Ordinal);
        Assert.Contains("\"StartConversation.AccountId\", \"StartConversation.Close\"",
            runner, StringComparison.Ordinal);
        Assert.Contains("windowsInvitation?.Text", runner, StringComparison.Ordinal);
        Assert.Contains("AssertDirectMessagesExactlyOnceOnBothClients(", runner,
            StringComparison.Ordinal);
        Assert.Contains("AssertGroupMessagesExactlyOnceOnBothClients(", runner,
            StringComparison.Ordinal);
        Assert.DoesNotContain("evidence.AddSafeValue(\"androidCmi1", runner,
            StringComparison.Ordinal);
        Assert.DoesNotContain("evidence.AddSafeValue(\"windowsCmi1", runner,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InstalledMsixApprovalIsSignedPolicyBoundAndLegacyModeRemainsAvailable()
    {
        var issuer = Read("eng", "Issue-AndroidLabPolicy.ps1");
        var wrapper = Read("eng", "Invoke-PhysicalMau2CrossPlatform.ps1");
        var session = Read("tests", "Deep.Client.Maui.UiTests",
            "WindowsUiSmokeTests.cs");

        Assert.Contains("windowsUatApprovalRelativePath", issuer,
            StringComparison.Ordinal);
        Assert.Contains("windowsUatApprovalSha256", issuer,
            StringComparison.Ordinal);
        Assert.Contains("Resolve-InstalledWindowsUatPackage", wrapper,
            StringComparison.Ordinal);
        Assert.Contains("Get-AppxPackage -Name", wrapper, StringComparison.Ordinal);
        Assert.Contains("DEEP_E2E_WINDOWS_UAT_APPROVAL", wrapper,
            StringComparison.Ordinal);
        Assert.Contains("if ([string]::IsNullOrWhiteSpace($WindowsUatApprovalTuplePath))",
            wrapper, StringComparison.Ordinal);
        Assert.Contains("WindowsUatPackageApproval.Current", session,
            StringComparison.Ordinal);
        Assert.Contains("policy.WindowsExeSha256", session, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([WorkspaceRoot(), .. parts]));

    private static string WorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "Deep.Client.Maui.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
