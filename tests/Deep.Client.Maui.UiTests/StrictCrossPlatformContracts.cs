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

    internal static string ExtractExactUiHierarchy(string output)
    {
        const string opening = "<?xml";
        const string closing = "</hierarchy>";
        var start = output.IndexOf(opening, StringComparison.Ordinal);
        var end = output.IndexOf(closing, StringComparison.Ordinal);
        if (start != 0 || end < 0 ||
            output.IndexOf(opening, opening.Length, StringComparison.Ordinal) >= 0 ||
            output.IndexOf(closing, end + closing.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException("uiautomator did not emit exactly one bounded XML hierarchy.");
        }
        var suffix = output[(end + closing.Length)..].Trim();
        if (!string.Equals(suffix, "UI hierchary dumped to: /dev/tty", StringComparison.Ordinal) &&
            !string.Equals(suffix, "UI hierarchy dumped to: /dev/tty", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("uiautomator emitted unexpected trailing output.");
        }
        return output[..(end + closing.Length)];
    }

    internal static AndroidNode FindExactlyOneResourceId(string xml, string resourceId)
    {
        var matches = FindAllResourceIds(xml, resourceId);

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"Required Android resource-id was not present: {resourceId}."),
            _ => throw new InvalidOperationException($"Android resource-id was not unique: {resourceId} ({matches.Length} matches).")
        };
    }

    internal static AndroidNode[] FindAllResourceIds(string xml, string resourceId)
    {
        ValidateResourceId(resourceId, "resource-id");
        var document = XDocument.Parse(xml, LoadOptions.None);
        return document.Descendants("node")
            .Where(node => string.Equals(
                (string?)node.Attribute("resource-id"),
                resourceId,
                StringComparison.Ordinal))
            .Select(AndroidNode.From)
            .ToArray();
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

    internal static void RequireInvalidSessionId(string value)
    {
        try
        {
            _ = RequireSessionId(value, "negative test identity");
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException("The negative-flow identity must be syntactically invalid. Valid unknown identities are intentionally accepted.");
    }

    internal static string Sha256File(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    internal static string Sha256Tree(string root)
    {
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root) ||
            File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("Pinned tree must be an existing absolute regular directory.");
        }

        var entries = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).ToArray();
        if (entries.Any(path => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)))
        {
            throw new InvalidOperationException("Pinned tree must not contain reparse points.");
        }

        var canonical = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var lines = entries.Where(File.Exists)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path =>
            {
                var relative = Path.GetFullPath(path)[(canonical.Length + 1)..].Replace('\\', '/');
                return $"{relative}\t{new FileInfo(path).Length}\t{Sha256File(path)}";
            });
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static void RequirePinnedFile(string path, string expectedSha256, string role)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
        {
            throw new InvalidOperationException($"{role} must be an existing absolute path.");
        }

        if (!Regex.IsMatch(expectedSha256, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant) ||
            !string.Equals(Sha256File(path), expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{role} does not match its independently pinned SHA-256.");
        }
    }

    internal static void RequirePinnedTree(string path, string expectedSha256, string role)
    {
        if (!Regex.IsMatch(expectedSha256, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant) ||
            !string.Equals(Sha256Tree(path), expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{role} does not match its independently pinned tree SHA-256.");
        }
    }

    internal static void RequirePhysicalDeviceInventory(
        string fingerprint,
        string model,
        string product,
        string hardware,
        string characteristics,
        int sdk)
    {
        if (sdk is < 26 or > 100 ||
            new[] { fingerprint, model, product, hardware, characteristics }.Any(string.IsNullOrWhiteSpace) ||
            Regex.IsMatch(
                $"{fingerprint} {model} {product} {hardware} {characteristics}",
                "(?i)(emulator|generic|goldfish|ranchu|vbox|qemu|simulator|sdk[_-]?gphone)",
                RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException("Approved inventory must identify a non-virtual physical device.");
        }
    }

    internal static void RequireExactVersion(ProcessResult result, string expected, string role)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{role} version command failed.");
        }

        var actual = (result.Output + result.Error).Trim();
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{role} version output does not match the approved inventory.");
        }
    }

    internal static ProcessResult RunBounded(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        if (!Path.IsPathFullyQualified(fileName) && !string.Equals(fileName, "git", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Tool execution requires an absolute path.");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            CloseRedirectedPipes(process);
            TryDrainAfterClose(stdout, stderr);
            throw new TimeoutException("Bounded tool process exceeded its timeout.");
        }

        if (!Task.WaitAll([stdout, stderr], timeout))
        {
            // A descendant can outlive an exited parent while retaining inherited stdout/
            // stderr handles.  Never block on GetResult in that state.
            try { process.Kill(entireProcessTree: true); } catch { }
            CloseRedirectedPipes(process);
            TryDrainAfterClose(stdout, stderr);
            throw new TimeoutException("Tool parent exited but redirected output did not close before the bounded drain deadline.");
        }

        if (!stdout.IsCompletedSuccessfully || !stderr.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException("Tool output could not be drained safely.");
        }

        return new ProcessResult(process.ExitCode, stdout.Result, stderr.Result);

        static void CloseRedirectedPipes(Process target)
        {
            try { target.StandardOutput.Dispose(); } catch { }
            try { target.StandardError.Dispose(); } catch { }
        }

        static void TryDrainAfterClose(Task stdoutTask, Task stderrTask)
        {
            try { _ = Task.WaitAll([stdoutTask, stderrTask], TimeSpan.FromSeconds(1)); }
            catch { }
        }
    }

    internal sealed record ProcessResult(int ExitCode, string Output, string Error);

    internal static string RequireCurrentCommit(string repositoryRoot, string expectedCommit)
    {
        var result = RunBounded(
            "git",
            ["-c", $"safe.directory={repositoryRoot.Replace('\\', '/')}", "-C", repositoryRoot, "rev-parse", "HEAD"],
            TimeSpan.FromSeconds(15));
        var actual = result.Output.Trim();
        if (result.ExitCode != 0 ||
            !Regex.IsMatch(actual, "^[a-f0-9]{40}$", RegexOptions.CultureInvariant) ||
            !string.Equals(actual, expectedCommit, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Actual Git HEAD does not match the pinned source commit.");
        }

        return actual;
    }

    internal static DownloadsSnapshot SnapshotDownloads(string directory)
    {
        if (!Path.IsPathFullyQualified(directory))
        {
            throw new InvalidOperationException("Downloads directory must be absolute.");
        }

        Directory.CreateDirectory(directory);
        return new DownloadsSnapshot(
            Path.GetFullPath(directory),
            Directory.EnumerateFiles(directory).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    internal sealed record DownloadsSnapshot(string Directory, IReadOnlySet<string> Preexisting)
    {
        internal string WaitForNewCorrelatedFile(string originalFileName, TimeSpan timeout, ICollection<string>? runOwned = null)
        {
            var stem = Path.GetFileNameWithoutExtension(originalFileName);
            var extension = Path.GetExtension(originalFileName);
            var correlated = new Regex(
                "^" + Regex.Escape(stem) + "(?: \\([2-9][0-9]*\\))?" + Regex.Escape(extension) + "$",
                RegexOptions.CultureInvariant);
            var until = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < until)
            {
                var matches = System.IO.Directory.EnumerateFiles(Directory)
                    .Select(Path.GetFullPath)
                    .Where(path => !Preexisting.Contains(path) && correlated.IsMatch(Path.GetFileName(path)))
                    .ToArray();
                if (runOwned is not null)
                {
                    foreach (var path in matches)
                    {
                        if (!runOwned.Contains(path, StringComparer.OrdinalIgnoreCase)) runOwned.Add(path);
                    }
                }
                if (matches.Length == 1)
                {
                    return matches[0];
                }
                if (matches.Length > 1)
                {
                    throw new InvalidOperationException("Save created more than one new correlated Downloads file.");
                }
                Thread.Sleep(200);
            }

            throw new InvalidOperationException("Production Save did not create one new correlated Downloads file.");
        }
    }

    internal sealed class CleanupScope
    {
        private readonly List<Action> actions = [];

        internal void Add(Action action) => actions.Add(action);

        internal void RunAll()
        {
            var failures = new List<Exception>();
            foreach (var action in actions)
            {
                try { action(); }
                catch (Exception exception) { failures.Add(exception); }
            }
            if (failures.Count > 0)
            {
                throw new AggregateException("One or more independent strict-lane cleanup steps failed.", failures);
            }
        }
    }

    internal sealed class AttemptCleanupState
    {
        internal bool AndroidPackageMutationAttempted { get; private set; }
        internal bool FixturePushAttempted { get; private set; }

        internal void Register(CleanupScope cleanup, Action clearAndroidPackage, Action deleteFixture)
        {
            cleanup.Add(() => { if (AndroidPackageMutationAttempted) clearAndroidPackage(); });
            cleanup.Add(() => { if (FixturePushAttempted) deleteFixture(); });
        }

        internal void BeginAndroidPackageMutation() => AndroidPackageMutationAttempted = true;
        internal void BeginFixturePush() => FixturePushAttempted = true;
    }

    internal sealed record AndroidNode(
        string ResourceId,
        string Text,
        string ContentDescription,
        AndroidBounds Bounds)
    {
        internal string AccessibleText => string.IsNullOrWhiteSpace(Text)
            ? ContentDescription
            : Text;

        internal static AndroidNode From(XElement node)
        {
            var resourceId = (string?)node.Attribute("resource-id") ?? throw new InvalidOperationException("uiautomator node has no resource-id.");
            var bounds = AndroidBounds.Parse((string?)node.Attribute("bounds") ?? string.Empty);
            return new AndroidNode(
                resourceId,
                (string?)node.Attribute("text") ?? string.Empty,
                (string?)node.Attribute("content-desc") ?? string.Empty,
                bounds);
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
            if (!Regex.IsMatch(value, "^[A-Za-z0-9._:+-]{1,128}$", RegexOptions.CultureInvariant))
            {
                throw new InvalidOperationException($"Evidence value '{key}' is not safe to publish.");
            }

            values.Add(key, value);
        }

        internal void Write(string path) => File.WriteAllText(path, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
    }
}
