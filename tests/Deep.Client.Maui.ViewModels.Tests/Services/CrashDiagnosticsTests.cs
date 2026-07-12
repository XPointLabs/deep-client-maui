namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class CrashDiagnosticsTests
{
    [Fact]
    public void SanitizeRedactsEveryRecoveryPhraseWordFromExceptionText()
    {
        var words = new[]
        {
            "amber", "anchor", "april", "arrow", "atom", "aurora",
            "autumn", "badge", "bamboo", "beacon", "berry", "blade"
        };
        var input = $"failure recovery_phrase={string.Join(' ', words)}{Environment.NewLine}at Deep.Client.Run()";

        var sanitized = Deep.Client.Maui.CrashDiagnostics.SanitizeForTests(input);

        Assert.Contains("recovery_phrase=[redacted]", sanitized, StringComparison.Ordinal);
        foreach (var word in words)
        {
            Assert.DoesNotContain(word, sanitized, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SanitizeRedactsQuotedJsonMnemonic()
    {
        var sanitized = Deep.Client.Maui.CrashDiagnostics.SanitizeForTests(
            "{\"mnemonic\":\"one two three four five six seven eight nine ten eleven twelve\",\"status\":\"failed\"}");

        Assert.DoesNotContain("one two", sanitized, StringComparison.Ordinal);
        Assert.Contains("[redacted]", sanitized, StringComparison.Ordinal);
    }
}
