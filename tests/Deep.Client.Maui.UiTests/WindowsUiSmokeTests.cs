using OpenQA.Selenium;
using OpenQA.Selenium.Appium;
using OpenQA.Selenium.Appium.Windows;
using System.Diagnostics;
using System.Text.Json;

namespace Deep.Client.Maui.UiTests;

public sealed class WindowsUiSmokeTests
{
    private static readonly UiBaseline Baseline = LoadBaseline();

    [Fact]
    public void OnboardingPageRendersInteractiveControls()
    {
        using var session = UiTestSession.TryCreate();
        if (session is null)
        {
            return;
        }

        Assert.NotNull(session.WaitForAccessibilityId("Onboarding.DisplayName", TimeSpan.FromSeconds(15)));
        Assert.NotNull(session.WaitForAccessibilityId("Onboarding.RecoveryPhrase", TimeSpan.FromSeconds(5)));
        Assert.NotNull(session.WaitForAccessibilityId("Onboarding.Create", TimeSpan.FromSeconds(5)));
        Assert.NotNull(session.WaitForAccessibilityId("Onboarding.Restore", TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void RegisterFlowNavigatesToConversations()
    {
        using var session = UiTestSession.TryCreate();
        if (session is null)
        {
            return;
        }

        var displayName = session.WaitForAccessibilityId("Onboarding.DisplayName", TimeSpan.FromSeconds(15));
        Assert.NotNull(displayName);
        displayName.Clear();
        displayName.SendKeys("UiAutomation");

        var createButton = session.WaitForAccessibilityId("Onboarding.Create", TimeSpan.FromSeconds(5));
        Assert.NotNull(createButton);
        createButton.Click();

        var conversationsRoot = session.WaitForAccessibilityId("Conversations.Root", TimeSpan.FromSeconds(20));
        Assert.NotNull(conversationsRoot);

        var newConversation = session.WaitForAccessibilityId("Conversations.NewConversation", TimeSpan.FromSeconds(5));
        Assert.NotNull(newConversation);
    }

    [Fact]
    public void NewChatOpenAndSendShowsMessageBubble()
    {
        using var session = UiTestSession.TryCreate();
        if (session is null)
        {
            return;
        }

        var displayName = session.WaitForAccessibilityId("Onboarding.DisplayName", TimeSpan.FromSeconds(15));
        Assert.NotNull(displayName);
        displayName.Clear();
        displayName.SendKeys("UiSendFlow");

        var createButton = session.WaitForAccessibilityId("Onboarding.Create", TimeSpan.FromSeconds(5));
        Assert.NotNull(createButton);
        createButton.Click();

        var sessionId = session.WaitForAccessibilityId("Onboarding.SessionId", TimeSpan.FromSeconds(8))?.Text;
        Assert.False(string.IsNullOrWhiteSpace(sessionId));

        var newConversation = session.WaitForAccessibilityId("Conversations.NewConversation", TimeSpan.FromSeconds(15));
        Assert.NotNull(newConversation);
        newConversation.Click();

        var newSessionId = session.WaitForAccessibilityId("NewConversation.SessionId", TimeSpan.FromSeconds(8));
        Assert.NotNull(newSessionId);
        newSessionId.Clear();
        newSessionId.SendKeys(sessionId);

        var start = session.WaitForAccessibilityId("NewConversation.Start", TimeSpan.FromSeconds(5));
        Assert.NotNull(start);
        start.Click();

        var draft = session.WaitForAccessibilityId("Chat.Draft", TimeSpan.FromSeconds(10));
        Assert.NotNull(draft);

        var sentText = "ui smoke message";
        draft.Clear();
        draft.SendKeys(sentText);

        var sendButton = session.WaitForAccessibilityId("Chat.Send", TimeSpan.FromSeconds(5));
        Assert.NotNull(sendButton);
        sendButton.Click();

        var sentMessage = session.WaitForText(sentText, TimeSpan.FromSeconds(8));
        Assert.NotNull(sentMessage);
    }

    [Fact]
    public void BaselineUiElementsAreVisibleAcrossCoreScreens()
    {
        using var session = UiTestSession.TryCreate();
        if (session is null)
        {
            return;
        }

        foreach (var id in Baseline.Onboarding)
        {
            Assert.NotNull(session.WaitForAccessibilityId(id, TimeSpan.FromSeconds(15)));
        }

        RegisterAndNavigateToConversations(session, "UiBaseline");

        foreach (var id in Baseline.Conversations)
        {
            Assert.NotNull(session.WaitForAccessibilityId(id, TimeSpan.FromSeconds(8)));
        }

        var sessionId = session.WaitForAccessibilityId("Onboarding.SessionId", TimeSpan.FromSeconds(8))?.Text;
        Assert.False(string.IsNullOrWhiteSpace(sessionId));

        OpenOneToOneChat(session, sessionId!);

        foreach (var id in Baseline.Chat)
        {
            Assert.NotNull(session.WaitForAccessibilityId(id, TimeSpan.FromSeconds(8)));
        }
    }

    [Fact]
    public void GroupChatCanSendMessage()
    {
        using var session = UiTestSession.TryCreate();
        if (session is null)
        {
            return;
        }

        RegisterAndNavigateToConversations(session, "UiGroupSend");

        var newConversation = session.WaitForAccessibilityId("Conversations.NewConversation", TimeSpan.FromSeconds(8));
        Assert.NotNull(newConversation);
        newConversation.Click();

        var createGroupEntry = session.WaitForAccessibilityId("StartConversation.CreateGroup", TimeSpan.FromSeconds(8));
        Assert.NotNull(createGroupEntry);
        createGroupEntry.Click();

        var groupName = session.WaitForAccessibilityId("Groups.GroupName", TimeSpan.FromSeconds(8));
        Assert.NotNull(groupName);
        groupName.Click();
        groupName.SendKeys("BaselineGroup");

        var createGroup = session.WaitForAccessibilityId("Groups.Create", TimeSpan.FromSeconds(8));
        Assert.NotNull(createGroup);
        createGroup.Click();

        var createdGroup = session.WaitForText("BaselineGroup", TimeSpan.FromSeconds(8));
        Assert.NotNull(createdGroup);

        foreach (var id in Baseline.GroupChat)
        {
            Assert.NotNull(session.WaitForAccessibilityId(id, TimeSpan.FromSeconds(10)));
        }

        var draft = session.WaitForAccessibilityId("GroupChat.Draft", TimeSpan.FromSeconds(8));
        Assert.NotNull(draft);
        var text = "group ui smoke";
        draft.Clear();
        draft.SendKeys(text);

        var send = session.WaitForAccessibilityId("GroupChat.Send", TimeSpan.FromSeconds(8));
        Assert.NotNull(send);
        send.Click();

        Assert.NotNull(session.WaitForText(text, TimeSpan.FromSeconds(8)));
    }

    private static void RegisterAndNavigateToConversations(UiTestSession session, string name)
    {
        var displayName = session.WaitForAccessibilityId("Onboarding.DisplayName", TimeSpan.FromSeconds(15));
        Assert.NotNull(displayName);
        displayName.Clear();
        displayName.SendKeys(name);

        var createButton = session.WaitForAccessibilityId("Onboarding.Create", TimeSpan.FromSeconds(5));
        Assert.NotNull(createButton);
        createButton.Click();

        Assert.NotNull(session.WaitForAccessibilityId("Conversations.Root", TimeSpan.FromSeconds(20)));
    }

    private static void OpenOneToOneChat(UiTestSession session, string sessionId)
    {
        var newConversation = session.WaitForAccessibilityId("Conversations.NewConversation", TimeSpan.FromSeconds(8));
        Assert.NotNull(newConversation);
        newConversation.Click();

        var newSessionId = session.WaitForAccessibilityId("NewConversation.SessionId", TimeSpan.FromSeconds(8));
        Assert.NotNull(newSessionId);
        newSessionId.Clear();
        newSessionId.SendKeys(sessionId);

        var start = session.WaitForAccessibilityId("NewConversation.Start", TimeSpan.FromSeconds(5));
        Assert.NotNull(start);
        start.Click();
    }

    private static UiBaseline LoadBaseline()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Baselines", "session-ui-baseline.json");
        if (!File.Exists(path))
        {
            path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Baselines", "session-ui-baseline.json"));
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<UiBaseline>(json)
            ?? throw new InvalidOperationException("UI baseline file could not be parsed.");
    }

    private sealed record UiBaseline(string[] Onboarding, string[] Conversations, string[] Chat, string[] GroupChat);

    private sealed class UiTestSession : IDisposable
    {
        private const string UiTestsEnabledKey = "DEEP_UI_TESTS";
        private const string AppPathKey = "DEEP_MAUI_EXE";
        private const string WinAppDriverUrlKey = "WINAPPDRIVER_URL";

        private readonly Process appProcess;

        public WindowsDriver Driver { get; }

        private UiTestSession(Process appProcess, WindowsDriver driver)
        {
            this.appProcess = appProcess;
            Driver = driver;
        }

        public static UiTestSession? TryCreate()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(UiTestsEnabledKey), "1", StringComparison.Ordinal))
            {
                return null;
            }

            var appPath = Environment.GetEnvironmentVariable(AppPathKey);
            if (string.IsNullOrWhiteSpace(appPath))
            {
                throw new InvalidOperationException($"Set {AppPathKey} to published Deep.Client.Maui.exe before running UI tests.");
            }

            if (!File.Exists(appPath))
            {
                throw new FileNotFoundException($"Cannot find MAUI executable at '{appPath}'.", appPath);
            }

            var appProcess = Process.Start(new ProcessStartInfo
            {
                FileName = appPath,
                UseShellExecute = true
            }) ?? throw new InvalidOperationException("Failed to start MAUI app process.");

            var serverUrl = Environment.GetEnvironmentVariable(WinAppDriverUrlKey);
            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                serverUrl = "http://127.0.0.1:4723";
            }

            var options = new AppiumOptions();
            options.PlatformName = "Windows";
            options.AddAdditionalAppiumOption("appTopLevelWindow", appProcess.MainWindowHandle.ToString("x"));

            var driver = new WindowsDriver(new Uri(serverUrl), options);
            driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromMilliseconds(200);

            return new UiTestSession(appProcess, driver);
        }

        public IWebElement? WaitForAccessibilityId(string id, TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var element = Driver.FindElements(MobileBy.AccessibilityId(id)).FirstOrDefault();
                if (element is not null)
                {
                    return element;
                }

                Thread.Sleep(200);
            }

            return null;
        }

        public IWebElement? WaitForText(string text, TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var element = Driver.FindElements(By.Name(text)).FirstOrDefault();
                if (element is not null)
                {
                    return element;
                }

                Thread.Sleep(200);
            }

            return null;
        }

        public void Dispose()
        {
            try
            {
                Driver?.Quit();
                Driver?.Dispose();
            }
            catch
            {
            }

            try
            {
                if (!appProcess.HasExited)
                {
                    appProcess.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            appProcess.Dispose();
        }
    }
}
