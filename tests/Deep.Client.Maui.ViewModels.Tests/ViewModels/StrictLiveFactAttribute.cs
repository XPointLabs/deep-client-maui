using Xunit;

namespace Deep.Client.Maui.ViewModels.Tests.ViewModels;

[AttributeUsage(AttributeTargets.Method)]
public sealed class StrictLiveFactAttribute : FactAttribute
{
    public StrictLiveFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("DEEP_STRICT_LIVE"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "NOT-RUN: set DEEP_STRICT_LIVE=1 and provide all live infrastructure endpoints.";
        }
    }
}
