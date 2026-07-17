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

    private sealed class WindowsUiTestSession : IDisposable
    {
        private const string AppPathKey = "DEEP_MAUI_EXE";
        private const string ArtifactDirectoryKey = "DEEP_E2E_ARTIFACTS";
        private const string AppDataDirectoryKey = "DEEP_E2E_APPDATA_ROOT";
        private const string BootstrapKey = "DEEP_E2E_BOOTSTRAP";

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

        public static WindowsUiTestSession CreateStrict()
        {
            if (!System.OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("The strict Windows UI lane requires Windows.");
            }

            var appPath = RequireExistingFile(AppPathKey);
            var artifactDirectory = RequireDirectorySetting(ArtifactDirectoryKey);
            var appDataDirectory = RequireDirectorySetting(AppDataDirectoryKey);
            Directory.CreateDirectory(artifactDirectory);
            Directory.CreateDirectory(appDataDirectory);

            var startInfo = new ProcessStartInfo
            {
                FileName = appPath,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(appPath)
                    ?? throw new InvalidOperationException("The MAUI executable has no parent directory.")
            };
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
                var result = Retry.WhileNull(
                    () => application
                        .GetAllTopLevelWindows(automation)
                        .FirstOrDefault(candidate =>
                            candidate.Properties.ProcessId.ValueOrDefault == application.ProcessId
                            && candidate.Properties.NativeWindowHandle.ValueOrDefault != IntPtr.Zero),
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
                () => window.FindFirstDescendant(condition => condition.ByAutomationId(automationId)),
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

        public void WriteSuccessEvidence()
        {
            File.WriteAllText(
                Path.Combine(artifactDirectory, "windows-ui-tree.txt"),
                BuildSanitizedTree(window),
                Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(artifactDirectory, "windows-ui-result.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        schema = "deep.survival.windows-ui.v1",
                        status = "passed",
                        processStarted = true,
                        nonzeroWindow = window.Properties.NativeWindowHandle.ValueOrDefault != IntPtr.Zero,
                        baseline = Baseline.Welcome
                    },
                    new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);
        }

        private void WriteFailureEvidence(string reason)
        {
            try
            {
                CaptureWindow(window, Path.Combine(artifactDirectory, "windows-ui-failure.png"));
                File.WriteAllText(
                    Path.Combine(artifactDirectory, "windows-ui-tree.txt"),
                    BuildSanitizedTree(window),
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
                            nonzeroWindow = window.Properties.NativeWindowHandle.ValueOrDefault != IntPtr.Zero
                        },
                        new JsonSerializerOptions { WriteIndented = true }),
                    Encoding.UTF8);
            }
            catch
            {
                // Evidence collection must not replace the original test failure.
            }
        }

        private static string BuildSanitizedTree(AutomationElement root)
        {
            var builder = new StringBuilder();
            Append(root, builder, 0);
            return builder.ToString();

            static void Append(AutomationElement element, StringBuilder output, int depth)
            {
                var automationId = ReadSafely(() => element.Properties.AutomationId.ValueOrDefault);
                var controlType = ReadSafely(() => element.Properties.ControlType.ValueOrDefault.ToString());
                output.Append(' ', depth * 2)
                    .Append(Sanitize(controlType))
                    .Append(" id=")
                    .AppendLine(Sanitize(automationId));

                if (depth >= 12)
                {
                    return;
                }

                foreach (var child in element.FindAllChildren())
                {
                    Append(child, output, depth + 1);
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
