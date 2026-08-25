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
    private static readonly Encoding ToolOutputEncoding =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
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

    internal static int CountResourceIdsWithAccessibleText(
        string xml,
        string resourceId,
        string exactText)
    {
        ValidateResourceId(resourceId, "resource-id");
        ArgumentException.ThrowIfNullOrEmpty(exactText);
        var document = XDocument.Parse(xml, LoadOptions.None);
        return document.Descendants("node")
            .Count(node => string.Equals(
                    (string?)node.Attribute("resource-id"), resourceId,
                    StringComparison.Ordinal)
                && string.Equals(ReadAccessibleText(node), exactText,
                    StringComparison.Ordinal));
    }

    internal static CanonicalImageMetadata ParseCanonicalImageMetadata(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        var fields = value.Split("; ", StringSplitOptions.None);
        if (fields.Length != 4
            || string.IsNullOrWhiteSpace(fields[0])
            || string.IsNullOrWhiteSpace(fields[1])
            || !long.TryParse(fields[2], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var sizeBytes)
            || sizeBytes <= 0)
        {
            throw new InvalidOperationException(
                "Image metadata is not the canonical filename/MIME/SizeBytes/dimensions structure.");
        }

        var dimensions = fields[3].Split('x', StringSplitOptions.None);
        if (dimensions.Length != 2
            || !int.TryParse(dimensions[0], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(dimensions[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var height)
            || width <= 0
            || height <= 0)
        {
            throw new InvalidOperationException(
                "Image metadata dimensions are not canonical positive integers.");
        }

        var parsed = new CanonicalImageMetadata(
            fields[0], fields[1], sizeBytes, width, height);
        if (!string.Equals(value, parsed.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Image metadata is not canonically serialized.");
        }
        return parsed;
    }

    internal static AndroidNode FindLastResourceIdContainingDescendant(
        string xml,
        string resourceId,
        string descendantResourceId)
    {
        ValidateResourceId(resourceId, "resource-id");
        ValidateResourceId(descendantResourceId, "descendant resource-id");
        var document = XDocument.Parse(xml, LoadOptions.None);
        var candidates = document.Descendants("node")
            .Where(node => string.Equals(
                (string?)node.Attribute("resource-id"),
                resourceId,
                StringComparison.Ordinal))
            .ToArray();
        if (candidates.Length == 0)
        {
            throw new InvalidOperationException(
                $"Required Android resource-id was not present: {resourceId}.");
        }

        var last = candidates[^1];
        var descendants = last.Descendants("node")
            .Count(node => string.Equals(
                (string?)node.Attribute("resource-id"),
                descendantResourceId,
                StringComparison.Ordinal));
        if (descendants != 1)
        {
            throw new InvalidOperationException(
                $"Last Android {resourceId} did not contain exactly one {descendantResourceId} descendant.");
        }

        return AndroidNode.From(last);
    }

    internal static AndroidNode FindExactlyOneCorrelatedDescendant(
        string xml,
        string ancestorResourceId,
        string correlationResourceId,
        string correlationText,
        string targetResourceId)
    {
        ValidateResourceId(ancestorResourceId, "ancestor resource-id");
        ValidateResourceId(correlationResourceId, "correlation resource-id");
        ValidateResourceId(targetResourceId, "target resource-id");
        var document = XDocument.Parse(xml, LoadOptions.None);
        var ancestors = document.Descendants("node")
            .Where(node => string.Equals(
                (string?)node.Attribute("resource-id"), ancestorResourceId,
                StringComparison.Ordinal))
            .Where(node => node.Descendants("node").Any(descendant =>
                string.Equals((string?)descendant.Attribute("resource-id"),
                    correlationResourceId, StringComparison.Ordinal)
                && string.Equals(
                    ReadAccessibleText(descendant), correlationText,
                    StringComparison.Ordinal)))
            .ToArray();
        if (ancestors.Length != 1)
        {
            throw new InvalidOperationException(
                "Android correlation did not identify exactly one message ancestor.");
        }
        var targets = ancestors[0].Descendants("node")
            .Where(node => string.Equals(
                (string?)node.Attribute("resource-id"), targetResourceId,
                StringComparison.Ordinal))
            .Select(AndroidNode.From)
            .ToArray();
        return targets.Length == 1
            ? targets[0]
            : throw new InvalidOperationException(
                "Correlated Android message did not contain exactly one target descendant.");
    }

    internal static AndroidNode FindExactlyOneResourceIdContainingDescendantText(
        string xml,
        string ancestorResourceId,
        string descendantResourceId,
        string descendantText)
    {
        ValidateResourceId(ancestorResourceId, "ancestor resource-id");
        ValidateResourceId(descendantResourceId, "descendant resource-id");
        var document = XDocument.Parse(xml, LoadOptions.None);
        var matches = document.Descendants("node")
            .Where(node => string.Equals(
                (string?)node.Attribute("resource-id"), ancestorResourceId,
                StringComparison.Ordinal))
            .Where(node => node.Descendants("node").Any(descendant =>
                string.Equals((string?)descendant.Attribute("resource-id"),
                    descendantResourceId, StringComparison.Ordinal)
                && string.Equals(ReadAccessibleText(descendant), descendantText,
                    StringComparison.Ordinal)))
            .Select(AndroidNode.From)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidOperationException(
                "Android descendant text did not correlate exactly one ancestor.");
    }

    private static string ReadAccessibleText(XElement node)
    {
        var text = (string?)node.Attribute("text") ?? string.Empty;
        return string.IsNullOrWhiteSpace(text)
            ? (string?)node.Attribute("content-desc") ?? string.Empty
            : text;
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
        if (sdk is < 28 or > 100 ||
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
                StandardOutputEncoding = ToolOutputEncoding,
                StandardErrorEncoding = ToolOutputEncoding,
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

    internal sealed record CanonicalImageMetadata(
        string FileName,
        string MimeType,
        long SizeBytes,
        int Width,
        int Height)
    {
        public override string ToString() =>
            $"{FileName}; {MimeType}; {SizeBytes}; {Width}x{Height}";
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

        internal bool HasExactPresentation(string text, string contentDescription) =>
            string.Equals(Text, text, StringComparison.Ordinal)
            && string.Equals(ContentDescription, contentDescription, StringComparison.Ordinal);

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

    internal sealed record PrivacyRouteProof(
        IReadOnlyList<string> Primary,
        IReadOnlyList<string> Fallback,
        string Selected,
        string Entry)
    {
        internal static PrivacyRouteProof ParseExact(string value)
        {
            var fields = value.Split('|', StringSplitOptions.None);
            if (fields.Length != 6 || fields[0] != "v1" ||
                fields[1] != "path=/api/ingress/v1/frame" ||
                !fields[2].StartsWith("primary=", StringComparison.Ordinal) ||
                !fields[3].StartsWith("fallback=", StringComparison.Ordinal) ||
                !fields[4].StartsWith("selected=", StringComparison.Ordinal) ||
                !fields[5].StartsWith("entry=", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Physical privacy-route proof is not canonical.");
            }

            var primary = fields[2]["primary=".Length..].Split(',', StringSplitOptions.None);
            var fallback = fields[3]["fallback=".Length..].Split(',', StringSplitOptions.None);
            var selected = fields[4]["selected=".Length..];
            var entry = fields[5]["entry=".Length..];
            var all = primary.Concat(fallback).ToArray();
            if (primary.Length != 3 || fallback.Length != 3 ||
                all.Any(static id => !Regex.IsMatch(
                    id, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant) ||
                    id.All(static character => character == '0')) ||
                all.Distinct(StringComparer.Ordinal).Count() != 6 ||
                selected is not ("primary" or "fallback") ||
                !string.Equals(
                    entry,
                    selected == "primary" ? primary[0] : fallback[0],
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Physical privacy-route proof is not exact-three, disjoint, or selected-entry bound.");
            }

            return new PrivacyRouteProof(primary, fallback, selected, entry);
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
