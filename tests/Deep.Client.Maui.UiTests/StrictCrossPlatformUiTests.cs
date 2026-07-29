using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;
using Xunit.Sdk;

namespace Deep.Client.Maui.UiTests;

/// <summary>
/// Opt-in, physical Android↔Windows acceptance. This is intentionally not an emulator,
/// Appium, WinAppDriver, mocked transport, or coordinate-script lane. Every Android action
/// starts with a fresh uiautomator tree and one configured exact resource-id; every Windows
/// action is scoped to the spawned application's exact PID and one AutomationId.
/// </summary>
public sealed class StrictCrossPlatformUiTests
{
    [StrictCrossPlatformUiFact]
    public void Physical_android_and_windows_exchange_persist_and_decrypt_an_attachment()
    {
        var options = CrossPlatformOptions.Load();
        File.Delete(options.ResultPath); // A pass may never reuse evidence from an earlier invocation.
        var cleanup = new StrictCrossPlatformContracts.CleanupScope();
        var attemptCleanup = new StrictCrossPlatformContracts.AttemptCleanupState();
        WindowsDesktopGate.EnsureUnlocked();
        var evidence = new StrictCrossPlatformContracts.SanitizedEvidence();
        var android = new AndroidUiautomatorClient(options);
        var marker = StrictCrossPlatformContracts.NewMarker("attachment") + ".bin";
        var windowsToAndroid = StrictCrossPlatformContracts.NewMarker("windows-to-android");
        var androidToWindows = StrictCrossPlatformContracts.NewMarker("android-to-windows");
        StrictCrossPlatformContracts.AssertSafeMarker(marker);
        StrictCrossPlatformContracts.AssertSafeMarker(windowsToAndroid);
        StrictCrossPlatformContracts.AssertSafeMarker(androidToWindows);

        var fixtureSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(options.AttachmentFixturePath)));
        var apk = options.ReadAndValidateApkMetadata();
        var approvedPolicy = ApprovedCrossPlatformPolicy.Current ?? throw new InvalidOperationException("Approved policy was not loaded.");
        var isolatedWindowsAppData = WindowsUiSmokeTests.WindowsUiTestSession.CreateIsolatedAppDataDirectory();
        var downloadsDirectory = options.ResolveProductionDownloadsDirectory();
        var createdDownloads = new List<string>();
        cleanup.Add(() =>
        {
            foreach (var path in createdDownloads)
            {
                if (File.Exists(path)) File.Delete(path);
            }
        });
        cleanup.Add(() => { if (Directory.Exists(isolatedWindowsAppData)) Directory.Delete(isolatedWindowsAppData, recursive: true); });
        attemptCleanup.Register(
            cleanup,
            android.ClearE2ePackageData,
            () => android.DeletePushedFixture(marker));
        evidence.AddSafeValue("schema", "deep.strict-cross-platform-ui.v2");
        evidence.AddSafeValue("invocationId", options.InvocationId);
        evidence.AddSafeValue("releaseInvocationId", options.ReleaseInvocationId);
        evidence.AddSafeValue("sourceCommit", options.SourceCommit);
        evidence.AddSafeValue("generatedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        evidence.AddSafeValue("androidPackage", apk.PackageName);
        evidence.AddSafeValue("androidVersion", apk.VersionName);
        evidence.AddSafeValue("androidVersionCode", apk.VersionCode);
        evidence.AddSafeValue("apkSha256", apk.Sha256);
        evidence.AddSafeValue("apkSizeBytes", approvedPolicy.Apk.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        evidence.AddSafeValue("apkSigningDigest", apk.SigningDigest);
        evidence.AddSafeValue("windowsExeSha256", options.WindowsExeSha256);
        evidence.AddSafeValue("policyId", approvedPolicy.PolicyId);
        evidence.AddSafeValue("approvalReceiptSha256", approvedPolicy.ApprovalReceiptSha256);
        evidence.AddSafeValue("mrXPublicKeySha256", approvedPolicy.MrXPublicKeySha256);
        evidence.AddSafeValue("adbSha256", approvedPolicy.Adb.Sha256);
        evidence.AddSafeValue("aaptSha256", approvedPolicy.Aapt.Sha256);
        evidence.AddSafeValue("apksignerSha256", approvedPolicy.Apksigner.Sha256);
        evidence.AddHash("adbVersionHash", approvedPolicy.Adb.Version);
        evidence.AddHash("aaptVersionHash", approvedPolicy.Aapt.Version);
        evidence.AddHash("apksignerVersionHash", approvedPolicy.Apksigner.Version);
        evidence.AddHash("androidSerialHash", options.AndroidSerial);
        evidence.AddHash("androidFingerprintHash", options.DeviceFingerprint);
        evidence.AddHash("androidModelHash", options.DeviceModel);
        evidence.AddHash("androidProductHash", approvedPolicy.Device.Product);
        evidence.AddHash("androidHardwareHash", approvedPolicy.Device.Hardware);
        evidence.AddHash("androidCharacteristicsHash", approvedPolicy.Device.Characteristics);
        evidence.AddSafeValue("androidSdk", approvedPolicy.Device.Sdk.ToString(System.Globalization.CultureInfo.InvariantCulture));
        evidence.AddSafeValue("approvedPolicySha256", options.PolicySha256);
        evidence.AddSafeValue("fixtureSha256", fixtureSha256);

        // The E2E-only package may be cleared; the production package is never queried or changed.
        android.AssertPhysicalConnectedDevice();
        android.AssertInstalledPackage(apk);
        Exception? runFailure = null;
        try
        {
        attemptCleanup.BeginAndroidPackageMutation();
        android.ClearE2ePackageData();
        android.ColdStart();
        var androidIdentity = CreateAndroidIdentity(android, options);
        evidence.AddHash("androidIdentityHash", androidIdentity);

        int firstWindowsPid;
        string windowsIdentity;
        using (var windows = WindowsUiSmokeTests.WindowsUiTestSession.CreateStrictWithAppData(isolatedWindowsAppData))
        {
            firstWindowsPid = windows.ProcessId;
            windowsIdentity = CreateWindowsIdentity(windows);
            evidence.AddHash("windowsIdentityHash", windowsIdentity);
            Assert.NotEqual(androidIdentity, windowsIdentity);

            VerifyAndroidRejectsInvalidIdWithoutOpeningContact(android, options);
            AddAndroidContact(android, options, windowsIdentity);
            AddWindowsContact(windows, androidIdentity);

            SendWindowsMessage(windows, windowsToAndroid);
            android.WaitForText(options.App("Chat.MessageBody"), windowsToAndroid, TimeSpan.FromSeconds(45));
            SendAndroidMessage(android, options, androidToWindows);
            WaitForWindowsText(windows, "DesktopWorkspace.DirectMessageBody", androidToWindows);

            attemptCleanup.BeginFixturePush();
            android.PushFixture(options.AttachmentFixturePath, marker);
            StageAndSendAndroidAttachment(android, options, marker);
            SaveOpenAndVerifyWindowsAttachment(windows, downloadsDirectory, createdDownloads, marker, fixtureSha256);
            evidence.AddBoolean("bidirectionalTextReceived", true);
        }

        android.ForceStop();
        android.ColdStart();
        android.WaitForResource(options.App("Conversations.Root"), TimeSpan.FromSeconds(30));
        android.Tap(options.App("Conversations.ConversationRow"));
        android.WaitForText(options.App("Chat.MessageBody"), windowsToAndroid, TimeSpan.FromSeconds(30));
        android.WaitForText(options.App("Chat.MessageBody"), androidToWindows, TimeSpan.FromSeconds(30));

        using (var restartedWindows = WindowsUiSmokeTests.WindowsUiTestSession.CreateStrictWithAppData(isolatedWindowsAppData))
        {
            // The root is unique to this lane, but intentionally reused across its cold restart.
            StrictCrossPlatformContracts.AssertDistinctProcessIds(firstWindowsPid, restartedWindows.ProcessId);
            // Reopening the persisted direct workspace proves the reciprocal contact survived too.
            Require(restartedWindows.WaitForAutomationId("DesktopWorkspace.DirectDraft", TimeSpan.FromSeconds(30)), "DesktopWorkspace.DirectDraft");
            WaitForWindowsText(restartedWindows, "DesktopWorkspace.DirectMessageBody", windowsToAndroid);
            WaitForWindowsText(restartedWindows, "DesktopWorkspace.DirectMessageBody", androidToWindows);
            ReDownloadAndVerifyWindowsAttachment(restartedWindows, downloadsDirectory, createdDownloads, marker, fixtureSha256);
            evidence.AddBoolean("coldRestartPersistedMessages", true);
            evidence.AddBoolean("distinctWindowsPid", true);
            evidence.AddBoolean("windowsOpenSaveAndDecryptVerified", true);
        }

        evidence.AddBoolean("androidColdRestartNoCrash", true);
        evidence.AddBoolean("windowsColdRestartNoCrash", true);
        }
        catch (Exception exception)
        {
            runFailure = exception;
        }

        Exception? cleanupFailure = null;
        try { cleanup.RunAll(); }
        catch (Exception exception) { cleanupFailure = exception; }
        if (runFailure is not null || cleanupFailure is not null)
        {
            var failures = new List<Exception>();
            if (runFailure is not null) failures.Add(runFailure);
            if (cleanupFailure is AggregateException aggregate) failures.AddRange(aggregate.InnerExceptions);
            else if (cleanupFailure is not null) failures.Add(cleanupFailure);
            throw new AggregateException("Strict cross-platform execution or cleanup failed.", failures);
        }

        evidence.AddBoolean("cleanupCompleted", true);
        evidence.AddSafeValue("status", "passed");
        evidence.Write(options.ResultPath);
    }

    private static string CreateAndroidIdentity(AndroidUiautomatorClient android, CrossPlatformOptions options)
    {
        android.WaitForResource(options.App("Welcome.DisplayName"), TimeSpan.FromSeconds(30));
        android.Type(options.App("Welcome.DisplayName"), StrictCrossPlatformContracts.NewMarker("android"));
        android.Tap(options.App("Welcome.Create"));
        android.WaitForResource(options.App("Conversations.Root"), TimeSpan.FromSeconds(30));
        android.Tap(options.App("Conversations.ProfileSettings"));
        var identity = StrictCrossPlatformContracts.RequireSessionId(android.WaitForResource(options.App("Settings.SessionId"), TimeSpan.FromSeconds(20)).Text, "Android settings");
        android.Tap(options.App("Settings.Back"));
        return identity;
    }

    private static string CreateWindowsIdentity(WindowsUiSmokeTests.WindowsUiTestSession windows)
    {
        var displayName = Require(windows.WaitForAutomationId("Welcome.DisplayName", TimeSpan.FromSeconds(30)), "Welcome.DisplayName").AsTextBox();
        displayName.Text = StrictCrossPlatformContracts.NewMarker("windows");
        windows.ActivateExact(Require(windows.WaitForAutomationId("Welcome.Create", TimeSpan.FromSeconds(10)), "Welcome.Create"));
        windows.WaitForAutomationId("Conversations.NewConversation", TimeSpan.FromSeconds(30));
        windows.ActivateExact(Require(windows.WaitForAutomationId("DesktopWorkspace.ProfileSettings", TimeSpan.FromSeconds(20)), "DesktopWorkspace.ProfileSettings"));
        var identity = StrictCrossPlatformContracts.RequireSessionId(Require(windows.WaitForAutomationId("Settings.SessionId", TimeSpan.FromSeconds(20)), "Settings.SessionId").Properties.Name.ValueOrDefault ?? string.Empty, "Windows settings");
        windows.ActivateExact(Require(windows.WaitForAutomationId("Settings.Back", TimeSpan.FromSeconds(10)), "Settings.Back"));
        return identity;
    }

    private static void VerifyAndroidRejectsInvalidIdWithoutOpeningContact(AndroidUiautomatorClient android, CrossPlatformOptions options)
    {
        OpenAndroidNewConversation(android, options);
        var invalid = "35" + new string('0', 64);
        StrictCrossPlatformContracts.RequireInvalidSessionId(invalid);
        android.Type(options.App("NewConversation.SessionId"), invalid);
        android.Tap(options.App("NewConversation.Start"));
        var error = android.WaitForResource(options.App("NewConversation.Error"), TimeSpan.FromSeconds(20));
        Assert.False(string.IsNullOrWhiteSpace(error.Text));
        Assert.Null(android.FindOptional(options.App("Chat.Draft")));
        Assert.Null(android.FindOptional(options.App("Chat.MessageBody")));
        android.Tap(options.App("NewConversation.Back"));
        android.WaitForResource(options.App("Conversations.Root"), TimeSpan.FromSeconds(15));
        Assert.Null(android.FindOptional(options.App("Conversations.ConversationRow")));
    }

    private static void AddAndroidContact(AndroidUiautomatorClient android, CrossPlatformOptions options, string windowsIdentity)
    {
        OpenAndroidNewConversation(android, options);
        android.Type(options.App("NewConversation.SessionId"), windowsIdentity);
        android.Type(options.App("NewConversation.DisplayName"), StrictCrossPlatformContracts.NewMarker("windows-contact"));
        android.Tap(options.App("NewConversation.Start"));
        android.WaitForResource(options.App("Chat.Draft"), TimeSpan.FromSeconds(30));
    }

    private static void OpenAndroidNewConversation(AndroidUiautomatorClient android, CrossPlatformOptions options)
    {
        android.Tap(options.App("Conversations.NewConversationTop"));
        android.Tap(options.App("StartConversation.NewMessage"));
        android.WaitForResource(options.App("NewConversation.SessionId"), TimeSpan.FromSeconds(15));
    }

    private static void AddWindowsContact(WindowsUiSmokeTests.WindowsUiTestSession windows, string androidIdentity)
    {
        windows.ActivateExact(Require(windows.WaitForAutomationId("Conversations.NewConversation", TimeSpan.FromSeconds(20)), "Conversations.NewConversation"));
        windows.ActivateExact(Require(windows.WaitForAutomationId("StartConversation.NewMessage", TimeSpan.FromSeconds(15)), "StartConversation.NewMessage"));
        Require(windows.WaitForAutomationId("NewConversation.SessionId", TimeSpan.FromSeconds(15)), "NewConversation.SessionId").AsTextBox().Text = androidIdentity;
        Require(windows.WaitForAutomationId("NewConversation.DisplayName", TimeSpan.FromSeconds(10)), "NewConversation.DisplayName").AsTextBox().Text = StrictCrossPlatformContracts.NewMarker("android-contact");
        windows.ActivateExact(Require(windows.WaitForAutomationId("NewConversation.Start", TimeSpan.FromSeconds(10)), "NewConversation.Start"));
        windows.WaitForAutomationId("DesktopWorkspace.DirectDraft", TimeSpan.FromSeconds(30));
    }

    private static void SendWindowsMessage(WindowsUiSmokeTests.WindowsUiTestSession windows, string message)
    {
        Require(windows.WaitForAutomationId("DesktopWorkspace.DirectDraft", TimeSpan.FromSeconds(15)), "DesktopWorkspace.DirectDraft").AsTextBox().Text = message;
        windows.ActivateExact(Require(windows.WaitForAutomationId("DesktopWorkspace.DirectSend", TimeSpan.FromSeconds(10)), "DesktopWorkspace.DirectSend"));
    }

    private static void SendAndroidMessage(AndroidUiautomatorClient android, CrossPlatformOptions options, string message)
    {
        android.Type(options.App("Chat.Draft"), message);
        android.Tap(options.App("Chat.Send"));
    }

    private static void StageAndSendAndroidAttachment(AndroidUiautomatorClient android, CrossPlatformOptions options, string marker)
    {
        android.Tap(options.App("Chat.Attach"));
        android.Tap(options.App("Chat.PickFile"));
        android.Tap(options.PickerDownloadsResourceId);
        // The picker filename is asserted from the exact configured resource-id; no text-only selector is used.
        android.TapExactResourceIdWithExactText(options.PickerFileResourceId, marker);
        if (options.PickerConfirmResourceId is not null)
        {
            android.Tap(options.PickerConfirmResourceId);
        }
        android.WaitForText(options.App("Chat.StagedAttachmentFilename"), marker, TimeSpan.FromSeconds(30));
        android.Tap(options.App("Chat.Send"));
    }

    private static void SaveOpenAndVerifyWindowsAttachment(WindowsUiSmokeTests.WindowsUiTestSession windows, string downloadsDirectory, ICollection<string> createdDownloads, string marker, string expectedSha256)
    {
        var attachment = Require(windows.WaitForAutomationIdWithName("DesktopWorkspace.DirectAttachmentFilename", marker, TimeSpan.FromSeconds(45)), "DesktopWorkspace.DirectAttachmentFilename");
        windows.ActivateExact(attachment);
        windows.ActivateExact(Require(windows.WaitForAutomationId("DesktopWorkspace.AttachmentOpen", TimeSpan.FromSeconds(15)), "DesktopWorkspace.AttachmentOpen"));
        // Open may replace the attachment menu.  Re-select the same exact correlated filename before Save.
        attachment = Require(windows.WaitForAutomationIdWithName("DesktopWorkspace.DirectAttachmentFilename", marker, TimeSpan.FromSeconds(15)), "DesktopWorkspace.DirectAttachmentFilename");
        windows.ActivateExact(attachment);
        var before = StrictCrossPlatformContracts.SnapshotDownloads(downloadsDirectory);
        windows.ActivateExact(Require(windows.WaitForAutomationId("DesktopWorkspace.AttachmentSave", TimeSpan.FromSeconds(15)), "DesktopWorkspace.AttachmentSave"));
        var saved = before.WaitForNewCorrelatedFile(marker, TimeSpan.FromSeconds(30), createdDownloads);
        Assert.Equal(expectedSha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(saved))));
    }

    private static void ReDownloadAndVerifyWindowsAttachment(WindowsUiSmokeTests.WindowsUiTestSession windows, string downloadsDirectory, ICollection<string> createdDownloads, string marker, string expectedSha256)
    {
        var attachment = Require(windows.WaitForAutomationIdWithName("DesktopWorkspace.DirectAttachmentFilename", marker, TimeSpan.FromSeconds(30)), "DesktopWorkspace.DirectAttachmentFilename");
        windows.ActivateExact(attachment);
        var before = StrictCrossPlatformContracts.SnapshotDownloads(downloadsDirectory);
        windows.ActivateExact(Require(windows.WaitForAutomationId("DesktopWorkspace.AttachmentSave", TimeSpan.FromSeconds(15)), "DesktopWorkspace.AttachmentSave"));
        var saved = before.WaitForNewCorrelatedFile(marker, TimeSpan.FromSeconds(30), createdDownloads);
        Assert.NotEqual(createdDownloads.First(), saved);
        Assert.Equal(expectedSha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(saved))));
    }

    private static void WaitForWindowsText(WindowsUiSmokeTests.WindowsUiTestSession windows, string automationId, string text) =>
        Assert.NotNull(windows.WaitForAutomationIdWithName(automationId, text, TimeSpan.FromSeconds(45)));

    private static AutomationElement Require(AutomationElement? element, string selector) => element ?? throw new InvalidOperationException($"Required AutomationId was not found: {selector}.");
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class StrictCrossPlatformUiFactAttribute : FactAttribute
{
    public StrictCrossPlatformUiFactAttribute()
    {
        Skip = CrossPlatformOptions.NotRunReason();
        if (Skip is null) Skip = WindowsDesktopGate.NotRunReason();
    }
}

internal sealed class CrossPlatformOptions
{
    private static readonly string[] RequiredAppRoles =
    [
        "Welcome.DisplayName", "Welcome.Create", "Conversations.Root", "Conversations.ProfileSettings",
        "Conversations.NewConversationTop", "Conversations.ConversationRow", "Settings.SessionId", "Settings.Back",
        "StartConversation.NewMessage", "NewConversation.SessionId", "NewConversation.DisplayName", "NewConversation.Start",
        "NewConversation.Error", "NewConversation.Back", "Chat.Draft", "Chat.Send", "Chat.MessageBody",
        "Chat.Attach", "Chat.PickFile", "Chat.StagedAttachmentFilename"
    ];
    private readonly Dictionary<string, string> androidSelectors;
    private CrossPlatformOptions(string serial, string adbPath, string apkPath, string aaptPath, string apksignerPath, string fixturePath, string artifactDirectory, Dictionary<string, string> selectors, string pickerDownloads, string pickerFile, string? pickerConfirm, string fingerprint, string model, string sourceCommit, string windowsExeSha256, string releaseInvocationId, string policySha256)
    {
        AndroidSerial = serial; AdbPath = adbPath; ApkPath = apkPath; AaptPath = aaptPath; ApksignerPath = apksignerPath; AttachmentFixturePath = fixturePath; ArtifactDirectory = artifactDirectory;
        androidSelectors = selectors; PickerDownloadsResourceId = pickerDownloads; PickerFileResourceId = pickerFile; PickerConfirmResourceId = pickerConfirm;
        ResultPath = Path.Combine(artifactDirectory, "cross-platform-ui-result.json"); InvocationId = Guid.NewGuid().ToString("N"); DeviceFingerprint = fingerprint; DeviceModel = model; SourceCommit = sourceCommit; WindowsExeSha256 = windowsExeSha256;
        ReleaseInvocationId = releaseInvocationId;
        PolicySha256 = policySha256;
    }
    internal string AndroidSerial { get; }
    internal string AdbPath { get; }
    internal string ApkPath { get; }
    internal string AaptPath { get; }
    internal string ApksignerPath { get; }
    internal string AttachmentFixturePath { get; }
    internal string ArtifactDirectory { get; }
    internal string PickerFileResourceId { get; }
    internal string PickerDownloadsResourceId { get; }
    internal string? PickerConfirmResourceId { get; }
    internal string ResultPath { get; }
    internal string InvocationId { get; }
    internal string DeviceFingerprint { get; }
    internal string DeviceModel { get; }
    internal string SourceCommit { get; }
    internal string WindowsExeSha256 { get; }
    internal string ReleaseInvocationId { get; }
    internal string PolicySha256 { get; }
    internal string App(string role) => androidSelectors.TryGetValue(role, out var id) ? id : throw new InvalidOperationException($"Missing Android selector for {role}.");

    internal static string? NotRunReason()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("DEEP_STRICT_CROSS_PLATFORM_UI"), "1", StringComparison.Ordinal)) return "NOT-RUN: set DEEP_STRICT_CROSS_PLATFORM_UI=1 on an approved unlocked physical Android and Windows UI lab.";
        var required = new[] { "DEEP_E2E_ANDROID_SERIAL", "DEEP_E2E_ADB", "DEEP_E2E_ANDROID_APK", "DEEP_E2E_AAPT", "DEEP_E2E_APKSIGNER", "DEEP_E2E_ATTACHMENT_FIXTURE", "DEEP_E2E_ARTIFACTS", "DEEP_E2E_ANDROID_SELECTORS_JSON", "DEEP_E2E_ANDROID_PICKER_DOWNLOADS_ID", "DEEP_E2E_ANDROID_PICKER_FILE_ID", "DEEP_MAUI_EXE", "DEEP_E2E_APPDATA_ROOT", "DEEP_E2E_BOOTSTRAP", "DEEP_E2E_ANDROID_POLICY", "DEEP_MR_X_PUBLIC_KEY_SHA256", "DEEP_RELEASE_INVOCATION_ID" };
        var missing = required.Where(key => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key))).ToArray();
        return missing.Length == 0 ? null : "NOT-RUN: missing physical lane prerequisites: " + string.Join(", ", missing);
    }

    internal static CrossPlatformOptions Load()
    {
        if (NotRunReason() is { } reason) throw new InvalidOperationException(reason);
        if (!string.Equals(Environment.GetEnvironmentVariable("DEEP_E2E_BOOTSTRAP"), "live", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Physical cross-platform UI requires DEEP_E2E_BOOTSTRAP=live; stub is not evidence.");
        var selectors = ParseSelectors(Environment.GetEnvironmentVariable("DEEP_E2E_ANDROID_SELECTORS_JSON")!);
        var pickerDownloads = Environment.GetEnvironmentVariable("DEEP_E2E_ANDROID_PICKER_DOWNLOADS_ID")!; var pickerFile = Environment.GetEnvironmentVariable("DEEP_E2E_ANDROID_PICKER_FILE_ID")!; var pickerConfirm = Environment.GetEnvironmentVariable("DEEP_E2E_ANDROID_PICKER_CONFIRM_ID");
        StrictCrossPlatformContracts.ValidateResourceId(pickerDownloads, "picker Downloads selector"); StrictCrossPlatformContracts.ValidateResourceId(pickerFile, "picker file selector");
        if (!string.IsNullOrWhiteSpace(pickerConfirm))
        {
            StrictCrossPlatformContracts.ValidateResourceId(pickerConfirm, "picker confirm selector");
        }
        else
        {
            pickerConfirm = null;
        }
        var repositoryRoot = Directory.GetCurrentDirectory();
        var policy = ApprovedCrossPlatformPolicy.Load(
            Environment.GetEnvironmentVariable("DEEP_E2E_ANDROID_POLICY")!,
            repositoryRoot,
            Environment.GetEnvironmentVariable("DEEP_MR_X_PUBLIC_KEY_SHA256")!);
        if (!string.Equals(Environment.GetEnvironmentVariable("DEEP_E2E_ANDROID_SERIAL"), policy.Device.Serial, StringComparison.Ordinal)) throw new InvalidOperationException("Configured Android serial does not match approved inventory.");
        var apk = RequirePinnedPath("DEEP_E2E_ANDROID_APK", policy.Apk.Path); var fixture = RequireAbsoluteFile("DEEP_E2E_ATTACHMENT_FIXTURE"); var adb = RequirePinnedPath("DEEP_E2E_ADB", policy.Adb.Path); var aapt = RequirePinnedPath("DEEP_E2E_AAPT", policy.Aapt.Path); var apksigner = RequirePinnedPath("DEEP_E2E_APKSIGNER", policy.Apksigner.Path); var artifacts = Path.GetFullPath(Environment.GetEnvironmentVariable("DEEP_E2E_ARTIFACTS")!);
        var windowsExe = Environment.GetEnvironmentVariable("DEEP_MAUI_EXE")!;
        var appDataRoot = Environment.GetEnvironmentVariable("DEEP_E2E_APPDATA_ROOT")!;
        if (!Path.IsPathFullyQualified(windowsExe) || !File.Exists(windowsExe) || !Path.IsPathFullyQualified(appDataRoot) || !string.Equals(Path.GetFullPath(windowsExe), policy.WindowsExePath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Configured Windows executable or app-data root is invalid.");
        StrictCrossPlatformContracts.RequirePinnedFile(windowsExe, policy.WindowsExeSha256, "Windows executable");
        var windowsHash = StrictCrossPlatformContracts.Sha256File(windowsExe);
        Directory.CreateDirectory(artifacts);
        var commit = StrictCrossPlatformContracts.RequireCurrentCommit(repositoryRoot, policy.SourceCommit);
        policy.ValidateTool(adb, "adb");
        policy.ValidateTool(aapt, "aapt");
        policy.ValidateTool(apksigner, "apksigner");
        var releaseInvocation = Environment.GetEnvironmentVariable("DEEP_RELEASE_INVOCATION_ID")!;
        if (!System.Text.RegularExpressions.Regex.IsMatch(releaseInvocation, "^[a-f0-9]{32}$")) throw new InvalidOperationException("Release invocation ID must be fresh 32-hex.");
        return new CrossPlatformOptions(policy.Device.Serial, adb, apk, aapt, apksigner, fixture, artifacts, selectors, pickerDownloads, pickerFile, pickerConfirm, policy.Device.Fingerprint, policy.Device.Model, commit, windowsHash, releaseInvocation, policy.PolicySha256);
    }
    internal StrictCrossPlatformContracts.ApkMetadata ReadAndValidateApkMetadata()
    {
        var policy = ApprovedCrossPlatformPolicy.Current ?? throw new InvalidOperationException("Approved policy was not loaded.");
        var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(ApkPath)));
        if (new FileInfo(ApkPath).Length != policy.Apk.SizeBytes || !string.Equals(sha, policy.Apk.Sha256, StringComparison.Ordinal)) throw new InvalidOperationException("Selected APK does not match the signed size/SHA-256.");
        var badging = AndroidUiautomatorClient.Run(AaptPath, ["dump", "badging", ApkPath]);
        if (badging.ExitCode != 0) throw new InvalidOperationException("aapt failed to inspect the configured APK.");
        var metadata = StrictCrossPlatformContracts.ApkMetadata.ParseAaptBadging(badging.Output, sha);
        if (!string.Equals(metadata.PackageName, StrictCrossPlatformContracts.AndroidPackage, StringComparison.Ordinal) || metadata.VersionCode != policy.Apk.VersionCode || metadata.VersionName != policy.Apk.VersionName) throw new InvalidOperationException("APK package/version does not match approved inventory.");
        var signer = AndroidUiautomatorClient.Run(ApksignerPath, ["verify", "--print-certs", ApkPath]);
        if (signer.ExitCode != 0) throw new InvalidOperationException("apksigner could not verify the configured APK.");
        var digest = System.Text.RegularExpressions.Regex.Match(signer.Output, "SHA-256[^:]*digest:\\s*(?<digest>[A-Fa-f0-9:]{64,95})", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!digest.Success) throw new InvalidOperationException("apksigner did not emit a certificate SHA-256 digest.");
        var actual = digest.Groups["digest"].Value.Replace(":", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        if (!string.Equals(actual, policy.Apk.SigningDigest, StringComparison.Ordinal)) throw new InvalidOperationException("APK signer does not match approved inventory.");
        return metadata.WithSigningDigest(actual);
    }

    private static string RequireAbsoluteFile(string key) { var path = Environment.GetEnvironmentVariable(key)!; if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new InvalidOperationException($"{key} must be an existing absolute path."); return Path.GetFullPath(path); }
    private static string RequirePinnedPath(string key, string approvedPath) { var path = RequireAbsoluteFile(key); if (!string.Equals(path, Path.GetFullPath(approvedPath), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"{key} is not the approved exact path."); return path; }
    internal string ResolveProductionDownloadsDirectory() =>
        Windows.Storage.DownloadsFolder.CreateFolderAsync("Deep", Windows.Storage.CreationCollisionOption.OpenIfExists).AsTask().GetAwaiter().GetResult().Path;
    private static Dictionary<string, string> ParseSelectors(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json); if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) throw new InvalidOperationException("Android selector JSON must be an object.");
        var items = doc.RootElement.EnumerateObject().ToArray();
        if (items.Length != RequiredAppRoles.Length || items.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != items.Length || !items.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(RequiredAppRoles.OrderBy(x => x, StringComparer.Ordinal))) throw new InvalidOperationException("Android selector JSON must contain exactly the canonical role key set with no duplicates or extras.");
        var map = new Dictionary<string, string>(StringComparer.Ordinal); foreach (var item in items) { if (item.Value.ValueKind != System.Text.Json.JsonValueKind.String) throw new InvalidOperationException("Android selector values must be literal strings."); var value = item.Value.GetString()!; StrictCrossPlatformContracts.ValidateResourceId(value, "Android selector"); if (!value.StartsWith(StrictCrossPlatformContracts.AndroidPackage + ":id/", StringComparison.Ordinal) || !map.TryAdd(item.Name, value)) throw new InvalidOperationException("Android selector is outside the E2E package or duplicated."); }
        if (map.Values.Distinct(StringComparer.Ordinal).Count() != map.Count) throw new InvalidOperationException("Android selector literals must be unique."); return map;
    }
}

internal sealed class ApprovedCrossPlatformPolicy
{
    internal sealed record ToolPin(string Path, string Sha256, string Version, string[] VersionArguments);
    internal sealed record ApkPin(string Path, long SizeBytes, string Sha256, string VersionCode, string VersionName, string SigningDigest);
    internal sealed record DevicePin(string Serial, string Fingerprint, string Model, string Product, string Hardware, int Sdk, string Characteristics);

    private ApprovedCrossPlatformPolicy(string sourceCommit, string policyId, string approvalReceiptSha256, string mrXPublicKeySha256, string windowsExePath, string windowsExeSha256, string policySha256, ToolPin adb, ToolPin aapt, ToolPin apksigner, ApkPin apk, DevicePin device)
    {
        SourceCommit = sourceCommit; PolicyId = policyId; ApprovalReceiptSha256 = approvalReceiptSha256; MrXPublicKeySha256 = mrXPublicKeySha256; WindowsExePath = windowsExePath; WindowsExeSha256 = windowsExeSha256; PolicySha256 = policySha256; Adb = adb; Aapt = aapt; Apksigner = apksigner; Apk = apk; Device = device;
    }

    internal static ApprovedCrossPlatformPolicy? Current { get; private set; }
    internal string SourceCommit { get; }
    internal string PolicyId { get; }
    internal string ApprovalReceiptSha256 { get; }
    internal string MrXPublicKeySha256 { get; }
    internal string WindowsExePath { get; }
    internal string WindowsExeSha256 { get; }
    internal string PolicySha256 { get; }
    internal ToolPin Adb { get; }
    internal ToolPin Aapt { get; }
    internal ToolPin Apksigner { get; }
    internal ApkPin Apk { get; }
    internal DevicePin Device { get; }

    internal static ApprovedCrossPlatformPolicy Load(string path, string repositoryRoot, string mrXPublicKeySha256)
    {
        var expectedPolicyPath = Path.Combine(repositoryRoot, ".secrets", "android-lab", "approved-policy.json");
        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(expectedPolicyPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Cross-platform evidence requires the established protected Android lab policy path.");
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(mrXPublicKeySha256, "^[a-f0-9]{64}$") || mrXPublicKeySha256.All(character => character == '0'))
        {
            throw new InvalidOperationException("A nonzero externally pinned Mr. X public-key SHA-256 is required.");
        }
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.GetProperty("schema").GetString() != "deep.survival.android-lab-policy.v1" ||
            !root.GetProperty("provisioned").GetBoolean() ||
            root.GetProperty("synthetic").GetBoolean())
        {
            throw new InvalidOperationException("Cross-platform evidence requires a provisioned non-synthetic Android lab policy.");
        }

        var approval = root.GetProperty("approval");
        var signature = root.GetProperty("signature");
        if (approval.GetProperty("state").GetString() != "approved" ||
            approval.GetProperty("approvedBy").GetString() != "Mr. X" ||
            signature.GetProperty("algorithm").GetString() != "Ed25519" ||
            signature.GetProperty("publicKeySha256").GetString() != mrXPublicKeySha256)
        {
            throw new InvalidOperationException("Android lab inventory lacks exact Mr. X approval/signature bindings.");
        }
        var receiptPath = ResolveProtectedPath(repositoryRoot, approval.GetProperty("receiptRelativePath").GetString()!);
        var publicKeyPath = ResolveProtectedPath(repositoryRoot, signature.GetProperty("publicKeyRelativePath").GetString()!);
        var signaturePath = ResolveProtectedPath(repositoryRoot, signature.GetProperty("signatureRelativePath").GetString()!);
        var payloadPath = ResolveProtectedPath(repositoryRoot, signature.GetProperty("signedPayloadRelativePath").GetString()!);
        StrictCrossPlatformContracts.RequirePinnedFile(receiptPath, approval.GetProperty("receiptSha256").GetString()!, "Mr. X approval receipt");
        StrictCrossPlatformContracts.RequirePinnedFile(publicKeyPath, mrXPublicKeySha256, "Mr. X public key");
        StrictCrossPlatformContracts.RequirePinnedFile(payloadPath, signature.GetProperty("signedPayloadSha256").GetString()!, "Mr. X signed payload");
        if (!File.Exists(signaturePath)) throw new InvalidOperationException("Mr. X detached signature is absent.");
        using var payloadDocument = System.Text.Json.JsonDocument.Parse(File.ReadAllText(payloadPath));
        var payload = payloadDocument.RootElement;
        foreach (var scalar in new[] { "schema", "provisioned", "synthetic", "sourceCommitSha", "policyId" })
        {
            if (root.GetProperty(scalar).GetRawText() != payload.GetProperty(scalar).GetRawText()) throw new InvalidOperationException($"Signed payload does not bind {scalar}.");
        }
        foreach (var section in new[] { "approval", "tools", "device", "application", "crossPlatform" })
        {
            var policyNode = System.Text.Json.Nodes.JsonNode.Parse(root.GetProperty(section).GetRawText());
            var payloadNode = System.Text.Json.Nodes.JsonNode.Parse(payload.GetProperty(section).GetRawText());
            if (!System.Text.Json.Nodes.JsonNode.DeepEquals(policyNode, payloadNode)) throw new InvalidOperationException($"Signed payload does not bind {section}.");
        }
        VerifySignature(repositoryRoot, publicKeyPath, signaturePath, payloadPath);

        var tools = root.GetProperty("tools");
        ToolPin ReadTool(string name)
        {
            var value = tools.GetProperty(name);
            return new ToolPin(
                ResolveProtectedPath(repositoryRoot, value.GetProperty("relativePath").GetString()!),
                value.GetProperty("sha256").GetString()!,
                value.GetProperty("version").GetString()!,
                value.GetProperty("versionArguments").EnumerateArray().Select(x => x.GetString()!).ToArray());
        }

        var apk = root.GetProperty("application");
        var device = root.GetProperty("device");
        var crossPlatform = root.GetProperty("crossPlatform");
        if (apk.GetProperty("packageId").GetString() != StrictCrossPlatformContracts.AndroidPackage ||
            device.GetProperty("kernelQemu").GetString() != "0" ||
            device.GetProperty("class").GetString() != "physical-managed-dedicated" ||
            !device.GetProperty("dedicated").GetBoolean() ||
            device.GetProperty("inventoryState").GetString() != "approved" ||
            device.GetProperty("inventoryApprovedBy").GetString() != "Mr. X" ||
            device.GetProperty("inventoryApprovalReceiptSha256").GetString() != approval.GetProperty("receiptSha256").GetString())
        {
            throw new InvalidOperationException("Signed application/device inventory is not the approved physical dedicated contract.");
        }
        var parsed = new ApprovedCrossPlatformPolicy(
            root.GetProperty("sourceCommitSha").GetString()!,
            root.GetProperty("policyId").GetString()!,
            approval.GetProperty("receiptSha256").GetString()!,
            mrXPublicKeySha256,
            ResolveRepositoryPath(repositoryRoot, crossPlatform.GetProperty("windowsExecutableRelativePath").GetString()!),
            crossPlatform.GetProperty("windowsExecutableSha256").GetString()!,
            StrictCrossPlatformContracts.Sha256File(path),
            ReadTool("adb"),
            ReadTool("aapt"),
            ReadTool("apksigner"),
            new ApkPin(
                ResolveRepositoryPath(repositoryRoot, apk.GetProperty("apkRelativePath").GetString()!),
                apk.GetProperty("apkSizeBytes").GetInt64(),
                apk.GetProperty("apkSha256").GetString()!,
                apk.GetProperty("versionCode").ToString(),
                apk.GetProperty("versionName").GetString()!,
                apk.GetProperty("signingCertificateSha256").GetString()!),
            new DevicePin(
                device.GetProperty("serial").GetString()!,
                device.GetProperty("fingerprint").GetString()!,
                device.GetProperty("model").GetString()!,
                device.GetProperty("product").GetString()!,
                device.GetProperty("hardware").GetString()!,
                device.GetProperty("sdk").GetInt32(),
                device.GetProperty("characteristics").GetString()!));
        parsed.ValidateShape();
        Current = parsed;
        return parsed;
    }

    internal void ValidateTool(string path, string role)
    {
        var pin = role switch { "adb" => Adb, "aapt" => Aapt, "apksigner" => Apksigner, _ => throw new InvalidOperationException("Unknown tool role.") };
        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(pin.Path), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"{role} path is caller-controlled.");
        StrictCrossPlatformContracts.RequirePinnedFile(path, pin.Sha256, role);
        StrictCrossPlatformContracts.RequireExactVersion(
            StrictCrossPlatformContracts.RunBounded(path, pin.VersionArguments, TimeSpan.FromSeconds(15)),
            pin.Version,
            role);
    }

    private void ValidateShape()
    {
        static void Hex(string value, int length, string role)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(value, $"^[a-f0-9]{{{length}}}$") || value.All(character => character == '0')) throw new InvalidOperationException($"{role} is malformed.");
        }
        Hex(SourceCommit, 40, "source commit"); Hex(PolicyId, 64, "policy ID"); Hex(ApprovalReceiptSha256, 64, "approval receipt"); Hex(MrXPublicKeySha256, 64, "Mr. X key"); Hex(WindowsExeSha256, 64, "Windows hash"); Hex(Apk.Sha256, 64, "APK hash"); Hex(Apk.SigningDigest, 64, "APK signer");
        foreach (var tool in new[] { Adb, Aapt, Apksigner })
        {
            Hex(tool.Sha256, 64, "tool hash");
            if (!Path.IsPathFullyQualified(tool.Path) || tool.VersionArguments.Length is < 1 or > 4 || string.IsNullOrWhiteSpace(tool.Version)) throw new InvalidOperationException("Approved tool definition is malformed.");
        }
        if (!string.Equals(Path.GetFileName(Adb.Path), "adb.exe", StringComparison.OrdinalIgnoreCase) ||
            !new[] { "aapt.exe", "aapt2.exe" }.Contains(Path.GetFileName(Aapt.Path), StringComparer.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(Apksigner.Path), "apksigner.bat", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Approved tool paths do not match their fixed roles.");
        }
        if (!Path.IsPathFullyQualified(Apk.Path) || Apk.SizeBytes <= 0 || string.IsNullOrWhiteSpace(Apk.VersionCode) || string.IsNullOrWhiteSpace(Apk.VersionName) || Device.Sdk is < 26 or > 100) throw new InvalidOperationException("Approved APK/device definition is malformed.");
        StrictCrossPlatformContracts.RequirePhysicalDeviceInventory(Device.Fingerprint, Device.Model, Device.Product, Device.Hardware, Device.Characteristics, Device.Sdk);
    }

    private static string ResolveProtectedPath(string repositoryRoot, string relative)
    {
        if (relative.Contains('\\') || relative.Split('/').Contains("..") || !relative.StartsWith(".secrets/android-lab/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Signed protected path is not canonical.");
        }
        return ResolveRepositoryPath(repositoryRoot, relative);
    }

    private static string ResolveRepositoryPath(string repositoryRoot, string relative)
    {
        if (Path.IsPathFullyQualified(relative) || relative.Contains('\\') || relative.Split('/').Contains(".."))
        {
            throw new InvalidOperationException("Signed repository path is not canonical.");
        }
        var root = Path.GetFullPath(repositoryRoot);
        var resolved = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Signed path escapes the repository.");
        }
        return resolved;
    }

    private static void VerifySignature(string repositoryRoot, string publicKeyPath, string signaturePath, string payloadPath)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(dotnet) || !Path.IsPathFullyQualified(dotnet) || !File.Exists(dotnet))
        {
            dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        }
        var verifier = Path.Combine(repositoryRoot, "eng", "Deep.AndroidLab.PolicyVerifier", "Deep.AndroidLab.PolicyVerifier.csproj");
        var result = StrictCrossPlatformContracts.RunBounded(
            dotnet,
            ["run", "--project", verifier, "-c", "Release", "--no-build", "--no-restore", "--", "verify", publicKeyPath, signaturePath, payloadPath],
            TimeSpan.FromSeconds(30));
        if (result.ExitCode != 0) throw new InvalidOperationException("Mr. X Ed25519 signature verification failed.");
    }
}

internal sealed class AndroidUiautomatorClient
{
    private readonly CrossPlatformOptions options;
    internal AndroidUiautomatorClient(CrossPlatformOptions options) => this.options = options;
    internal void AssertPhysicalConnectedDevice()
    {
        var policy = ApprovedCrossPlatformPolicy.Current ?? throw new InvalidOperationException("Approved policy was not loaded.");
        RequireSuccess(Adb("get-state"), expectedOutput: "device");
        RequireExactProperty("ro.kernel.qemu", "0");
        RequireExactProperty("ro.build.fingerprint", policy.Device.Fingerprint);
        RequireExactProperty("ro.product.model", policy.Device.Model);
        RequireExactProperty("ro.product.name", policy.Device.Product);
        RequireExactProperty("ro.hardware", policy.Device.Hardware);
        RequireExactProperty("ro.build.version.sdk", policy.Device.Sdk.ToString(System.Globalization.CultureInfo.InvariantCulture));
        RequireExactProperty("ro.build.characteristics", policy.Device.Characteristics);
    }
    internal void AssertInstalledPackage(StrictCrossPlatformContracts.ApkMetadata apk)
    {
        var pathResult = Adb("shell", "pm", "path", StrictCrossPlatformContracts.AndroidPackage);
        var detailsResult = Adb("shell", "dumpsys", "package", StrictCrossPlatformContracts.AndroidPackage);
        RequireSuccess(pathResult); RequireSuccess(detailsResult);
        var path = pathResult.Output;
        var details = detailsResult.Output;
        var installedPath = path.Split('\n').Select(x => x.Trim()).SingleOrDefault(x => x.StartsWith("package:", StringComparison.Ordinal))?.Substring("package:".Length) ?? throw new InvalidOperationException("The E2E package has no unique installed APK path.");
        var installedHashResult = Adb("shell", "sha256sum", installedPath);
        RequireSuccess(installedHashResult);
        var installedSha = installedHashResult.Output.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
        if (!details.Contains("versionCode=" + apk.VersionCode, StringComparison.Ordinal) || !details.Contains("versionName=" + apk.VersionName, StringComparison.Ordinal) || !details.Replace(":", string.Empty, StringComparison.Ordinal).Contains(apk.SigningDigest, StringComparison.OrdinalIgnoreCase) || !string.Equals(installedSha, apk.Sha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Installed E2E package metadata, signing digest, or APK identity does not match the supplied APK.");
    }
    internal void ClearE2ePackageData() => RequireSuccess(Adb("shell", "pm", "clear", StrictCrossPlatformContracts.AndroidPackage));
    internal void ColdStart() => RequireSuccess(Adb("shell", "monkey", "-p", StrictCrossPlatformContracts.AndroidPackage, "1"));
    internal void ForceStop() => RequireSuccess(Adb("shell", "am", "force-stop", StrictCrossPlatformContracts.AndroidPackage));
    internal void PushFixture(string source, string marker) { var target = "/sdcard/Download/" + marker; RequireSuccess(Adb("push", source, target)); }
    internal void DeletePushedFixture(string marker) => RequireSuccess(Adb("shell", "rm", "-f", "/sdcard/Download/" + marker));
    internal StrictCrossPlatformContracts.AndroidNode WaitForResource(string resourceId, TimeSpan timeout) => Wait(resourceId, null, timeout);
    internal void WaitForText(string resourceId, string text, TimeSpan timeout) { var node = WaitByMarker(resourceId, text, timeout); Assert.Contains(text, node.Text, StringComparison.Ordinal); }
    internal StrictCrossPlatformContracts.AndroidNode? FindOptional(string resourceId) => StrictCrossPlatformContracts.FindOptionalResourceId(Dump(), resourceId);
    internal void Tap(string resourceId) { var node = WaitForResource(resourceId, TimeSpan.FromSeconds(15)); var point = node.Bounds.Center; RequireSuccess(Adb("shell", "input", "tap", point.X.ToString(System.Globalization.CultureInfo.InvariantCulture), point.Y.ToString(System.Globalization.CultureInfo.InvariantCulture))); }
    internal void TapExactResourceIdWithExactText(string resourceId, string text) { var node = WaitByText(resourceId, text, TimeSpan.FromSeconds(15)); Assert.Equal(text, node.Text); var point = node.Bounds.Center; RequireSuccess(Adb("shell", "input", "tap", point.X.ToString(System.Globalization.CultureInfo.InvariantCulture), point.Y.ToString(System.Globalization.CultureInfo.InvariantCulture))); }
    internal void Type(string resourceId, string value) { Tap(resourceId); RequireSuccess(Adb("shell", "input", "text", value)); }
    private StrictCrossPlatformContracts.AndroidNode Wait(string resourceId, string? expectedText, TimeSpan timeout) { var until = DateTime.UtcNow + timeout; Exception? last = null; while (DateTime.UtcNow < until) { try { var node = StrictCrossPlatformContracts.FindExactlyOneResourceId(Dump(), resourceId); if (expectedText is null || string.Equals(node.Text, expectedText, StringComparison.Ordinal)) return node; } catch (Exception ex) { last = ex; } Thread.Sleep(250); } throw new InvalidOperationException($"Required Android resource-id did not reach its expected state: {resourceId}.", last); }
    private StrictCrossPlatformContracts.AndroidNode WaitByText(string resourceId, string text, TimeSpan timeout) { var until = DateTime.UtcNow + timeout; Exception? last = null; while (DateTime.UtcNow < until) { try { return StrictCrossPlatformContracts.FindExactlyOneResourceIdWithText(Dump(), resourceId, text); } catch (Exception ex) { last = ex; } Thread.Sleep(250); } throw new InvalidOperationException($"Required Android resource-id/text pair did not reach its expected state: {resourceId}.", last); }
    private StrictCrossPlatformContracts.AndroidNode WaitByMarker(string resourceId, string text, TimeSpan timeout) { var until = DateTime.UtcNow + timeout; Exception? last = null; while (DateTime.UtcNow < until) { try { return StrictCrossPlatformContracts.FindExactlyOneResourceIdContainingText(Dump(), resourceId, text); } catch (Exception ex) { last = ex; } Thread.Sleep(250); } throw new InvalidOperationException($"Required Android resource-id/text marker did not reach its expected state: {resourceId}.", last); }
    private string Dump() { var result = Adb("exec-out", "uiautomator", "dump", "/dev/tty"); RequireSuccess(result); return result.Output; }
    private void RequireExactProperty(string name, string expected)
    {
        var result = Adb("shell", "getprop", name);
        RequireSuccess(result);
        if (!string.Equals(result.Output.Trim(), expected, StringComparison.Ordinal)) throw new InvalidOperationException($"Approved device property mismatch: {name}.");
    }
    private StrictCrossPlatformContracts.ProcessResult Adb(params string[] args) => Run(options.AdbPath, ["-s", options.AndroidSerial, .. args]);
    internal static StrictCrossPlatformContracts.ProcessResult Run(string fileName, IReadOnlyList<string> args) => StrictCrossPlatformContracts.RunBounded(fileName, args, TimeSpan.FromSeconds(30));
    private static void RequireSuccess(StrictCrossPlatformContracts.ProcessResult result, string? expectedOutput = null) { if (result.ExitCode != 0 || (expectedOutput is not null && !string.Equals(result.Output.Trim(), expectedOutput, StringComparison.Ordinal))) throw new InvalidOperationException("ADB/aapt command failed without emitting its raw output into evidence."); }
}

internal static class WindowsDesktopGate
{
    private const uint DesktopReadObjects = 0x0001;
    private const int UoiName = 2;

    internal static string? NotRunReason()
    {
        if (!OperatingSystem.IsWindows()) return "NOT-RUN: the physical cross-platform lane requires Windows.";
        var desktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == IntPtr.Zero) return "NOT-RUN: Windows input desktop is unavailable (the session may be locked).";
        try
        {
            var name = new StringBuilder(128);
            if (!GetUserObjectInformation(desktop, UoiName, name, name.Capacity, out _) || !string.Equals(name.ToString(), "Default", StringComparison.Ordinal))
                return "NOT-RUN: Windows is locked or does not expose the interactive Default desktop.";
        }
        finally { CloseDesktop(desktop); }

        return null;
    }

    internal static void EnsureUnlocked()
    {
        if (NotRunReason() is { } reason) throw new InvalidOperationException(reason);
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder info, int length, out int needed);
}
