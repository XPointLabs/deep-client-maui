namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class OfflineUpdateUiContractSmokeTests
{
    [Fact]
    public void SettingsUpdateEntry_IsReachableFailClosedAndDoesNotAddInstallerPrivilege()
    {
        var settings = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "Pages", "SettingsPage.xaml");
        var settingsCode = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "Pages", "SettingsPage.xaml.cs");
        var detailCode = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "Pages", "SettingsDetailPage.xaml.cs");
        var manifest = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "Platforms", "Android", "AndroidManifest.xml");
        var composition = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "MauiProgram.cs");

        Assert.Contains("AutomationId=\"Settings.OfflineUpdate\"", settings, StringComparison.Ordinal);
        Assert.Contains("OnOfflineUpdateClicked", settings, StringComparison.Ordinal);
        Assert.Contains(
            "NavigateToSettingsSectionAsync(\"offline-update\")",
            settingsCode,
            StringComparison.Ordinal);
        Assert.Contains("case \"offline-update\"", detailCode, StringComparison.Ordinal);
        Assert.Contains("Функция выключена", detailCode, StringComparison.Ordinal);
        Assert.Contains("Кнопки обхода проверки нет", detailCode, StringComparison.Ordinal);
        Assert.Contains("Apple", detailCode, StringComparison.Ordinal);
        Assert.DoesNotContain("REQUEST_INSTALL_PACKAGES", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("PackageInstaller", composition, StringComparison.Ordinal);
        Assert.DoesNotContain("OfflineAndroidUpdateVerifier", composition, StringComparison.Ordinal);
        Assert.DoesNotContain("PortableUpdateMetadataVerifier", composition, StringComparison.Ordinal);
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
