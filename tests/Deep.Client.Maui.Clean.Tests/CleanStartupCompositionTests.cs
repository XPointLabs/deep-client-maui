namespace Deep.Client.Maui.Clean.Tests;

public sealed class CleanStartupCompositionTests
{
    [Fact]
    public void ContactAdvertisementAuthorUsesExplicitDependencyFactory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string? source = null;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Deep.Client.Maui",
                "MauiProgram.Clean.cs");
            if (File.Exists(candidate))
            {
                source = File.ReadAllText(candidate);
                break;
            }
            directory = directory.Parent;
        }

        Assert.NotNull(source);
        Assert.DoesNotContain(
            "AddSingleton<AccountOwnedContactRouteAdvertisementAuthor>();",
            source, StringComparison.Ordinal);
        Assert.Contains(
            "new AccountOwnedContactRouteAdvertisementAuthor(",
            source, StringComparison.Ordinal);
        Assert.Contains(
            "GetRequiredService<DeepContactResolveRuntimeAccessor>()",
            source, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericStartupFailureDoesNotTellUserToDeleteAccountData()
    {
        var app = ReadSource("App.Clean.cs");
        Assert.Contains("catch (LocalStateResetRequiredException exception)", app,
            StringComparison.Ordinal);
        Assert.Contains("catch (ProtectedIdentityResetRequiredException exception)", app,
            StringComparison.Ordinal);
        Assert.Contains("PresentStartupFailureAsync(exception, resetRequired: false)", app,
            StringComparison.Ordinal);
        Assert.Contains("Не удаляйте данные приложения", app, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsUatPackageCanAdvanceRevisionWithoutChangingProductionVersion()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "eng",
                "Invoke-PhysicalUatWindowsMsix.ps1");
            if (!File.Exists(candidate)) continue;
            var script = File.ReadAllText(candidate);
            Assert.Contains("[int]$PackageRevision", script, StringComparison.Ordinal);
            Assert.Contains("$PSBoundParameters.ContainsKey('PackageRevision')", script,
                StringComparison.Ordinal);
            Assert.Contains("[int]$versionCode", script, StringComparison.Ordinal);
            return;
        }
        throw new FileNotFoundException("Windows UAT build script is unavailable.");
    }

    private static string ReadSource(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Deep.Client.Maui", name);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException($"MAUI source {name} is unavailable.");
    }
}
