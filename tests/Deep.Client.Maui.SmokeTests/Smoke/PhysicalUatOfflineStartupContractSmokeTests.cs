namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class PhysicalUatOfflineStartupContractSmokeTests
{
    [Fact]
    public void ColdUiCompositionDefersEveryNetworkInput()
    {
        var program = ReadMauiProgram();
        var create = ExtractMethodBody(program, "public static MauiApp CreateMauiApp()");
        var app = ReadWorkspaceFile("src", "Deep.Client.Maui", "App.xaml.cs");
        var shell = ReadWorkspaceFile("src", "Deep.Client.Maui", "AppShell.xaml.cs");
        var appConstructor = ExtractMethodBody(app, "public App(");
        var shellConstructor = ExtractMethodBody(shell, "public AppShell(");
        var auth = ReadWorkspaceFile(
            "src", "Deep.Client.Maui.Core", "Navigation", "AuthNavigationState.cs");

        Assert.DoesNotContain("ResolveApplicationServiceInputs()", create, StringComparison.Ordinal);
        Assert.Contains(
            "ConfigureApplicationServices(builder.Services, ResolveApplicationServiceInputs)",
            create,
            StringComparison.Ordinal);
        Assert.Contains("DeferredApplicationServiceInputs", program, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientRuntimeBootstrapper", appConstructor, StringComparison.Ordinal);
        Assert.DoesNotContain("IRealityTransportRuntime", appConstructor, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientRuntime", shellConstructor, StringComparison.Ordinal);
        Assert.Contains("IDeepAccountRuntimeAccessor", auth, StringComparison.Ordinal);
        Assert.DoesNotContain("SessionAccountService", auth, StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicalUatRuntimeFactoryDoesNotSynchronizeInboxBeforePublishingRuntime()
    {
        var program = ReadMauiProgram();
        var runtimeFactory = ExtractMethodBody(
            program,
            "private static async Task<ClientRuntime> CreateClientRuntimeAsync(");

        Assert.DoesNotContain(
            ".Inbox.SynchronizeAsync(",
            runtimeFactory,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeBootstrapperDoesNotStartNetworkTransportOnItsCriticalPath()
    {
        var program = ReadMauiProgram();
        var composition = ExtractMethodBody(
            program,
            "private static ApplicationServiceInputs ResolveApplicationServiceInputs(");
        var bootstrapperStart = composition.IndexOf(
            "runtimeBootstrapperFactory =",
            StringComparison.Ordinal);
        var runtimeFactoryStart = composition.IndexOf(
            "runtimeFactory =",
            bootstrapperStart,
            StringComparison.Ordinal);

        Assert.True(bootstrapperStart >= 0, "Runtime bootstrapper registration was not found.");
        Assert.True(runtimeFactoryStart > bootstrapperStart, "Runtime factory registration was not found.");

        var bootstrapperRegistration = composition[bootstrapperStart..runtimeFactoryStart];
        Assert.DoesNotContain(
            ".EnsureStartedAsync(",
            bootstrapperRegistration,
            StringComparison.Ordinal);
        var runtimeRegistration = composition[runtimeFactoryStart..];
        Assert.Contains(".InitializeAsync()", runtimeRegistration,
            StringComparison.Ordinal);
        Assert.DoesNotContain(".GetRequiredRuntime()", runtimeRegistration,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StartupFailureDoesNotMisreportEveryLocalFailureAsConnectivity()
    {
        var app = ReadWorkspaceFile("src", "Deep.Client.Maui", "App.xaml.cs");

        Assert.Contains(
            "Создание аккаунта не требует подключения к сети.",
            app,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Проверьте подключение и повторите попытку.",
            app,
            StringComparison.Ordinal);
    }

    private static string ReadMauiProgram()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "Deep.Client.Maui.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(
            directory!.FullName,
            "src",
            "Deep.Client.Maui",
            "MauiProgram.cs"));
    }

    private static string ReadWorkspaceFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "Deep.Client.Maui.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine([directory!.FullName, .. segments]));
    }

    private static string ExtractMethodBody(string source, string declaration)
    {
        var declarationIndex = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(declarationIndex >= 0, $"Method declaration '{declaration}' was not found.");

        var bodyStart = source.IndexOf('{', declarationIndex);
        Assert.True(bodyStart >= 0, $"Method declaration '{declaration}' has no body.");

        var depth = 0;
        for (var index = bodyStart; index < source.Length; index++)
        {
            switch (source[index])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return source[(bodyStart + 1)..index];
                    }
                    break;
            }
        }

        throw new InvalidDataException($"Method declaration '{declaration}' has an unterminated body.");
    }
}
