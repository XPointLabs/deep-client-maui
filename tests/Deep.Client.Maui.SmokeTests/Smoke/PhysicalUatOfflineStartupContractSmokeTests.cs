namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class PhysicalUatOfflineStartupContractSmokeTests
{
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
