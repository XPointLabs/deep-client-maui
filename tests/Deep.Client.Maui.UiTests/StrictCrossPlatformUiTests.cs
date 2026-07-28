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
        var isolatedWindowsAppData = WindowsUiSmokeTests.WindowsUiTestSession.CreateIsolatedAppDataDirectory();
        File.Delete(options.ResultPath); // A pass may never reuse evidence from an earlier invocation.
        evidence.AddSafeValue("schema", "deep.strict-cross-platform-ui.v2");
        evidence.AddSafeValue("invocationId", options.InvocationId);
        evidence.AddSafeValue("sourceCommit", options.SourceCommit);
        evidence.AddSafeValue("androidPackage", apk.PackageName);
        evidence.AddSafeValue("androidVersion", apk.VersionName);
        evidence.AddSafeValue("apkSha256", apk.Sha256);
        evidence.AddSafeValue("apkSigningDigest", apk.SigningDigest);
        evidence.AddSafeValue("windowsExeSha256", options.WindowsExeSha256);
        evidence.AddHash("androidSerialHash", options.AndroidSerial);
        evidence.AddSafeValue("fixtureSha256", fixtureSha256);

        // The E2E-only package may be cleared; the production package is never queried or changed.
        android.AssertPhysicalConnectedDevice();
        android.AssertInstalledPackage(apk);
        var androidDataCreated = false;
        var pushedFixture = false;
        try
        {
        android.ClearE2ePackageData();
        androidDataCreated = true;
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

            android.PushFixture(options.AttachmentFixturePath, marker);
            pushedFixture = true;
            StageAndSendAndroidAttachment(android, options, marker);
            SaveOpenAndVerifyWindowsAttachment(windows, options, marker, fixtureSha256);
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
            ReDownloadAndVerifyWindowsAttachment(restartedWindows, options, marker, fixtureSha256);
            evidence.AddBoolean("coldRestartPersistedMessages", true);
            evidence.AddBoolean("distinctWindowsPid", true);
            evidence.AddBoolean("windowsOpenSaveAndDecryptVerified", true);
        }

        evidence.AddBoolean("androidColdRestartNoCrash", true);
        evidence.AddBoolean("windowsColdRestartNoCrash", true);
        }
        finally
        {
            // These are run-owned paths/package data only.  Any cleanup failure fails the lane.
            if (pushedFixture) android.DeletePushedFixture(marker);
            if (File.Exists(options.SavedAttachmentPath)) File.Delete(options.SavedAttachmentPath);
            if (androidDataCreated) android.ClearE2ePackageData();
            if (Directory.Exists(isolatedWindowsAppData)) Directory.Delete(isolatedWindowsAppData, recursive: true);
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
        var nonexistent = "05" + new string('0', 64);
        StrictCrossPlatformContracts.RequireSessionId(nonexistent, "negative test identity");
        android.Type(options.App("NewConversation.SessionId"), nonexistent);
        android.Tap(options.App("NewConversation.Start"));
        var error = android.WaitForResource(options.App("NewConversation.Error"), TimeSpan.FromSeconds(20));
        Assert.False(string.IsNullOrWhiteSpace(error.Text));
        Assert.Null(android.FindOptional(options.App("Chat.Draft")));
        Assert.Null(android.FindOptional(options.App("Chat.MessageBody")));
        android.Tap(options.App("NewConversation.Back"));
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
        android.Tap(options.PickerDownloadsResourceId);
        // The picker filename is asserted from the exact configured resource-id; no text-only selector is used.
        android.TapExactResourceIdWithExactText(options.PickerFileResourceId, marker);
        android.Tap(options.PickerConfirmResourceId);
        android.WaitForText(options.App("Chat.StagedAttachmentFilename"), marker, TimeSpan.FromSeconds(30));
        android.Tap(options.App("Chat.Send"));
    }

    private static void SaveOpenAndVerifyWindowsAttachment(WindowsUiSmokeTests.WindowsUiTestSession windows, CrossPlatformOptions options, string marker, string expectedSha256)
    {
        var attachment = Require(windows.WaitForAutomationIdWithName("DesktopWorkspace.DirectAttachmentFilename", marker, TimeSpan.FromSeconds(45)), "DesktopWorkspace.DirectAttachmentFilename");
        windows.ActivateExact(attachment);
        windows.ActivateExact(Require(windows.WaitForAutomationId("DesktopWorkspace.AttachmentOpen", TimeSpan.FromSeconds(15)), "DesktopWorkspace.AttachmentOpen"));
        // Open may replace the attachment menu.  Re-select the same exact correlated filename before Save.
        attachment = Require(windows.WaitForAutomationIdWithName("DesktopWorkspace.DirectAttachmentFilename", marker, TimeSpan.FromSeconds(15)), "DesktopWorkspace.DirectAttachmentFilename");
        windows.ActivateExact(attachment);
        windows.ActivateExact(Require(windows.WaitForAutomationId("DesktopWorkspace.AttachmentSave", TimeSpan.FromSeconds(15)), "DesktopWorkspace.AttachmentSave"));
        var fileName = Require(windows.WaitForOwnedDialogAutomationId(options.WindowsSaveFilenameAutomationId, TimeSpan.FromSeconds(15)), options.WindowsSaveFilenameAutomationId).AsTextBox();
        fileName.Text = options.SavedAttachmentPath;
        windows.ActivateExact(Require(windows.WaitForOwnedDialogAutomationId(options.WindowsSaveConfirmAutomationId, TimeSpan.FromSeconds(15)), options.WindowsSaveConfirmAutomationId));
        WaitForFile(options.SavedAttachmentPath, TimeSpan.FromSeconds(30));
        Assert.Equal(expectedSha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(options.SavedAttachmentPath))));
        File.Delete(options.SavedAttachmentPath);
    }

    private static void ReDownloadAndVerifyWindowsAttachment(WindowsUiSmokeTests.WindowsUiTestSession windows, CrossPlatformOptions options, string marker, string expectedSha256)
    {
        var attachment = Require(windows.WaitForAutomationIdWithName("DesktopWorkspace.DirectAttachmentFilename", marker, TimeSpan.FromSeconds(30)), "DesktopWorkspace.DirectAttachmentFilename");
        windows.ActivateExact(attachment);
        windows.ActivateExact(Require(windows.WaitForAutomationId("DesktopWorkspace.AttachmentSave", TimeSpan.FromSeconds(15)), "DesktopWorkspace.AttachmentSave"));
        Require(windows.WaitForOwnedDialogAutomationId(options.WindowsSaveFilenameAutomationId, TimeSpan.FromSeconds(15)), options.WindowsSaveFilenameAutomationId).AsTextBox().Text = options.SavedAttachmentPath;
        windows.ActivateExact(Require(windows.WaitForOwnedDialogAutomationId(options.WindowsSaveConfirmAutomationId, TimeSpan.FromSeconds(15)), options.WindowsSaveConfirmAutomationId));
        WaitForFile(options.SavedAttachmentPath, TimeSpan.FromSeconds(30));
        Assert.Equal(expectedSha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(options.SavedAttachmentPath))));
    }

    private static void WaitForWindowsText(WindowsUiSmokeTests.WindowsUiTestSession windows, string automationId, string text) =>
        Assert.NotNull(windows.WaitForAutomationIdWithName(automationId, text, TimeSpan.FromSeconds(45)));

    private static void WaitForFile(string path, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (!File.Exists(path) && DateTime.UtcNow < until) Thread.Sleep(200);
        Assert.True(File.Exists(path), "The exact Windows Save dialog did not create the requested file.");
    }

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
        "Chat.Attach", "Chat.StagedAttachmentFilename"
    ];
    private readonly Dictionary<string, string> androidSelectors;
    private CrossPlatformOptions(string serial, string adbPath, string apkPath, string aaptPath, string apksignerPath, string fixturePath, string artifactDirectory, Dictionary<string, string> selectors, string pickerDownloads, string pickerFile, string pickerConfirm, string saveFilename, string saveConfirm, string fingerprint, string model, string sourceCommit, string windowsExeSha256)
    {
        AndroidSerial = serial; AdbPath = adbPath; ApkPath = apkPath; AaptPath = aaptPath; ApksignerPath = apksignerPath; AttachmentFixturePath = fixturePath; ArtifactDirectory = artifactDirectory;
        androidSelectors = selectors; PickerDownloadsResourceId = pickerDownloads; PickerFileResourceId = pickerFile; PickerConfirmResourceId = pickerConfirm; WindowsSaveFilenameAutomationId = saveFilename; WindowsSaveConfirmAutomationId = saveConfirm;
        SavedAttachmentPath = Path.Combine(artifactDirectory, "cross-platform-download.bin");
        ResultPath = Path.Combine(artifactDirectory, "cross-platform-ui-result.json"); InvocationId = Guid.NewGuid().ToString("N"); DeviceFingerprint = fingerprint; DeviceModel = model; SourceCommit = sourceCommit; WindowsExeSha256 = windowsExeSha256;
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
    internal string PickerConfirmResourceId { get; }
    internal string WindowsSaveFilenameAutomationId { get; }
    internal string WindowsSaveConfirmAutomationId { get; }
    internal string SavedAttachmentPath { get; }
    internal string ResultPath { get; }
    internal string InvocationId { get; }
    internal string DeviceFingerprint { get; }
    internal string DeviceModel { get; }
    internal string SourceCommit { get; }
    internal string WindowsExeSha256 { get; }
    internal string App(string role) => androidSelectors.TryGetValue(role, out var id) ? id : throw new InvalidOperationException($"Missing Android selector for {role}.");

    internal static string? NotRunReason()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("DEEP_STRICT_CROSS_PLATFORM_UI"), "1", StringComparison.Ordinal)) return "NOT-RUN: set DEEP_STRICT_CROSS_PLATFORM_UI=1 on an approved unlocked physical Android and Windows UI lab.";
        var required = new[] { "DEEP_E2E_ANDROID_SERIAL", "DEEP_E2E_ADB", "DEEP_E2E_ANDROID_APK", "DEEP_E2E_AAPT", "DEEP_E2E_APKSIGNER", "DEEP_E2E_ATTACHMENT_FIXTURE", "DEEP_E2E_ARTIFACTS", "DEEP_E2E_ANDROID_SELECTORS_JSON", "DEEP_E2E_ANDROID_PICKER_DOWNLOADS_ID", "DEEP_E2E_ANDROID_PICKER_FILE_ID", "DEEP_E2E_ANDROID_PICKER_CONFIRM_ID", "DEEP_E2E_WINDOWS_SAVE_FILENAME_AUTOMATION_ID", "DEEP_E2E_WINDOWS_SAVE_CONFIRM_AUTOMATION_ID", "DEEP_MAUI_EXE", "DEEP_E2E_APPDATA_ROOT", "DEEP_E2E_BOOTSTRAP", "DEEP_E2E_ANDROID_FINGERPRINT", "DEEP_E2E_ANDROID_MODEL", "DEEP_E2E_SOURCE_COMMIT", "DEEP_E2E_WINDOWS_EXE_SHA256" };
        var missing = required.Where(key => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key))).ToArray();
        return missing.Length == 0 ? null : "NOT-RUN: missing physical lane prerequisites: " + string.Join(", ", missing);
    }

    internal static CrossPlatformOptions Load()
    {
        if (NotRunReason() is { } reason) throw new InvalidOperationException(reason);
        if (!string.Equals(Environment.GetEnvironmentVariable("DEEP_E2E_BOOTSTRAP"), "live", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Physical cross-platform UI requires DEEP_E2E_BOOTSTRAP=live; stub is not evidence.");
        var selectors = ParseSelectors(Environment.GetEnvironmentVariable("DEEP_E2E_ANDROID_SELECTORS_JSON")!);
        var pickerDownloads = Environment.GetEnvironmentVariable("DEEP_E2E_ANDROID_PICKER_DOWNLOADS_ID")!; var pickerFile = Environment.GetEnvironmentVariable("DEEP_E2E_ANDROID_PICKER_FILE_ID")!; var pickerConfirm = Environment.GetEnvironmentVariable("DEEP_E2E_ANDROID_PICKER_CONFIRM_ID")!;
        StrictCrossPlatformContracts.ValidateResourceId(pickerDownloads, "picker Downloads selector"); StrictCrossPlatformContracts.ValidateResourceId(pickerFile, "picker file selector"); StrictCrossPlatformContracts.ValidateResourceId(pickerConfirm, "picker confirm selector");
        var apk = RequireAbsoluteFile("DEEP_E2E_ANDROID_APK"); var fixture = RequireAbsoluteFile("DEEP_E2E_ATTACHMENT_FIXTURE"); var adb = RequireAbsoluteFile("DEEP_E2E_ADB"); var aapt = RequireAbsoluteFile("DEEP_E2E_AAPT"); var apksigner = RequireAbsoluteFile("DEEP_E2E_APKSIGNER"); var artifacts = Path.GetFullPath(Environment.GetEnvironmentVariable("DEEP_E2E_ARTIFACTS")!);
        var windowsExe = Environment.GetEnvironmentVariable("DEEP_MAUI_EXE")!;
        var appDataRoot = Environment.GetEnvironmentVariable("DEEP_E2E_APPDATA_ROOT")!;
        if (!Path.IsPathFullyQualified(windowsExe) || !File.Exists(windowsExe) || !Path.IsPathFullyQualified(appDataRoot)) throw new InvalidOperationException("Configured Windows executable or app-data root is invalid.");
        var windowsHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(windowsExe)));
        if (!string.Equals(windowsHash, Environment.GetEnvironmentVariable("DEEP_E2E_WINDOWS_EXE_SHA256"), StringComparison.Ordinal)) throw new InvalidOperationException("Configured Windows executable hash does not bind the launched binary.");
        Directory.CreateDirectory(artifacts);
        var commit = Environment.GetEnvironmentVariable("DEEP_E2E_SOURCE_COMMIT")!;
        if (!System.Text.RegularExpressions.Regex.IsMatch(commit, "^[a-f0-9]{40}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)) throw new InvalidOperationException("Source commit binding must be a lowercase 40-hex commit.");
        return new CrossPlatformOptions(Environment.GetEnvironmentVariable("DEEP_E2E_ANDROID_SERIAL")!, adb, apk, aapt, apksigner, fixture, artifacts, selectors, pickerDownloads, pickerFile, pickerConfirm, Environment.GetEnvironmentVariable("DEEP_E2E_WINDOWS_SAVE_FILENAME_AUTOMATION_ID")!, Environment.GetEnvironmentVariable("DEEP_E2E_WINDOWS_SAVE_CONFIRM_AUTOMATION_ID")!, Environment.GetEnvironmentVariable("DEEP_E2E_ANDROID_FINGERPRINT")!, Environment.GetEnvironmentVariable("DEEP_E2E_ANDROID_MODEL")!, commit, windowsHash);
    }
    internal StrictCrossPlatformContracts.ApkMetadata ReadAndValidateApkMetadata()
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(ApkPath)));
        var output = AndroidUiautomatorClient.Run(AaptPath, "dump", "badging", ApkPath).Output;
        var metadata = StrictCrossPlatformContracts.ApkMetadata.ParseAaptBadging(output, sha);
        if (!string.Equals(metadata.PackageName, StrictCrossPlatformContracts.AndroidPackage, StringComparison.Ordinal)) throw new InvalidOperationException("APK package is not the dedicated E2E package.");
        var signer = AndroidUiautomatorClient.Run(ApksignerPath, "verify", "--print-certs", ApkPath);
        if (signer.ExitCode != 0) throw new InvalidOperationException("apksigner could not verify the configured APK.");
        var digest = System.Text.RegularExpressions.Regex.Match(signer.Output, "SHA-256[^:]*digest:\\s*(?<digest>[A-Fa-f0-9:]{64,95})", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!digest.Success) throw new InvalidOperationException("apksigner did not emit a certificate SHA-256 digest.");
        return metadata.WithSigningDigest(digest.Groups["digest"].Value.Replace(":", string.Empty, StringComparison.Ordinal).ToLowerInvariant());
    }

    private static string RequireAbsoluteFile(string key) { var path = Environment.GetEnvironmentVariable(key)!; if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new InvalidOperationException($"{key} must be an existing absolute path."); return Path.GetFullPath(path); }
    private static Dictionary<string, string> ParseSelectors(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json); if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) throw new InvalidOperationException("Android selector JSON must be an object.");
        var items = doc.RootElement.EnumerateObject().ToArray();
        if (items.Length != RequiredAppRoles.Length || items.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != items.Length || !items.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(RequiredAppRoles.OrderBy(x => x, StringComparer.Ordinal))) throw new InvalidOperationException("Android selector JSON must contain exactly the canonical role key set with no duplicates or extras.");
        var map = new Dictionary<string, string>(StringComparer.Ordinal); foreach (var item in items) { if (item.Value.ValueKind != System.Text.Json.JsonValueKind.String) throw new InvalidOperationException("Android selector values must be literal strings."); var value = item.Value.GetString()!; StrictCrossPlatformContracts.ValidateResourceId(value, "Android selector"); if (!value.StartsWith(StrictCrossPlatformContracts.AndroidPackage + ":id/", StringComparison.Ordinal) || !map.TryAdd(item.Name, value)) throw new InvalidOperationException("Android selector is outside the E2E package or duplicated."); }
        if (map.Values.Distinct(StringComparer.Ordinal).Count() != map.Count) throw new InvalidOperationException("Android selector literals must be unique."); return map;
    }
}

internal sealed class AndroidUiautomatorClient
{
    private readonly CrossPlatformOptions options;
    internal AndroidUiautomatorClient(CrossPlatformOptions options) => this.options = options;
    internal void AssertPhysicalConnectedDevice() { var state = Adb("get-state").Output.Trim(); if (!string.Equals(state, "device", StringComparison.Ordinal)) throw new InvalidOperationException("Configured Android serial is not an authorized device."); var qemu = Adb("shell", "getprop", "ro.kernel.qemu").Output.Trim(); if (qemu == "1") throw new InvalidOperationException("Emulators are forbidden in the physical cross-platform lane."); if (!string.Equals(Adb("shell", "getprop", "ro.build.fingerprint").Output.Trim(), options.DeviceFingerprint, StringComparison.Ordinal) || !string.Equals(Adb("shell", "getprop", "ro.product.model").Output.Trim(), options.DeviceModel, StringComparison.Ordinal)) throw new InvalidOperationException("Configured approved physical device fingerprint/model does not match the selected serial."); }
    internal void AssertInstalledPackage(StrictCrossPlatformContracts.ApkMetadata apk)
    {
        var path = Adb("shell", "pm", "path", StrictCrossPlatformContracts.AndroidPackage).Output;
        var details = Adb("shell", "dumpsys", "package", StrictCrossPlatformContracts.AndroidPackage).Output;
        var installedPath = path.Split('\n').Select(x => x.Trim()).SingleOrDefault(x => x.StartsWith("package:", StringComparison.Ordinal))?.Substring("package:".Length) ?? throw new InvalidOperationException("The E2E package has no unique installed APK path.");
        var installedSha = Adb("shell", "sha256sum", installedPath).Output.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
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
    private ProcessResult Adb(params string[] args) => Run(options.AdbPath, ["-s", options.AndroidSerial, .. args]);
    internal static ProcessResult Run(string fileName, params string[] args) { using var process = new Process { StartInfo = new ProcessStartInfo { FileName = fileName, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } }; foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg); process.Start(); var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit(); return new ProcessResult(process.ExitCode, output, error); }
    private static void RequireSuccess(ProcessResult result) { if (result.ExitCode != 0) throw new InvalidOperationException("ADB/aapt command failed without emitting its raw output into evidence."); }
    internal sealed record ProcessResult(int ExitCode, string Output, string Error);
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
