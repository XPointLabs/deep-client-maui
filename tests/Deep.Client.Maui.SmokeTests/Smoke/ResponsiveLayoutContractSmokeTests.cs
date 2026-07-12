namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class ResponsiveLayoutContractSmokeTests
{
    [Fact]
    public void AccountEntryFormsFillNarrowWindowsAndRemainBoundedOnWideWindows()
    {
        var welcome = ReadWorkspaceFile("src", "Deep.Client.Maui", "Pages", "WelcomePage.xaml");
        var onboarding = ReadWorkspaceFile("src", "Deep.Client.Maui", "Pages", "OnboardingPage.xaml");

        Assert.Contains("MaximumWidthRequest=\"420\"", welcome, StringComparison.Ordinal);
        Assert.Contains("MaximumWidthRequest=\"560\"", welcome, StringComparison.Ordinal);
        Assert.Contains("MaximumWidthRequest=\"520\"", onboarding, StringComparison.Ordinal);
        Assert.DoesNotContain("WidthRequest=\"{OnIdiom Desktop=", welcome, StringComparison.Ordinal);
        Assert.DoesNotContain("WidthRequest=\"{OnIdiom Desktop=", onboarding, StringComparison.Ordinal);
        Assert.Contains("HorizontalOptions=\"Fill\"", welcome, StringComparison.Ordinal);
        Assert.Contains("HorizontalOptions=\"Fill\"", onboarding, StringComparison.Ordinal);
    }

    private static string ReadWorkspaceFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deep.Client.Maui.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine([directory!.FullName, .. parts]));
    }
}
