namespace Deep.AndroidRunner;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && string.Equals(args[0], "--version", StringComparison.Ordinal))
        {
            Console.WriteLine(RunnerOptions.Version);
            return 0;
        }

        RunnerOptions options;
        try
        {
            options = RunnerOptions.Parse(args);
        }
        catch (RunnerConfigurationException)
        {
            Console.Error.WriteLine("deep-android-runner: configuration rejected");
            return 2;
        }

        RunnerOutcome outcome;
        try
        {
            var adbPath = Path.Combine(AppContext.BaseDirectory, "adb.exe");
            var runner = new AndroidDeviceRun(options, new AdbClient(adbPath));
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            outcome = await runner.ExecuteAsync(timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            outcome = new RunnerOutcome(
                false,
                "runner-bootstrap-failed",
                new TestCounters(total: 2),
                new DeviceAttestation());
        }

        try
        {
            EvidenceWriter.Write(options, outcome);
        }
        catch
        {
            Console.Error.WriteLine("deep-android-runner: evidence write failed");
            return 3;
        }

        if (!outcome.Passed)
        {
            Console.Error.WriteLine("deep-android-runner: execution failed");
            return 4;
        }

        return 0;
    }
}
