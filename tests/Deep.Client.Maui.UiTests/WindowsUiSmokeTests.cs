using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using Xunit;

namespace Deep.Client.Maui.UiTests;

public sealed class WindowsUiSmokeTests
{
    private const string AuthenticatedControlId = "Conversations.NewConversation";
    private static readonly UiBaseline Baseline = LoadBaseline();

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

            var application = Application.Launch(startInfo);
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

        internal AutomationElement? WaitForAutomationIdWithName(string automationId, string name, TimeSpan timeout)
        {
            var result = Retry.WhileNull(
                () => FindAutomationIdWithNameForRetry(automationId, name),
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

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PrintWindow(IntPtr windowHandle, IntPtr deviceContext, uint flags);

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
            try
            {
                app.Close();
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

            app.Dispose();
        }

        public void Dispose()
        {
            automation.Dispose();
            CloseApplication(application);
        }
    }
}
