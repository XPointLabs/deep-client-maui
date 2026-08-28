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
    public void GroupTextIsASeparatePrePayloadPhaseWithColdRestartAndSanitizedEvidence()
    {
        var runner = Read("tests", "Deep.Client.Maui.UiTests",
            "StrictCrossPlatformUiTests.cs");
        var phases = Read("tests", "Deep.Client.Maui.UiTests",
            "Mau2PhysicalPhase.cs");

        var groupEnum = phases.IndexOf("GroupText,", StringComparison.Ordinal);
        var payloadEnum = phases.IndexOf("PayloadMatrix,", StringComparison.Ordinal);
        var groupCase = runner.IndexOf(
            "case Mau2PhysicalPhase.GroupText:", StringComparison.Ordinal);
        var payloadCase = runner.IndexOf(
            "case Mau2PhysicalPhase.PayloadMatrix:", StringComparison.Ordinal);
        Assert.True(groupEnum >= 0 && payloadEnum > groupEnum,
            "GroupText must remain ordered before PayloadMatrix.");
        Assert.True(groupCase >= 0 && payloadCase > groupCase,
            "The independently selectable GroupText dispatch must precede PayloadMatrix.");

        var start = runner.IndexOf(
            "private static void ExerciseGroupTextOnExistingProvisionedClients(",
            StringComparison.Ordinal);
        var end = runner.IndexOf(
            "private static void ExercisePrivacyFallbackOnExistingProvisionedClients(",
            start,
            StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start,
            "The bounded GroupText phase body was not found.");
        var groupBody = runner[start..end];

        Assert.Contains("ReadAndroidInvitation(android, options)", groupBody,
            StringComparison.Ordinal);
        Assert.Contains("ReadWindowsInvitation(windows)", groupBody,
            StringComparison.Ordinal);
        Assert.Contains("AddAndroidContact(", groupBody, StringComparison.Ordinal);
        Assert.Contains("AddWindowsContact(", groupBody, StringComparison.Ordinal);
        Assert.Contains("ExerciseTwoMemberGroupRoundtrip(", groupBody,
            StringComparison.Ordinal);
        Assert.Contains("AssertGroupMessagesExactlyOnceOnBothClients(", groupBody,
            StringComparison.Ordinal);
        Assert.Contains("AssertDistinctProcessIds(", groupBody,
            StringComparison.Ordinal);
        Assert.Contains("android.RequireRunningProcessId()", groupBody,
            StringComparison.Ordinal);
        Assert.Contains("groupMessagesPersistedExactlyOnceAcrossColdRestart", groupBody,
            StringComparison.Ordinal);
        Assert.Contains("payloadAndFilePickerStepsNotInvoked", groupBody,
            StringComparison.Ordinal);

        Assert.DoesNotContain("CaptureFilePickerInvocationContext", groupBody,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ExchangeAndroidDocument(", groupBody,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ExchangeWindowsDocument(", groupBody,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ExchangeInlineImagesBothDirections(", groupBody,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ExchangeVoiceMessagesBothDirections(", groupBody,
            StringComparison.Ordinal);
        Assert.DoesNotContain("evidence.AddSafeValue(\"androidCmi1", groupBody,
            StringComparison.Ordinal);
        Assert.DoesNotContain("evidence.AddSafeValue(\"windowsCmi1", groupBody,
            StringComparison.Ordinal);

        var payloadStart = runner.IndexOf(
            "private static void ExercisePayloadMatrixOnExistingProvisionedClients(",
            StringComparison.Ordinal);
        Assert.True(payloadStart >= 0 && start > payloadStart,
            "PayloadMatrix must remain independently implemented.");
        var payloadBody = runner[payloadStart..start];
        Assert.Contains("ExchangeAndroidDocument(", payloadBody,
            StringComparison.Ordinal);
        Assert.Contains("ExchangeWindowsDocument(", payloadBody,
            StringComparison.Ordinal);
        Assert.Contains("ExerciseTwoMemberGroupRoundtrip(", payloadBody,
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
