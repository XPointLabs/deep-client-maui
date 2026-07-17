using Microsoft.Maui.Storage;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

[Collection("Environment variables")]
public sealed class AppDataPathTests
{
    [Theory]
    [InlineData("stub")]
    [InlineData("live")]
    public void StrictDebugBootstrapUsesMandatoryIsolatedRoot(string bootstrap)
    {
        var root = Path.Combine(Path.GetTempPath(), "deep-strict-appdata", Guid.NewGuid().ToString("N"));
        using var environment = new EnvironmentScope(
            ("DEEP_STRICT_WINDOWS_UI", "1"),
            ("DEEP_E2E_BOOTSTRAP", bootstrap),
            ("DEEP_E2E_APPDATA_ROOT", root));
        try
        {
            Assert.Equal(Path.GetFullPath(root), AppDataPath.Resolve());
            Assert.NotEqual(
                Path.GetFullPath(FileSystem.AppDataDirectory),
                Path.GetFullPath(AppDataPath.Resolve()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StrictDebugBootstrapRejectsMissingRoot()
    {
        using var environment = new EnvironmentScope(
            ("DEEP_STRICT_WINDOWS_UI", "1"),
            ("DEEP_E2E_BOOTSTRAP", "live"),
            ("DEEP_E2E_APPDATA_ROOT", null));
        Assert.Throws<InvalidOperationException>(AppDataPath.Resolve);
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly (string Name, string? Value)[] original;

        public EnvironmentScope(params (string Name, string? Value)[] values)
        {
            original = values
                .Select(value => (value.Name, Environment.GetEnvironmentVariable(value.Name)))
                .ToArray();
            foreach (var (name, value) in values)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in original)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}

[CollectionDefinition("Environment variables", DisableParallelization = true)]
public sealed class EnvironmentVariableCollection;
