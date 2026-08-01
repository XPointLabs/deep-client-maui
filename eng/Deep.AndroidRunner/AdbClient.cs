using System.Diagnostics;
using System.Text;

namespace Deep.AndroidRunner;

internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

internal interface IAdbClient
{
    Task<CommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class AdbClient : IAdbClient
{
    private const int MaxOutputCharacters = 4 * 1024 * 1024;
    private readonly string executablePath;

    internal AdbClient(string executablePath)
    {
        executablePath = Path.GetFullPath(executablePath);
        if (!File.Exists(executablePath) ||
            !string.Equals(
                Path.GetFileName(executablePath),
                "adb.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new RunnerConfigurationException("adb-unavailable");
        }

        this.executablePath = executablePath;
    }

    public async Task<CommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (arguments.Count == 0 ||
            arguments.Any(argument =>
                argument.Contains('\r', StringComparison.Ordinal) ||
                argument.Contains('\n', StringComparison.Ordinal)))
        {
            throw new RunnerExecutionException("invalid-adb-arguments");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new RunnerExecutionException("adb-start-failed");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var standardOutput = ReadBoundedAsync(process.StandardOutput, timeoutSource.Token);
        var standardError = ReadBoundedAsync(process.StandardError, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            return new CommandResult(
                process.ExitCode,
                await standardOutput.ConfigureAwait(false),
                await standardError.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw new RunnerExecutionException("adb-timeout");
        }
        catch (RunnerExecutionException)
        {
            TryKill(process);
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                return builder.ToString();
            }

            if (builder.Length + count > MaxOutputCharacters)
            {
                throw new RunnerExecutionException("adb-output-limit");
            }

            builder.Append(buffer, 0, count);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
        }
        catch
        {
            // The caller still receives the original bounded failure.
        }
    }
}

internal sealed class RunnerExecutionException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}
