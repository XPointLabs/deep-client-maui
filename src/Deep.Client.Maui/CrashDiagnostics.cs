using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui;

internal static class CrashDiagnostics
{
    private static readonly object Sync = new();
    private static readonly Channel<InfoEntry> InfoEntries = Channel.CreateBounded<InfoEntry>(new BoundedChannelOptions(256)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest
    });
    private const string LogFileName = "crash.log";
    private const long MaxLogBytes = 256 * 1024;
    private const int MaxEntryChars = 16 * 1024;
    private const int MaxSanitizeInputChars = 64 * 1024;
    private static readonly Regex RecoveryPhrasePattern = new(
        @"(?im)\b(seed(?:[ _]?phrase)?|mnemonic|recovery[ _]?phrase)\b['""]?(?<separator>\s*[:=]\s*)['""]?[^\r\n,;}&\]]{1,2048}",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex SensitivePairPattern = new(
        @"(?i)\b(authorization|bearer|token|access_token|refresh_token|password|secret|seed|mnemonic|recovery[ _]?phrase)\b\s*[:=]\s*['""]?[^'""\s&]+",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex LongSecretPattern = new(
        @"\b([a-fA-F0-9]{48,}|[A-Za-z0-9_\-]{64,})\b",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    static CrashDiagnostics()
    {
        _ = Task.Run(ProcessInfoEntriesAsync);
    }

    internal static string LogPath => Path.Combine(ResolveDiagnosticRoot(), LogFileName);

    private static string ResolveDiagnosticRoot()
    {
        return AppDataPath.Resolve();
    }

    internal static void LogException(string source, Exception? exception, string? details = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("==== Deep crash ====");
        builder.AppendLine($"UTC: {DateTimeOffset.UtcNow:O}");
        builder.AppendLine($"Source: {source}");

        if (!string.IsNullOrWhiteSpace(details))
        {
            builder.AppendLine($"Details: {Sanitize(details)}");
        }

        if (exception is not null)
        {
            builder.AppendLine($"Exception: {exception.GetType().FullName}");
            builder.AppendLine($"Message: {Sanitize(exception.Message)}");
            builder.AppendLine("Stack:");
            builder.AppendLine(Sanitize(exception.ToString()));
        }

        builder.AppendLine();

        TryAppend(builder.ToString());
    }

    internal static void LogInfo(string source, string message)
    {
        InfoEntries.Writer.TryWrite(new InfoEntry(DateTimeOffset.UtcNow, source, message));
    }

    private static async Task ProcessInfoEntriesAsync()
    {
        try
        {
            await foreach (var entry in InfoEntries.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var text = $"[{entry.Timestamp:O}] [{Sanitize(entry.Source)}] {Sanitize(entry.Message)}{Environment.NewLine}";
                TryAppend(text);
            }
        }
        catch
        {
            // Logging must never terminate the process or surface an unobserved task failure.
        }
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var bounded = value.Length <= MaxSanitizeInputChars
            ? value
            : value[..MaxSanitizeInputChars] + $"{Environment.NewLine}[input truncated]";
        try
        {
            var redacted = RecoveryPhrasePattern.Replace(
                bounded,
                match => match.Groups[1].Value + match.Groups["separator"].Value + "[redacted]");
            redacted = SensitivePairPattern.Replace(redacted, match =>
            {
                var equalsIndex = match.Value.IndexOf('=');
                var colonIndex = match.Value.IndexOf(':');
                var separatorIndex = equalsIndex < 0
                    ? colonIndex
                    : colonIndex < 0 ? equalsIndex : Math.Min(equalsIndex, colonIndex);

                return separatorIndex < 0
                    ? "[redacted]"
                    : match.Value[..(separatorIndex + 1)] + "[redacted]";
            });

            redacted = LongSecretPattern.Replace(redacted, "[redacted]");
            return redacted.Length <= MaxEntryChars
                ? redacted
                : redacted[..MaxEntryChars] + $"{Environment.NewLine}[truncated]";
        }
        catch (RegexMatchTimeoutException)
        {
            return "[redacted: diagnostic sanitization timed out]";
        }
    }

    internal static string SanitizeForTests(string value) => Sanitize(value);

    private static void TryAppend(string text)
    {
        try
        {
            var path = LogPath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            lock (Sync)
            {
                RotateIfNeeded(path, Encoding.UTF8.GetByteCount(text));
                File.AppendAllText(path, text, Encoding.UTF8);
            }
        }
        catch
        {
            // Never throw from crash logger.
        }
    }

    private static void RotateIfNeeded(string path, int incomingBytes)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var currentLength = new FileInfo(path).Length;
        if (currentLength + incomingBytes <= MaxLogBytes)
        {
            return;
        }

        var rotatedPath = path + ".1";
        if (File.Exists(rotatedPath))
        {
            File.Delete(rotatedPath);
        }

        File.Move(path, rotatedPath);
    }

    private sealed record InfoEntry(DateTimeOffset Timestamp, string Source, string Message);
}
