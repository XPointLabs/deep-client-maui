namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class TransportSettingsUiContractSmokeTests
{
    [Fact]
    public void DirectChatSecurityStatusIsTransportNeutral()
    {
        var chat = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "Pages", "ChatPage.xaml");

        Assert.Contains("AutomationId=\"Chat.SecurityStatus\"", chat, StringComparison.Ordinal);
        Assert.Contains("Text=\"защищено сквозным шифрованием\"", chat, StringComparison.Ordinal);
        Assert.DoesNotContain("защищено сетью XPoint", chat, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsExposeOneTransportNeutralEntryWithoutNetworkDonation()
    {
        var settings = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "Pages", "SettingsPage.xaml");
        var settingsCode = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "Pages", "SettingsPage.xaml.cs");

        Assert.Contains("AutomationId=\"Settings.Path\"", settings, StringComparison.Ordinal);
        Assert.Contains("Text=\"Транспорты\"", settings, StringComparison.Ordinal);
        Assert.Contains("Доступные маршруты и приоритеты", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Поддержать сеть", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.Donate", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.SessionNetwork", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("OnDonateClicked", settingsCode, StringComparison.Ordinal);
        Assert.DoesNotContain("OnSessionNetworkClicked", settingsCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TransportPageTreatsXPointAsOneProviderAndDirectP2pStaysHidden()
    {
        var detail = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "Pages", "SettingsDetailPage.xaml.cs");

        Assert.Contains("TitleLabel.Text = \"Транспорты\"", detail, StringComparison.Ordinal);
        Assert.Contains("Deep может использовать несколько транспортов", detail, StringComparison.Ordinal);
        Assert.Contains("\"XPoint Network\"", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Direct P2P\"", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Wi-Fi и Bluetooth", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("case \"donate\"", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("case \"network\"", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildDonateSection", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildNetworkSection", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("DirectP2pEnabled", detail, StringComparison.Ordinal);
    }

    private static string ReadWorkspaceFile(params string[] relative)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(new[] { current.FullName }.Concat(relative).ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, relative));
    }
}
