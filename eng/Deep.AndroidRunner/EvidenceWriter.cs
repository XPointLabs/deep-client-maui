using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Deep.AndroidRunner;

internal static class EvidenceWriter
{
    internal static void Write(
        RunnerOptions options,
        RunnerOutcome outcome)
    {
        var junit = CreateJUnit(outcome.Counters);
        WriteAtomic(options.JUnitPath, junit);
        var junitSha256 = Sha256File(options.JUnitPath);

        var result = new
        {
            schema = RunnerOptions.Schema,
            sourceCommitSha = options.SourceCommitSha,
            releaseInvocationId = options.ReleaseInvocationId,
            laneInvocationId = options.LaneInvocationId,
            labPolicyId = options.LabPolicyId,
            labPolicySha256 = options.LabPolicySha256,
            apkSha256 = options.ApkSha256,
            packageId = options.PackageId,
            versionCode = options.VersionCode,
            versionName = options.VersionName,
            signingCertificateSha256 = options.SigningCertificateSha256,
            runnerSha256 = options.RunnerSha256,
            runnerVersion = options.RunnerVersion,
            junitSha256,
            device = new
            {
                serial = options.Serial,
                fingerprintSha256 = options.DeviceFingerprintSha256,
                productSha256 = options.DeviceProductSha256,
                hardwareSha256 = options.DeviceHardwareSha256,
                modelSha256 = options.DeviceModelSha256,
                kernelQemu = options.DeviceKernelQemu,
                sdk = options.DeviceSdk,
                @class = options.DeviceClass,
                dedicatedManaged = outcome.Device.DedicatedManaged,
                personalDataAbsent = outcome.Device.PersonalDataAbsent,
                productionPackageAbsentBefore = outcome.Device.ProductionPackageAbsentBefore,
                testPackageClearedBefore = outcome.Device.TestPackageClearedBefore,
                testPackageRemovedAfter = outcome.Device.TestPackageRemovedAfter
            },
            status = outcome.Passed ? "passed" : "failed",
            counters = new
            {
                total = outcome.Counters.Total,
                executed = outcome.Counters.Executed,
                passed = outcome.Counters.Passed,
                failed = outcome.Counters.Failed,
                skipped = outcome.Counters.Skipped
            }
        };
        WriteAtomic(
            options.ResultPath,
            JsonSerializer.Serialize(result));
    }

    internal static string CreateJUnit(TestCounters counters) =>
        FormattableString.Invariant(
            $"<testsuite tests=\"{counters.Total}\" failures=\"{counters.Failed}\" errors=\"0\" skipped=\"{counters.Skipped}\" />");

    private static void WriteAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new RunnerExecutionException("evidence-parent-invalid");
        EnsureSafeParent(directory);
        Directory.CreateDirectory(directory);
        EnsureSafeParent(directory);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            EnsureSafeParent(directory);
            if (File.Exists(path) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new RunnerExecutionException("evidence-destination-reparse");
            }

            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                .GetBytes(content);
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            EnsureSafeParent(directory);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch
            {
                // Never replace the primary evidence result with cleanup noise.
            }
        }
    }

    private static void EnsureSafeParent(string directory)
    {
        var current = Path.GetFullPath(directory);
        while (!Directory.Exists(current))
        {
            if (File.Exists(current))
            {
                throw new RunnerExecutionException("evidence-parent-not-directory");
            }

            var missingParent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(missingParent) ||
                string.Equals(missingParent, current, StringComparison.OrdinalIgnoreCase))
            {
                throw new RunnerExecutionException("evidence-parent-missing");
            }

            current = missingParent;
        }

        while (true)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new RunnerExecutionException("evidence-parent-reparse");
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
