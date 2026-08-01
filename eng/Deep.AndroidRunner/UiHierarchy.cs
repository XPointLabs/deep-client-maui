using System.Globalization;
using System.Xml;

namespace Deep.AndroidRunner;

internal sealed record UiTarget(int X, int Y);

internal static class UiHierarchy
{
    private const int MaxXmlCharacters = 2 * 1024 * 1024;
    private const int MaxNodes = 10_000;

    internal static UiTarget FindExactResource(string rawOutput, string resourceId)
    {
        if (rawOutput.Length > MaxXmlCharacters)
        {
            throw new RunnerExecutionException("uiautomator-output-limit");
        }

        var start = rawOutput.IndexOf("<hierarchy", StringComparison.Ordinal);
        if (start < 0)
        {
            throw new RunnerExecutionException("uiautomator-xml-missing");
        }

        using var stringReader = new StringReader(rawOutput[start..]);
        using var reader = XmlReader.Create(
            stringReader,
            new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxXmlCharacters,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = true
            });

        UiTarget? target = null;
        var nodes = 0;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element ||
                !string.Equals(reader.Name, "node", StringComparison.Ordinal))
            {
                continue;
            }

            nodes++;
            if (nodes > MaxNodes)
            {
                throw new RunnerExecutionException("uiautomator-node-limit");
            }

            if (!string.Equals(
                    reader.GetAttribute("resource-id"),
                    resourceId,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (target is not null)
            {
                throw new RunnerExecutionException("uiautomator-selector-ambiguous");
            }

            target = ParseBounds(reader.GetAttribute("bounds"));
        }

        return target ?? throw new RunnerExecutionException("uiautomator-selector-missing");
    }

    internal static bool ContainsExactResource(string rawOutput, string resourceId)
    {
        try
        {
            _ = FindExactResource(rawOutput, resourceId);
            return true;
        }
        catch (RunnerExecutionException exception)
            when (exception.Code == "uiautomator-selector-missing")
        {
            return false;
        }
    }

    private static UiTarget ParseBounds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new RunnerExecutionException("uiautomator-bounds-missing");
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            value,
            "^\\[(?<left>\\d{1,5}),(?<top>\\d{1,5})\\]\\[(?<right>\\d{1,5}),(?<bottom>\\d{1,5})\\]$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant |
            System.Text.RegularExpressions.RegexOptions.NonBacktracking);
        if (!match.Success)
        {
            throw new RunnerExecutionException("uiautomator-bounds-invalid");
        }

        var left = int.Parse(match.Groups["left"].Value, CultureInfo.InvariantCulture);
        var top = int.Parse(match.Groups["top"].Value, CultureInfo.InvariantCulture);
        var right = int.Parse(match.Groups["right"].Value, CultureInfo.InvariantCulture);
        var bottom = int.Parse(match.Groups["bottom"].Value, CultureInfo.InvariantCulture);
        if (left < 0 || top < 0 || right <= left || bottom <= top ||
            right > 20_000 || bottom > 20_000)
        {
            throw new RunnerExecutionException("uiautomator-bounds-invalid");
        }

        return new UiTarget(left + ((right - left) / 2), top + ((bottom - top) / 2));
    }
}
