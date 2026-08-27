using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using Xunit;

namespace Deep.Client.Maui.UiTests;

public sealed class WindowsUiSmokeTests
{
    private const string AuthenticatedControlId = "Conversations.NewConversation";
    private static readonly UiBaseline Baseline = LoadBaseline();

    internal readonly record struct FilePickerInvocationFacts(
        IntPtr MainWindowHandle,
        int ApplicationProcessId,
        int UiaProcessId,
        int NativeProcessId,
        IntPtr ForegroundRootHandle,
        IntPtr LastActivePopupHandle,
        bool IsNativeVisible,
        bool IsNativeEnabled,
        bool IsUiaEnabled,
        bool IsUiaOffscreen,
        int VisibleOwnedPopupCount);

    internal readonly record struct FilePickerInvocationContext(
        IntPtr MainWindowHandle,
        int ApplicationProcessId,
        IntPtr ForegroundRootHandle);

    internal readonly record struct FilePickerWindowFacts(
        IntPtr WindowHandle,
        int UiaProcessId,
        int NativeProcessId,
        IntPtr OwnerHandle,
        IntPtr RootOwnerHandle,
        IntPtr ForegroundRootHandle,
        IntPtr LastActivePopupHandle,
        bool IsNativeVisible,
        bool IsNativeEnabled,
        bool IsUiaEnabled,
        bool IsUiaOffscreen,
        int FilenameEditorCount,
        int AffirmativeButtonCount);

    internal static bool IsSafeFilePickerInvocationPrecondition(
        FilePickerInvocationFacts facts) =>
        facts.MainWindowHandle != IntPtr.Zero &&
        facts.ApplicationProcessId > 0 &&
        facts.UiaProcessId == facts.ApplicationProcessId &&
        facts.NativeProcessId == facts.ApplicationProcessId &&
        facts.ForegroundRootHandle == facts.MainWindowHandle &&
        facts.LastActivePopupHandle == facts.MainWindowHandle &&
        facts.IsNativeVisible &&
        facts.IsNativeEnabled &&
        facts.IsUiaEnabled &&
        !facts.IsUiaOffscreen &&
        facts.VisibleOwnedPopupCount == 0;

    internal static bool IsOwnedForegroundFilePicker(
        FilePickerInvocationContext context,
        FilePickerWindowFacts facts) =>
        context.MainWindowHandle != IntPtr.Zero &&
        context.ApplicationProcessId > 0 &&
        context.ForegroundRootHandle == context.MainWindowHandle &&
        facts.WindowHandle != IntPtr.Zero &&
        facts.WindowHandle != context.MainWindowHandle &&
        facts.UiaProcessId > 0 &&
        facts.NativeProcessId == facts.UiaProcessId &&
        facts.OwnerHandle != IntPtr.Zero &&
        facts.RootOwnerHandle == context.MainWindowHandle &&
        facts.ForegroundRootHandle == facts.WindowHandle &&
        facts.ForegroundRootHandle != context.ForegroundRootHandle &&
        facts.LastActivePopupHandle == facts.WindowHandle &&
        facts.IsNativeVisible &&
        facts.IsNativeEnabled &&
        facts.IsUiaEnabled &&
        !facts.IsUiaOffscreen &&
        facts.FilenameEditorCount == 1 &&
        facts.AffirmativeButtonCount == 1;

    internal static int FindSingleOwnedForegroundFilePickerIndex(
        FilePickerInvocationContext context,
        IReadOnlyList<FilePickerWindowFacts> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var matches = candidates
            .Select((facts, index) => (facts, index))
            .Where(candidate => IsOwnedForegroundFilePicker(context, candidate.facts))
            .Select(candidate => candidate.index)
            .ToArray();
        return matches.Length switch
        {
            0 => -1,
            1 => matches[0],
            _ => throw new InvalidOperationException(
                "The app action exposed more than one owned foreground file picker.")
        };
    }

    [StrictWindowsUiFact]
    public void WelcomePageRendersInteractiveControlsInRealWindowsApp()
    {
        using var session = WindowsUiTestSession.CreateStrict();

        foreach (var id in Baseline.Welcome)
        {
            Assert.NotNull(session.WaitForAutomationId(id, TimeSpan.FromSeconds(20)));
        }

        var displayName = session.WaitForAutomationId("Welcome.DisplayName", TimeSpan.FromSeconds(5))?.AsTextBox();
        Assert.NotNull(displayName);
        Assert.True(displayName!.IsEnabled);
        displayName.Text = "UIAutomation";

        var create = session.WaitForAutomationId("Welcome.Create", TimeSpan.FromSeconds(5))?.AsButton();
        Assert.NotNull(create);
        Assert.True(
            Retry.WhileFalse(
                () => create!.IsEnabled,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(100),
                throwOnTimeout: false).Result);

        var restore = session.WaitForAutomationId("Welcome.Restore", TimeSpan.FromSeconds(5))?.AsButton();
        Assert.NotNull(restore);
        Assert.True(restore!.IsEnabled);

        session.FocusWindow();
        create!.Focus();
        Assert.True(displayName.Text.Length > 0);
        // UIA Invoke is the semantic button activation path. Coordinate-based Click
        // races MAUI/WinUI focus and layout updates and can land on the host surface
        // without executing the bound command.
        create.Invoke();
        var authenticatedControl = session.WaitForAutomationId(
            AuthenticatedControlId,
            TimeSpan.FromSeconds(20))?.AsButton();
        Assert.NotNull(authenticatedControl);
        Assert.True(authenticatedControl!.IsEnabled);
        Assert.Null(session.FindAutomationId("Welcome.Create"));

        session.WriteSuccessEvidence();
    }

    private static UiBaseline LoadBaseline()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Baselines", "session-ui-baseline.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<UiBaseline>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("UI baseline file could not be parsed.");
    }

    private sealed record UiBaseline(string[] Welcome);

    // The physical Android↔Windows lane deliberately reuses this exact-PID UIA3 session;
    // it must not attach to an arbitrary desktop window.
    internal sealed class WindowsUiTestSession : IDisposable
    {
        private const string AppPathKey = "DEEP_MAUI_EXE";
        private const string ArtifactDirectoryKey = "DEEP_E2E_ARTIFACTS";
        private const string AppDataDirectoryKey = "DEEP_E2E_APPDATA_ROOT";
        private const string BootstrapKey = "DEEP_E2E_BOOTSTRAP";
        private const string CaptureFailureKey = "DEEP_E2E_CAPTURE_STUB_WELCOME_FAILURE";
        private static readonly HashSet<string> AllowedEvidenceIds =
        [
            .. Baseline.Welcome,
            "Page.DesktopWorkspace",
            "Page.Conversations",
            "Conversations.Root",
            "PhysicalE2E.RouteNodeMarker",
            AuthenticatedControlId
        ];

        private readonly Application application;
        private readonly UIA3Automation automation;
        private readonly Window window;
        private readonly string artifactDirectory;

        private WindowsUiTestSession(
            Application application,
            UIA3Automation automation,
            Window window,
            string artifactDirectory)
        {
            this.application = application;
            this.automation = automation;
            this.window = window;
            this.artifactDirectory = artifactDirectory;
        }

        internal int ProcessId => application.ProcessId;

        public static WindowsUiTestSession CreateStrict() => CreateStrict(appDataDirectoryOverride: null);

        internal static WindowsUiTestSession CreateStrictWithIsolatedAppData()
        {
            return CreateStrict(CreateIsolatedAppDataDirectory());
        }

        internal static string CreateIsolatedAppDataDirectory()
        {
            var root = RequireDirectorySetting(AppDataDirectoryKey);
            return Path.Combine(root, $"cross-platform-{Guid.NewGuid():N}");
        }

        internal static WindowsUiTestSession CreateStrictWithAppData(string appDataDirectory) =>
            CreateStrict(Path.GetFullPath(appDataDirectory));

        private static WindowsUiTestSession CreateStrict(string? appDataDirectoryOverride)
        {
            if (!System.OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("The strict Windows UI lane requires Windows.");
            }

            var appPath = RequireExistingFile(AppPathKey);
            var artifactDirectory = RequireDirectorySetting(ArtifactDirectoryKey);
            var appDataDirectory = appDataDirectoryOverride ?? RequireDirectorySetting(AppDataDirectoryKey);
            Directory.CreateDirectory(artifactDirectory);
            Directory.CreateDirectory(appDataDirectory);

            var startInfo = new ProcessStartInfo
            {
                FileName = appPath,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(appPath)
                    ?? throw new InvalidOperationException("The MAUI executable has no parent directory.")
            };
            // CreateStrict is the only supported Windows physical UI lane. The
            // application deliberately ignores DEEP_E2E_APPDATA_ROOT unless this
            // guard is present, so pass it to the spawned process rather than
            // relying on ambient test-host state.
            startInfo.Environment[StrictLaneEnvironment.WindowsUiEnabledKey] = "1";
            startInfo.Environment[BootstrapKey] = RequireSetting(BootstrapKey);
            startInfo.Environment[AppDataDirectoryKey] = appDataDirectory;

            if (string.Equals(startInfo.Environment[BootstrapKey], "stub", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var key in StrictLaneEnvironment.EndpointKeys)
                {
                    startInfo.Environment.Remove(key);
                }
            }

            var application = LaunchAndBindExactApplication(startInfo, appPath);
            var automation = new UIA3Automation();
            try
            {
                AssertLaunchedBinaryBinding(application, appPath);
                var result = Retry.WhileNull(
                    () => GetPidBoundMainWindow(application, automation),
                    timeout: TimeSpan.FromSeconds(30),
                    interval: TimeSpan.FromMilliseconds(250),
                    throwOnTimeout: false);
                var window = result.Result;
                if (window is null)
                {
                    throw new InvalidOperationException("The real MAUI process did not expose a nonzero top-level window.");
                }

                return new WindowsUiTestSession(application, automation, window, artifactDirectory);
            }
            catch (Exception exception)
            {
                WriteLaunchFailure(artifactDirectory, application, exception);
                automation.Dispose();
                CloseApplication(application);
                throw;
            }
        }

        private static Application LaunchAndBindExactApplication(
            ProcessStartInfo startInfo,
            string expectedPath)
        {
            if (FindExactBinaryProcessIds(expectedPath, DateTime.MinValue).Count != 0)
            {
                throw new InvalidOperationException(
                    "An existing target Windows process would make strict launch binding ambiguous.");
            }

            var launchedAfterUtc = DateTime.UtcNow;
            var launcher = Application.Launch(startInfo);
            if (IsExactBinary(launcher.ProcessId, expectedPath))
            {
                return launcher;
            }

            int? exactProcessId = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                var candidates = FindExactBinaryProcessIds(expectedPath, launchedAfterUtc);
                if (candidates.Count > 1)
                {
                    launcher.Dispose();
                    throw new InvalidOperationException(
                        "Windows launch produced more than one new exact target process.");
                }

                if (candidates.Count == 1)
                {
                    exactProcessId = candidates[0];
                    break;
                }

                Thread.Sleep(100);
            }

            // The transient launcher is never used as application authority. It
            // may already have exited or handed off activation, so only release
            // the wrapper; never risk killing a reused PID.
            launcher.Dispose();
            if (exactProcessId is null)
            {
                throw new InvalidOperationException(
                    "Windows launch did not produce one new exact target process.");
            }

            return Application.Attach(exactProcessId.Value);
        }

        private static IReadOnlyList<int> FindExactBinaryProcessIds(
            string expectedPath,
            DateTime launchedAfterUtc)
        {
            var name = Path.GetFileNameWithoutExtension(expectedPath);
            var matches = new List<int>();
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (process.StartTime.ToUniversalTime() >= launchedAfterUtc &&
                            string.Equals(
                                Path.GetFullPath(process.MainModule?.FileName ?? string.Empty),
                                Path.GetFullPath(expectedPath),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            matches.Add(process.Id);
                        }
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                    }
                }
            }

            return matches;
        }

        private static bool IsExactBinary(int processId, string expectedPath)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return string.Equals(
                    Path.GetFullPath(process.MainModule?.FileName ?? string.Empty),
                    Path.GetFullPath(expectedPath),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        public AutomationElement? WaitForAutomationId(string automationId, TimeSpan timeout)
        {
            var result = Retry.WhileNull(
                () => FindAutomationIdForRetry(automationId),
                timeout,
                TimeSpan.FromMilliseconds(200),
                throwOnTimeout: false);
            if (result.Result is not null)
            {
                return result.Result;
            }

            WriteFailureEvidence($"Element '{automationId}' was not found.");
            return null;
        }

        public AutomationElement? FindAutomationId(string automationId) =>
            CurrentWindow().FindFirstDescendant(condition => condition.ByAutomationId(automationId));

        private AutomationElement? FindAutomationIdForRetry(string automationId)
        {
            try
            {
                return FindAutomationId(automationId);
            }
            catch (COMException) when (!application.HasExited)
            {
                // WinUI can transiently invalidate its UIA provider while replacing
                // a navigation subtree. Retry only inside the caller's existing
                // deadline and only while the exact launched PID remains alive.
                return null;
            }
        }

        internal IReadOnlySet<string> FindPresentAutomationIds(
            IReadOnlyCollection<string> automationIds)
        {
            ArgumentNullException.ThrowIfNull(automationIds);
            var expected = automationIds.ToHashSet(StringComparer.Ordinal);
            return CurrentWindow()
                .FindAllDescendants()
                .Select(static candidate =>
                    candidate.Properties.AutomationId.ValueOrDefault)
                .Where(value => value is not null && expected.Contains(value))
                .Select(static value => value!)
                .ToHashSet(StringComparer.Ordinal);
        }

        internal int CountAutomationId(string automationId) => CurrentWindow()
            .FindAllDescendants(condition => condition.ByAutomationId(automationId))
            .Length;

        internal IReadOnlySet<string> SnapshotAutomationIdNames(string automationId)
        {
            var names = CurrentWindow()
                .FindAllDescendants(condition => condition.ByAutomationId(automationId))
                .Select(static candidate => candidate.Properties.Name.ValueOrDefault ?? string.Empty)
                .ToArray();
            if (names.Any(string.IsNullOrWhiteSpace)
                || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
                throw new InvalidOperationException(
                    "Windows correlated automation names must be non-empty and unique.");
            return names.ToHashSet(StringComparer.Ordinal);
        }

        internal IReadOnlyList<string> SnapshotAutomationIdNameMultiset(string automationId)
        {
            var names = CurrentWindow()
                .FindAllDescendants(condition => condition.ByAutomationId(automationId))
                .Select(static candidate => candidate.Properties.Name.ValueOrDefault ?? string.Empty)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (names.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException(
                    "Windows correlated automation names must be non-empty.");
            return names;
        }

        internal void WaitForExactAutomationIdNameMultiset(
            string automationId,
            IReadOnlyList<string> expected,
            TimeSpan timeout)
        {
            var orderedExpected = expected.Order(StringComparer.Ordinal).ToArray();
            var until = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < until)
            {
                var actual = SnapshotAutomationIdNameMultiset(automationId);
                if (actual.Count > orderedExpected.Length)
                    throw new InvalidOperationException(
                        "Windows rendered unexpected duplicate correlated automation names.");
                if (actual.SequenceEqual(orderedExpected, StringComparer.Ordinal)) return;
                Thread.Sleep(200);
            }
            throw new TimeoutException(
                "Windows did not preserve the exact correlated automation-name multiset.");
        }

        internal void WaitForExactAutomationIdNameSet(
            string automationId,
            IReadOnlySet<string> expected,
            TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            Exception? last = null;
            while (DateTime.UtcNow < until)
            {
                try
                {
                    var actual = SnapshotAutomationIdNames(automationId);
                    if (actual.Count > expected.Count)
                        throw new InvalidOperationException(
                            "Windows rendered unexpected correlated automation names.");
                    if (actual.SetEquals(expected)) return;
                }
                catch (COMException exception) when (!application.HasExited)
                {
                    last = exception;
                }
                Thread.Sleep(200);
            }
            throw new InvalidOperationException(
                "Windows did not preserve the exact correlated automation-name set.", last);
        }

        internal void WaitForExactAutomationIdCount(
            string automationId, int expected, TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < until)
            {
                var count = CountAutomationId(automationId);
                if (count == expected) return;
                if (count > expected)
                    throw new InvalidOperationException(
                        "Windows rendered duplicate correlated automation elements.");
                Thread.Sleep(200);
            }
            throw new InvalidOperationException(
                "Windows did not render the exact expected automation-element count.");
        }

        internal AutomationElement? WaitForOneNewAutomationId(
            string automationId,
            int previousCount,
            TimeSpan timeout)
        {
            var result = Retry.WhileNull(
                () => FindOneNewAutomationIdForRetry(automationId, previousCount),
                timeout,
                TimeSpan.FromMilliseconds(200),
                throwOnTimeout: false);
            return result.Result;
        }

        internal AutomationElement? WaitForLastAutomationIdContainingDescendant(
            string ancestorAutomationId,
            string descendantAutomationId,
            TimeSpan timeout)
        {
            var result = Retry.WhileNull(
                () => FindLastAutomationIdContainingDescendantForRetry(
                    ancestorAutomationId, descendantAutomationId),
                timeout,
                TimeSpan.FromMilliseconds(200),
                throwOnTimeout: false);
            return result.Result;
        }

        private AutomationElement? FindLastAutomationIdContainingDescendantForRetry(
            string ancestorAutomationId,
            string descendantAutomationId)
        {
            try
            {
                var candidates = CurrentWindow()
                    .FindAllDescendants(condition => condition.ByAutomationId(ancestorAutomationId));
                if (candidates.Length == 0)
                {
                    return null;
                }

                var last = candidates
                    .OrderBy(candidate => candidate.BoundingRectangle.Top)
                    .Last();
                var descendants = last.FindAllDescendants(
                    condition => condition.ByAutomationId(descendantAutomationId));
                if (descendants.Length > 1)
                {
                    throw new InvalidOperationException(
                        $"Last {ancestorAutomationId} contained more than one {descendantAutomationId}.");
                }

                return descendants.SingleOrDefault();
            }
            catch (COMException) when (!application.HasExited)
            {
                return null;
            }
        }

        private AutomationElement? FindOneNewAutomationIdForRetry(
            string automationId,
            int previousCount)
        {
            try
            {
                var candidates = CurrentWindow()
                    .FindAllDescendants(condition => condition.ByAutomationId(automationId));
                if (candidates.Length > previousCount + 1)
                {
                    throw new InvalidOperationException(
                        "More than one new exact Windows automation element appeared.");
                }

                return candidates.Length == previousCount + 1
                    ? candidates[^1]
                    : null;
            }
            catch (COMException) when (!application.HasExited)
            {
                return null;
            }
        }

        internal AutomationElement? WaitForAutomationIdWithName(string automationId, string name, TimeSpan timeout)
        {
            var result = Retry.WhileNull(
                () => FindAutomationIdWithNameForRetry(automationId, name),
                timeout,
                TimeSpan.FromMilliseconds(200),
                throwOnTimeout: false);
            return result.Result;
        }

        internal AutomationElement? WaitForAutomationIdWithNameContaining(
            string automationId,
            string exactMarker,
            TimeSpan timeout)
        {
            if (string.IsNullOrEmpty(exactMarker))
                throw new ArgumentException("A nonempty exact marker is required.", nameof(exactMarker));
            var result = Retry.WhileNull(
                () =>
                {
                    var matches = CurrentWindow().FindAllDescendants(
                        condition => condition.ByAutomationId(automationId));
                    if (matches.Length > 1)
                        throw new InvalidOperationException(
                            "AutomationId was not unique while waiting for an exact name marker.");
                    return matches.Length == 1 &&
                        (matches[0].Properties.Name.ValueOrDefault ?? string.Empty)
                            .Contains(exactMarker, StringComparison.Ordinal)
                        ? matches[0]
                        : null;
                }, timeout, TimeSpan.FromMilliseconds(200), throwOnTimeout: false);
            return result.Result;
        }

        internal AutomationElement? WaitForCorrelatedDescendant(
            string ancestorAutomationId,
            string correlationAutomationId,
            string correlationName,
            string targetAutomationId,
            TimeSpan timeout,
            string? targetName = null)
            => WaitForCorrelatedDescendantCore(
                ancestorAutomationId,
                correlationAutomationId,
                correlationName,
                targetAutomationId,
                timeout,
                targetName is null
                    ? null
                    : new HashSet<string>(StringComparer.Ordinal) { targetName });

        internal AutomationElement? WaitForCorrelatedDescendantWithAnyName(
            string ancestorAutomationId,
            string correlationAutomationId,
            string correlationName,
            string targetAutomationId,
            TimeSpan timeout,
            params string[] targetNames)
        {
            ArgumentNullException.ThrowIfNull(targetNames);
            var acceptedNames = targetNames.ToHashSet(StringComparer.Ordinal);
            if (acceptedNames.Count == 0 || acceptedNames.Count != targetNames.Length)
                throw new ArgumentException(
                    "At least one unique exact target name is required.", nameof(targetNames));
            return WaitForCorrelatedDescendantCore(
                ancestorAutomationId,
                correlationAutomationId,
                correlationName,
                targetAutomationId,
                timeout,
                acceptedNames);
        }

        internal int CountCorrelatedAncestors(
            string ancestorAutomationId,
            string correlationAutomationId,
            string correlationName) =>
            FindCorrelatedAncestors(
                ancestorAutomationId,
                correlationAutomationId,
                correlationName).Length;

        internal AutomationElement? WaitForNewCorrelatedDescendantWithAnyName(
            string ancestorAutomationId,
            string correlationAutomationId,
            string correlationName,
            int previousCount,
            string targetAutomationId,
            TimeSpan timeout,
            params string[] targetNames)
        {
            if (previousCount < 0)
                throw new ArgumentOutOfRangeException(nameof(previousCount));
            ArgumentNullException.ThrowIfNull(targetNames);
            var acceptedNames = targetNames.ToHashSet(StringComparer.Ordinal);
            if (acceptedNames.Count == 0 || acceptedNames.Count != targetNames.Length)
                throw new ArgumentException(
                    "At least one unique exact target name is required.", nameof(targetNames));
            var result = Retry.WhileNull(
                () =>
                {
                    try
                    {
                        var ancestors = FindCorrelatedAncestors(
                            ancestorAutomationId,
                            correlationAutomationId,
                            correlationName);
                        if (ancestors.Length > previousCount + 1)
                            throw new InvalidOperationException(
                                "More than one new correlated Windows element appeared.");
                        if (ancestors.Length != previousCount + 1) return null;
                        var target = ancestors[^1].FindAllDescendants(
                            condition => condition.ByAutomationId(targetAutomationId));
                        if (target.Length != 1) return null;
                        return acceptedNames.Contains(
                            target[0].Properties.Name.ValueOrDefault ?? string.Empty)
                                ? target[0]
                                : null;
                    }
                    catch (COMException) when (!application.HasExited) { return null; }
                },
                timeout,
                TimeSpan.FromMilliseconds(200),
                throwOnTimeout: false);
            return result.Result;
        }

        private AutomationElement? WaitForCorrelatedDescendantCore(
            string ancestorAutomationId,
            string correlationAutomationId,
            string correlationName,
            string targetAutomationId,
            TimeSpan timeout,
            IReadOnlySet<string>? acceptedTargetNames)
        {
            var result = Retry.WhileNull(
                () =>
                {
                    try
                    {
                        var ancestors = FindCorrelatedAncestors(
                            ancestorAutomationId,
                            correlationAutomationId,
                            correlationName);
                        if (ancestors.Length != 1) return null;
                        var targets = ancestors[0].FindAllDescendants(
                            condition => condition.ByAutomationId(targetAutomationId));
                        if (targets.Length != 1) return null;
                        return acceptedTargetNames is null || acceptedTargetNames.Contains(
                            targets[0].Properties.Name.ValueOrDefault ?? string.Empty)
                                ? targets[0]
                                : null;
                    }
                    catch (COMException) when (!application.HasExited) { return null; }
                },
                timeout,
                TimeSpan.FromMilliseconds(200),
                throwOnTimeout: false);
            return result.Result;
        }

        private AutomationElement[] FindCorrelatedAncestors(
            string ancestorAutomationId,
            string correlationAutomationId,
            string correlationName) =>
            CurrentWindow().FindAllDescendants(
                    condition => condition.ByAutomationId(ancestorAutomationId))
                .Where(ancestor => ancestor.FindAllDescendants(
                        condition => condition.ByAutomationId(correlationAutomationId))
                    .Any(candidate => string.Equals(
                        candidate.Properties.Name.ValueOrDefault,
                        correlationName, StringComparison.Ordinal)))
                .OrderBy(ancestor => ancestor.BoundingRectangle.Top)
                .ToArray();

        internal AutomationElement WaitForExactButtonName(string name, TimeSpan timeout)
        {
            var result = Retry.WhileNull(
                () =>
                {
                    var matches = CurrentWindow().FindAllDescendants()
                        .Where(element => element.ControlType == ControlType.Button
                            && string.Equals(element.Properties.Name.ValueOrDefault,
                                name, StringComparison.Ordinal))
                        .ToArray();
                    return matches.Length == 1 ? matches[0] : null;
                }, timeout, TimeSpan.FromMilliseconds(200), throwOnTimeout: false);
            return result.Result ?? throw new InvalidOperationException(
                "The app did not expose one exact requested action-sheet button.");
        }

        internal FilePickerInvocationContext CaptureFilePickerInvocationContext()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            FilePickerInvocationFacts facts = default;
            do
            {
                var mainWindow = CurrentWindow();
                var mainHandle = mainWindow.Properties.NativeWindowHandle.ValueOrDefault;
                if (mainHandle != IntPtr.Zero)
                {
                    RestoreExactForegroundWindow(mainHandle);
                }
                mainWindow.Focus();
                Thread.Sleep(TimeSpan.FromMilliseconds(200));
                var foregroundRoot = RootWindow(GetForegroundWindow());
                var visibleOwnedPopupCount = automation.GetDesktop()
                    .FindAllChildren(condition => condition.ByControlType(ControlType.Window))
                    .Count(element =>
                    {
                        var handle = element.Properties.NativeWindowHandle.ValueOrDefault;
                        return handle != IntPtr.Zero &&
                            handle != mainHandle &&
                            GetAncestor(handle, GetAncestorRootOwner) == mainHandle &&
                            IsWindowVisible(handle) &&
                            element.Properties.IsOffscreen.ValueOrDefault != true;
                    });
                facts = new FilePickerInvocationFacts(
                    mainHandle,
                    application.ProcessId,
                    mainWindow.Properties.ProcessId.ValueOrDefault,
                    NativeProcessId(mainHandle),
                    foregroundRoot,
                    GetLastActivePopup(mainHandle),
                    IsWindowVisible(mainHandle),
                    IsWindowEnabled(mainHandle),
                    mainWindow.Properties.IsEnabled.ValueOrDefault,
                    mainWindow.Properties.IsOffscreen.ValueOrDefault,
                    visibleOwnedPopupCount);
                if (IsSafeFilePickerInvocationPrecondition(facts))
                    return new FilePickerInvocationContext(
                        mainHandle, application.ProcessId, foregroundRoot);
            }
            while (DateTime.UtcNow < deadline);

            throw new InvalidOperationException(
                "The MAUI window did not reach a clean foreground state before opening " +
                $"the file picker (main={facts.MainWindowHandle != IntPtr.Zero}, " +
                $"uiaPid={facts.UiaProcessId == facts.ApplicationProcessId}, " +
                $"nativePid={facts.NativeProcessId == facts.ApplicationProcessId}, " +
                $"foreground={facts.ForegroundRootHandle == facts.MainWindowHandle}, " +
                $"lastPopup={facts.LastActivePopupHandle == facts.MainWindowHandle}, " +
                $"nativeVisible={facts.IsNativeVisible}, nativeEnabled={facts.IsNativeEnabled}, " +
                $"uiaEnabled={facts.IsUiaEnabled}, uiaOffscreen={facts.IsUiaOffscreen}, " +
                $"ownedPopups={facts.VisibleOwnedPopupCount}).");
        }

        private static void RestoreExactForegroundWindow(IntPtr mainHandle)
        {
            var callerThread = GetCurrentThreadId();
            var foregroundThread = GetWindowThreadProcessId(
                GetForegroundWindow(), out _);
            var targetThread = GetWindowThreadProcessId(mainHandle, out _);
            var callerAttachedToForeground = foregroundThread != 0 &&
                foregroundThread != callerThread &&
                AttachThreadInput(callerThread, foregroundThread, true);
            var callerAttachedToTarget = targetThread != 0 &&
                targetThread != callerThread &&
                AttachThreadInput(callerThread, targetThread, true);
            try
            {
                _ = ShowWindowAsync(mainHandle, ShowWindowRestore);
                _ = BringWindowToTop(mainHandle);
                _ = SetActiveWindow(mainHandle);
                _ = SetFocus(mainHandle);
                _ = SetForegroundWindow(mainHandle);
            }
            finally
            {
                if (callerAttachedToTarget)
                    _ = AttachThreadInput(callerThread, targetThread, false);
                if (callerAttachedToForeground)
                    _ = AttachThreadInput(callerThread, foregroundThread, false);
            }
        }

        internal void ChooseSingleFileFromOwnedForegroundPicker(
            string absolutePath,
            FilePickerInvocationContext invocation,
            TimeSpan timeout)
        {
            if (!Path.IsPathFullyQualified(absolutePath) || !File.Exists(absolutePath))
                throw new InvalidOperationException("Picker input must be one existing absolute file.");
            var result = Retry.WhileNull(
                () =>
                {
                    try
                    {
                        var snapshots = automation.GetDesktop()
                            .FindAllChildren(condition => condition.ByControlType(ControlType.Window))
                            .Select(CreateFilePickerSnapshot)
                            .ToArray();
                        var index = FindSingleOwnedForegroundFilePickerIndex(
                            invocation, snapshots.Select(snapshot => snapshot.Facts).ToArray());
                        return index < 0 ? null : snapshots[index];
                    }
                    catch (COMException) when (!application.HasExited)
                    {
                        return null;
                    }
                },
                timeout, TimeSpan.FromMilliseconds(200), throwOnTimeout: false);
            var picker = result.Result ?? throw new InvalidOperationException(
                "The exact app action did not expose one owned foreground canonical file picker.");
            picker.FilenameEditors[0].AsTextBox().Text = absolutePath;
            ActivateExact(picker.AffirmativeButtons[0]);
        }

        private FilePickerSnapshot CreateFilePickerSnapshot(AutomationElement element)
        {
            var handle = element.Properties.NativeWindowHandle.ValueOrDefault;
            var editors = element.FindAllDescendants(
                condition => condition.ByControlType(ControlType.Edit));
            var namedEditors = editors.Where(candidate =>
                    candidate.Properties.AutomationId.ValueOrDefault is "FileNameControlHost" or "1148")
                .ToArray();
            var filenameEditors = namedEditors.Length > 0
                ? namedEditors
                : editors.Length == 1 ? editors : [];
            var affirmativeButtons = element.FindAllDescendants(
                    condition => condition.ByControlType(ControlType.Button))
                .Where(button => button.Properties.AutomationId.ValueOrDefault == "1")
                .ToArray();
            var facts = new FilePickerWindowFacts(
                handle,
                element.Properties.ProcessId.ValueOrDefault,
                NativeProcessId(handle),
                GetWindow(handle, GetWindowOwner),
                GetAncestor(handle, GetAncestorRootOwner),
                RootWindow(GetForegroundWindow()),
                GetLastActivePopup(CurrentWindow().Properties.NativeWindowHandle.ValueOrDefault),
                IsWindowVisible(handle),
                IsWindowEnabled(handle),
                element.Properties.IsEnabled.ValueOrDefault,
                element.Properties.IsOffscreen.ValueOrDefault,
                filenameEditors.Length,
                affirmativeButtons.Length);
            return new FilePickerSnapshot(filenameEditors, affirmativeButtons, facts);
        }

        private static IntPtr RootWindow(IntPtr handle) =>
            handle == IntPtr.Zero ? IntPtr.Zero : GetAncestor(handle, GetAncestorRoot);

        private static int NativeProcessId(IntPtr handle)
        {
            if (handle == IntPtr.Zero ||
                GetWindowThreadProcessId(handle, out var processId) == 0 ||
                processId == 0 || processId > int.MaxValue)
            {
                return 0;
            }
            return checked((int)processId);
        }

        private sealed record FilePickerSnapshot(
            AutomationElement[] FilenameEditors,
            AutomationElement[] AffirmativeButtons,
            FilePickerWindowFacts Facts);

        internal void HoldExact(AutomationElement element, TimeSpan duration)
        {
            if (duration < TimeSpan.FromMilliseconds(700) || duration > TimeSpan.FromSeconds(10))
                throw new ArgumentOutOfRangeException(nameof(duration));
            var point = element.GetClickablePoint();
            Mouse.MoveTo(point);
            Mouse.Down(MouseButton.Left);
            try { Thread.Sleep(duration); }
            finally { Mouse.Up(MouseButton.Left); }
        }

        internal AutomationElement? WaitForAutomationIdWithDescendantNameContaining(
            string automationId,
            string descendantNameMarker,
            TimeSpan timeout)
        {
            var result = Retry.WhileNull(
                () => FindAutomationIdWithDescendantNameContainingForRetry(
                    automationId,
                    descendantNameMarker),
                timeout,
                TimeSpan.FromMilliseconds(200),
                throwOnTimeout: false);
            return result.Result;
        }

        private AutomationElement? FindAutomationIdWithNameForRetry(
            string automationId,
            string name)
        {
            try
            {
                return CurrentWindow()
                    .FindAllDescendants(condition => condition.ByAutomationId(automationId))
                    .SingleOrDefault(candidate => string.Equals(
                        candidate.Properties.Name.ValueOrDefault,
                        name,
                        StringComparison.Ordinal));
            }
            catch (COMException) when (!application.HasExited)
            {
                return null;
            }
        }

        private AutomationElement? FindAutomationIdWithDescendantNameContainingForRetry(
            string automationId,
            string descendantNameMarker)
        {
            try
            {
                return CurrentWindow()
                    .FindAllDescendants(condition => condition.ByAutomationId(automationId))
                    .SingleOrDefault(candidate => candidate
                        .FindAllDescendants()
                        .Any(descendant =>
                            descendant.Properties.Name.ValueOrDefault?.Contains(
                                descendantNameMarker,
                                StringComparison.Ordinal) == true));
            }
            catch (COMException) when (!application.HasExited)
            {
                return null;
            }
        }

        internal void ActivateExact(AutomationElement element)
        {
            if (element.Patterns.Invoke.IsSupported)
            {
                element.Patterns.Invoke.Pattern.Invoke();
                return;
            }

            // A click is permitted only after the exact AutomationId found this element.
            FlaUI.Core.Input.Mouse.Click(element.GetClickablePoint());
        }

        internal void RequestContextMenuOnAncestor(
            AutomationElement exactDescendant,
            string ancestorAutomationId)
        {
            ArgumentNullException.ThrowIfNull(exactDescendant);
            ArgumentException.ThrowIfNullOrWhiteSpace(ancestorAutomationId);

            AutomationElement? current = exactDescendant;
            for (var depth = 0; current is not null && depth < 16; depth++)
            {
                if (string.Equals(
                    current.Properties.AutomationId.ValueOrDefault,
                    ancestorAutomationId,
                    StringComparison.Ordinal))
                {
                    current.RightClick();
                    return;
                }

                current = current.Parent;
            }

            throw new InvalidOperationException(
                $"Exact descendant is not inside the required {ancestorAutomationId} ancestor.");
        }

        public void FocusWindow() => CurrentWindow().Focus();

        internal void AssertStartupFailClosed(string expectedRuntimeFailureCode, TimeSpan timeout)
        {
            var result = Retry.WhileNull(
                () =>
                {
                    var error = FindAutomationId("Startup.Error");
                    if (error is null ||
                        string.IsNullOrWhiteSpace(error.Properties.Name.ValueOrDefault))
                    {
                        return null;
                    }
                    return error;
                },
                timeout,
                TimeSpan.FromMilliseconds(200),
                throwOnTimeout: false);
            if (result.Result is null)
            {
                throw new InvalidOperationException(
                    "Invalid mailbox runtime did not expose a concrete Startup.Error.");
            }

            var code = WaitForAutomationIdWithName(
                "Startup.RuntimeFailureCode", expectedRuntimeFailureCode, timeout);
            if (code is null)
            {
                throw new InvalidOperationException(
                    "Invalid mailbox runtime did not expose its expected sanitized failure code.");
            }

            foreach (var forbidden in new[]
            {
                "Welcome.Create",
                "Welcome.Restore",
                "Conversations.Root",
                "Page.DesktopWorkspace"
            })
            {
                if (FindAutomationId(forbidden) is not null)
                {
                    throw new InvalidOperationException(
                        "Invalid mailbox runtime reached an interactive or authenticated UI.");
                }
            }
        }

        public void WriteSuccessEvidence()
        {
            File.WriteAllText(
                Path.Combine(artifactDirectory, "windows-ui-tree.txt"),
                BuildSanitizedTree(CurrentWindow()),
                Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(artifactDirectory, "windows-ui-result.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        schema = "deep.survival.windows-ui.v1",
                        status = "passed",
                        processStarted = true,
                        nonzeroWindow = CurrentWindow().Properties.NativeWindowHandle.ValueOrDefault != IntPtr.Zero,
                        baseline = Baseline.Welcome,
                        createInvoked = true,
                        authenticatedControl = AuthenticatedControlId,
                        authenticatedRootObserved = true
                    },
                    new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);
        }

        private void WriteFailureEvidence(string reason)
        {
            try
            {
                if (string.Equals(Environment.GetEnvironmentVariable(CaptureFailureKey), "1", StringComparison.Ordinal) &&
                    string.Equals(Environment.GetEnvironmentVariable(BootstrapKey), "stub", StringComparison.OrdinalIgnoreCase))
                {
                    var quarantine = Path.Combine(artifactDirectory, "quarantine", "raw");
                    Directory.CreateDirectory(quarantine);
                    CaptureWindow(CurrentWindow(), Path.Combine(quarantine, "stub-welcome-failure.png"));
                }
                File.WriteAllText(
                    Path.Combine(artifactDirectory, "windows-ui-tree.txt"),
                    BuildSanitizedTree(CurrentWindow()),
                    Encoding.UTF8);
                File.WriteAllText(
                    Path.Combine(artifactDirectory, "windows-ui-result.json"),
                    JsonSerializer.Serialize(
                        new
                        {
                            schema = "deep.survival.windows-ui.v1",
                            status = "failed",
                            reason,
                            processStarted = true,
                            nonzeroWindow = CurrentWindow().Properties.NativeWindowHandle.ValueOrDefault != IntPtr.Zero
                        },
                        new JsonSerializerOptions { WriteIndented = true }),
                    Encoding.UTF8);
            }
            catch
            {
                // Evidence collection must not replace the original test failure.
            }
        }

        private Window CurrentWindow() => window;

        private static Window? GetPidBoundMainWindow(
            Application application,
            UIA3Automation automation)
        {
            try
            {
                var candidate = application.GetMainWindow(
                    automation,
                    TimeSpan.FromMilliseconds(200));
                return candidate is not null
                    && candidate.Properties.ProcessId.ValueOrDefault == application.ProcessId
                    && candidate.Properties.NativeWindowHandle.ValueOrDefault != IntPtr.Zero
                        ? candidate
                        : null;
            }
            catch (COMException)
            {
                // A transient UIA provider timeout must not make this exact-PID
                // lane enumerate every unrelated top-level desktop window.
                return null;
            }
        }

        private static string BuildSanitizedTree(AutomationElement root)
        {
            var builder = new StringBuilder();
            Append(root, builder);
            return builder.ToString();

            static void Append(AutomationElement element, StringBuilder output)
            {
                var automationId = ReadSafely(() => element.Properties.AutomationId.ValueOrDefault);
                if (AllowedEvidenceIds.Contains(automationId))
                {
                    var controlType = ReadSafely(() => element.Properties.ControlType.ValueOrDefault.ToString());
                    output.Append(Sanitize(controlType))
                        .Append(" id=")
                        .AppendLine(Sanitize(automationId));
                }

                foreach (var child in element.FindAllChildren())
                {
                    Append(child, output);
                }
            }

            static string ReadSafely(Func<string?> read)
            {
                try
                {
                    return read() ?? "-";
                }
                catch
                {
                    return "unavailable";
                }
            }

            static string Sanitize(string? value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return "-";
                }

                var safe = new string(value.Where(character =>
                    char.IsLetterOrDigit(character) || character is '.' or '_' or '-').ToArray());
                return safe.Length > 96 ? safe[..96] : safe;
            }
        }


        private static void CaptureWindow(Window target, string path)
        {
            var handle = target.Properties.NativeWindowHandle.ValueOrDefault;
            var bounds = target.BoundingRectangle;
            var width = Math.Max(1, (int)Math.Ceiling((double)bounds.Width));
            var height = Math.Max(1, (int)Math.Ceiling((double)bounds.Height));
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            var deviceContext = graphics.GetHdc();
            try
            {
                if (!PrintWindow(handle, deviceContext, PrintWindowRenderFullContent))
                {
                    throw new InvalidOperationException("Win32 PrintWindow did not capture the MAUI window.");
                }
            }
            finally
            {
                graphics.ReleaseHdc(deviceContext);
            }

            bitmap.Save(path, ImageFormat.Png);
        }

        private const uint PrintWindowRenderFullContent = 2;
        private const uint GetWindowOwner = 4;
        private const uint GetAncestorRoot = 2;
        private const uint GetAncestorRootOwner = 3;
        private const int ShowWindowRestore = 9;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PrintWindow(IntPtr windowHandle, IntPtr deviceContext, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr windowHandle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindowAsync(IntPtr windowHandle, int command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(IntPtr windowHandle);

        [DllImport("user32.dll")]
        private static extern IntPtr SetActiveWindow(IntPtr windowHandle);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr windowHandle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(
            uint attachThreadId,
            uint attachToThreadId,
            [MarshalAs(UnmanagedType.Bool)] bool attach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr windowHandle, uint command);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr windowHandle, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetLastActivePopup(IntPtr windowHandle);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(
            IntPtr windowHandle,
            out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr windowHandle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowEnabled(IntPtr windowHandle);


        private static void WriteLaunchFailure(string artifactDirectory, Application app, Exception exception)
        {
            Directory.CreateDirectory(artifactDirectory);
            File.WriteAllText(
                Path.Combine(artifactDirectory, "windows-ui-result.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        schema = "deep.survival.windows-ui.v1",
                        status = "failed",
                        reason = exception.GetType().Name,
                        processStarted = app.ProcessId > 0,
                        nonzeroWindow = false
                    },
                    new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);
        }

        private static string RequireExistingFile(string key)
        {
            var value = RequireSetting(key);
            if (!File.Exists(value))
            {
                throw new FileNotFoundException($"{key} does not point to an existing file.");
            }

            return Path.GetFullPath(value);
        }

        private static void AssertLaunchedBinaryBinding(Application application, string expectedPath)
        {
            using var process = Process.GetProcessById(application.ProcessId);
            var launchedPath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(launchedPath) || !string.Equals(Path.GetFullPath(launchedPath), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Launched Windows process path does not match DEEP_MAUI_EXE.");
            }

            if (!string.Equals(Environment.GetEnvironmentVariable("DEEP_STRICT_CROSS_PLATFORM_UI"), "1", StringComparison.Ordinal))
            {
                return;
            }
            var policy = ApprovedCrossPlatformPolicy.Current ?? throw new InvalidOperationException("Approved cross-platform policy was not loaded before Windows launch.");
            var expectedHash = policy.WindowsExeSha256;
            var actualHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(launchedPath)));
            if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal) || !System.Text.RegularExpressions.Regex.IsMatch(policy.SourceCommit, "^[a-f0-9]{40}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            {
                throw new InvalidOperationException("Launched Windows binary hash or source-commit binding is invalid.");
            }
        }

        private static string RequireDirectorySetting(string key)
        {
            var value = RequireSetting(key);
            if (!Path.IsPathFullyQualified(value))
            {
                throw new InvalidOperationException($"{key} must be an absolute path.");
            }

            return Path.GetFullPath(value);
        }

        private static string RequireSetting(string key) =>
            Environment.GetEnvironmentVariable(key) is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException($"{key} is required in the strict Windows UI lane.");

        private static void CloseApplication(Application app)
        {
            var processId = app.ProcessId;
            try
            {
                app.Close(killIfCloseFails: true);
            }
            catch
            {
                try
                {
                    app.Kill();
                }
                catch
                {
                }
            }

            try
            {
                using var process = Process.GetProcessById(processId);
                if (!process.WaitForExit(TimeSpan.FromSeconds(15)))
                {
                    try
                    {
                        app.Kill();
                    }
                    catch
                    {
                    }

                    if (!process.WaitForExit(TimeSpan.FromSeconds(15)))
                    {
                        throw new InvalidOperationException(
                            "The exact launched Windows process did not exit before session disposal.");
                    }
                }
            }
            catch (ArgumentException)
            {
                // The exact PID has already exited and was removed from the process table.
            }
            finally
            {
                app.Dispose();
            }
        }

        public void Dispose()
        {
            automation.Dispose();
            CloseApplication(application);
        }
    }
}
