using System.Text;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui;

internal static class CrashDiagnostics
{
    private static readonly object Sync = new();
    private const string LogFileName = "crash.log";

    internal static string LogPath => Path.Combine(FileSystem.Current.AppDataDirectory, LogFileName);

    internal static void LogException(string source, Exception? exception, string? details = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("==== Deep crash ====");
        builder.AppendLine($"UTC: {DateTimeOffset.UtcNow:O}");
        builder.AppendLine($"Source: {source}");

        if (!string.IsNullOrWhiteSpace(details))
        {
            builder.AppendLine($"Details: {details}");
        }

        if (exception is not null)
        {
            builder.AppendLine($"Exception: {exception.GetType().FullName}");
            builder.AppendLine($"Message: {exception.Message}");
            builder.AppendLine("Stack:");
            builder.AppendLine(exception.ToString());
        }

        builder.AppendLine();

        TryAppend(builder.ToString());
    }

    internal static void LogInfo(string source, string message)
    {
        var text = $"[{DateTimeOffset.UtcNow:O}] [{source}] {message}{Environment.NewLine}";
        TryAppend(text);
    }

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
                File.AppendAllText(path, text, Encoding.UTF8);
            }
        }
        catch
        {
            // Never throw from crash logger.
        }
    }
}
