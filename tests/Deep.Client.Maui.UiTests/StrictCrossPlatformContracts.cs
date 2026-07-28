using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Deep.Client.Maui.UiTests;

/// <summary>
/// Small, deliberately dependency-free primitives used by the physical lane.  Keeping these
/// parsers separate makes it possible to prove the selector/coordinate safety rules without a
/// connected phone or an interactive desktop session.
/// </summary>
internal static class StrictCrossPlatformContracts
{
    internal const string AndroidPackage = "network.xpoint.deep.e2e";
    private static readonly Regex ResourceId = new(
        "^[a-zA-Z][a-zA-Z0-9_.]*:id/[a-zA-Z][a-zA-Z0-9_.]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Bounds = new(
        "^\\[(?<left>-?\\d+),(?<top>-?\\d+)\\]\\[(?<right>-?\\d+),(?<bottom>-?\\d+)\\]$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    // Session account identifiers are wire-format identifiers, not arbitrary hex strings.
    // Keep this exact here: accepting a shorter value would make the negative-contact test
    // exercise only client-side validation rather than a syntactically valid absent peer.
    private static readonly Regex SessionId = new("^(?:05|15|25)[0-9a-f]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static AndroidNode FindExactlyOneResourceId(string xml, string resourceId)
    {
        ValidateResourceId(resourceId, "resource-id");
        var document = XDocument.Parse(xml, LoadOptions.None);
        var matches = document.Descendants("node")
            .Where(node => string.Equals((string?)node.Attribute("resource-id"), resourceId, StringComparison.Ordinal))
            .Select(AndroidNode.From)
            .ToArray();

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"Required Android resource-id was not present: {resourceId}."),
            _ => throw new InvalidOperationException($"Android resource-id was not unique: {resourceId} ({matches.Length} matches).")
        };
    }

    internal static AndroidNode? FindOptionalResourceId(string xml, string resourceId)
    {
        ValidateResourceId(resourceId, "resource-id");
        var document = XDocument.Parse(xml, LoadOptions.None);
        var matches = document.Descendants("node")
            .Where(node => string.Equals((string?)node.Attribute("resource-id"), resourceId, StringComparison.Ordinal))
            .Select(AndroidNode.From)
            .ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException($"Android resource-id was not unique: {resourceId} ({matches.Length} matches).")
        };
    }

    internal static AndroidNode FindExactlyOneResourceIdWithText(string xml, string resourceId, string text)
    {
        ValidateResourceId(resourceId, "resource-id");
        var document = XDocument.Parse(xml, LoadOptions.None);
        var matches = document.Descendants("node")
            .Where(node => string.Equals((string?)node.Attribute("resource-id"), resourceId, StringComparison.Ordinal))
            .Select(AndroidNode.From)
            .Where(node => string.Equals(node.Text, text, StringComparison.Ordinal))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"Required Android resource-id/text pair was not present: {resourceId}."),
            _ => throw new InvalidOperationException($"Android resource-id/text pair was not unique: {resourceId} ({matches.Length} matches).")
        };
    }

    internal static AndroidNode FindExactlyOneResourceIdContainingText(string xml, string resourceId, string text)
    {
        ValidateResourceId(resourceId, "resource-id");
        if (string.IsNullOrEmpty(text))
        {
            throw new InvalidOperationException("Expected Android text must not be empty.");
        }

        var document = XDocument.Parse(xml, LoadOptions.None);
        var matches = document.Descendants("node")
            .Where(node => string.Equals((string?)node.Attribute("resource-id"), resourceId, StringComparison.Ordinal))
            .Select(AndroidNode.From)
            .Where(node => node.Text.Contains(text, StringComparison.Ordinal))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"Required Android resource-id/text marker was not present: {resourceId}."),
            _ => throw new InvalidOperationException($"Android resource-id/text marker was ambiguous: {resourceId} ({matches.Length} matches).")
        };
    }

    internal static void ValidateResourceId(string value, string name)
    {
        if (!ResourceId.IsMatch(value))
        {
            throw new InvalidOperationException($"{name} must be an exact Android package:id/name resource-id.");
        }
    }

    internal static string RequireSessionId(string value, string surface)
    {
        var normalized = value.Trim();
        if (!SessionId.IsMatch(normalized))
        {
            throw new InvalidOperationException($"{surface} did not expose a valid 66-character lowercase Session identity with a 05, 15, or 25 prefix.");
        }

        return normalized;
    }

    internal static string Sha256(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static void AssertDistinctProcessIds(int first, int second)
    {
        if (first <= 0 || second <= 0 || first == second)
        {
            throw new InvalidOperationException("Windows cold restart did not produce a distinct positive process ID.");
        }
    }

    internal static void AssertSafeMarker(string marker)
    {
        if (!Regex.IsMatch(marker, "^[A-Za-z0-9._-]{12,96}$", RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException("The generated marker is outside the safe evidence alphabet.");
        }
    }

    internal static string NewMarker(string role) => $"strict-{role}-{Guid.NewGuid():N}";

    internal sealed record AndroidNode(string ResourceId, string Text, AndroidBounds Bounds)
    {
        internal static AndroidNode From(XElement node)
        {
            var resourceId = (string?)node.Attribute("resource-id") ?? throw new InvalidOperationException("uiautomator node has no resource-id.");
            var bounds = AndroidBounds.Parse((string?)node.Attribute("bounds") ?? string.Empty);
            return new AndroidNode(resourceId, (string?)node.Attribute("text") ?? string.Empty, bounds);
        }
    }

    internal readonly record struct AndroidBounds(int Left, int Top, int Right, int Bottom)
    {
        internal static AndroidBounds Parse(string value)
        {
            var match = Bounds.Match(value);
            if (!match.Success ||
                !int.TryParse(match.Groups["left"].Value, out var left) ||
                !int.TryParse(match.Groups["top"].Value, out var top) ||
                !int.TryParse(match.Groups["right"].Value, out var right) ||
                !int.TryParse(match.Groups["bottom"].Value, out var bottom) ||
                right <= left || bottom <= top)
            {
                throw new InvalidOperationException("uiautomator bounds are invalid.");
            }

            return new AndroidBounds(left, top, right, bottom);
        }

        internal (int X, int Y) Center => (checked(Left + ((Right - Left) / 2)), checked(Top + ((Bottom - Top) / 2)));
    }

    internal sealed record ApkMetadata(string PackageName, string VersionCode, string VersionName, string Sha256, string SigningDigest)
    {
        internal static ApkMetadata ParseAaptBadging(string output, string sha256)
        {
            var packageLine = output.Split('\n').FirstOrDefault(line => line.StartsWith("package: ", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("aapt did not return APK package metadata.");
            var packageMatch = Regex.Match(packageLine, "name='(?<name>[^']+)'\\s+versionCode='(?<code>[^']+)'\\s+versionName='(?<version>[^']+)'", RegexOptions.CultureInvariant);
            if (!packageMatch.Success || string.IsNullOrWhiteSpace(packageMatch.Groups["code"].Value) || string.IsNullOrWhiteSpace(packageMatch.Groups["version"].Value))
            {
                throw new InvalidOperationException("aapt package metadata was malformed.");
            }

            return new ApkMetadata(packageMatch.Groups["name"].Value, packageMatch.Groups["code"].Value, packageMatch.Groups["version"].Value, sha256, string.Empty);
        }

        internal ApkMetadata WithSigningDigest(string digest)
        {
            if (!Regex.IsMatch(digest, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant))
            {
                throw new InvalidOperationException("APK signing certificate SHA-256 digest was malformed.");
            }

            return this with { SigningDigest = digest };
        }
    }

    internal sealed class SanitizedEvidence
    {
        private readonly Dictionary<string, object?> values = new(StringComparer.Ordinal);

        internal void AddBoolean(string key, bool value) => values.Add(key, value);
        internal void AddHash(string key, string sensitiveValue) => values.Add(key, Sha256(sensitiveValue));
        internal void AddSafeValue(string key, string value)
        {
            if (!Regex.IsMatch(value, "^[A-Za-z0-9._-]{1,128}$", RegexOptions.CultureInvariant))
            {
                throw new InvalidOperationException($"Evidence value '{key}' is not safe to publish.");
            }

            values.Add(key, value);
        }

        internal void Write(string path) => File.WriteAllText(path, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
    }
}
