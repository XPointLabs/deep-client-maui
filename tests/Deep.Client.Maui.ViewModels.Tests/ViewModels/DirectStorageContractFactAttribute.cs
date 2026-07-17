using Xunit;

namespace Deep.Client.Maui.ViewModels.Tests.ViewModels;

[AttributeUsage(AttributeTargets.Method)]
public sealed class DirectStorageContractFactAttribute : FactAttribute
{
    public DirectStorageContractFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("DEEP_STRICT_DIRECT_STORAGE"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "NOT-RUN: direct storage is diagnostic and cannot satisfy routed release evidence.";
        }
    }
}
