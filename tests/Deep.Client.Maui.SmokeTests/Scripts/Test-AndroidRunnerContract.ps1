$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$sandbox = Join-Path $repoRoot ("artifacts\contract-test-{0}" -f [Guid]::NewGuid().ToString('N'))
$tools = Join-Path $sandbox 'tools'
$evidence = Join-Path $sandbox 'evidence'
New-Item -ItemType Directory -Force -Path $tools, $evidence | Out-Null
$apkStream = $null
$apkArchive = $null
$protectedSandbox = $null
try {
    $apk = Join-Path $sandbox 'client test.apk'
    $apkStream = [IO.File]::Create($apk)
    $apkArchive = [IO.Compression.ZipArchive]::new(
        $apkStream,
        [IO.Compression.ZipArchiveMode]::Create,
        $false)
    foreach ($entryName in @('AndroidManifest.xml', 'classes.dex')) {
        $entry = $apkArchive.CreateEntry($entryName)
        $entryStream = $entry.Open()
        $bytes = [Text.Encoding]::UTF8.GetBytes("synthetic-$entryName")
        $entryStream.Write($bytes, 0, $bytes.Length)
        $entryStream.Dispose()
    }
    $apkArchive.Dispose()
    $apkStream.Dispose()
    $fixtureSource = @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

public static class SyntheticAndroidTool
{
    private static string Q(string value)
    {
        return "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static string Sha256(string path)
    {
        using (var stream = File.OpenRead(path))
        using (var hash = SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    public static int Main(string[] args)
    {
        var name = Path.GetFileNameWithoutExtension(
            Process.GetCurrentProcess().MainModule.FileName).ToLowerInvariant();
        if (name.Contains("runner"))
        {
            if (args.Length == 1 && args[0] == "--version")
            {
                Console.WriteLine("Synthetic Runner 1.0");
                return 0;
            }
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i + 1 < args.Length; i += 2)
                values[args[i]] = args[i + 1];
            if (!values.ContainsKey("--serial") || values["--serial"] != "safe-serial" ||
                !values.ContainsKey("--apk") || !File.Exists(values["--apk"]))
                return 8;

            var privacyMode = Environment.GetEnvironmentVariable("DEEP_FAKE_PRIVATE_JUNIT") ?? "";
            var junit = "<testsuite tests=\"2\" failures=\"0\" errors=\"0\" skipped=\"0\" />";
            if (privacyMode == "system-output")
                junit = "<testsuite tests=\"2\" failures=\"0\" errors=\"0\" skipped=\"0\"><system-out>password=secret</system-out></testsuite>";
            else if (privacyMode == "absolute-path")
                junit = "<testsuite tests=\"2\" failures=\"0\" errors=\"0\" skipped=\"0\"><testcase name=\"safe\" file=\"/tmp/private.log\" /></testsuite>";
            else if (privacyMode == "sensitive-property")
                junit = "<testsuite tests=\"2\" failures=\"0\" errors=\"0\" skipped=\"0\"><properties><property name=\"token\" value=\"private\" /></properties></testsuite>";
            else if (privacyMode == "windows-path")
                junit = "<testsuite tests=\"2\" failures=\"0\" errors=\"0\" skipped=\"0\"><testcase name=\"safe\" file=\"C:\\Users\\Private\\result.xml\" /></testsuite>";
            else if (privacyMode == "absolute-uri")
                junit = "<testsuite tests=\"2\" failures=\"0\" errors=\"0\" skipped=\"0\"><testcase name=\"https://private.example/result\" /></testsuite>";
            else if (privacyMode == "attachment")
                junit = "<testsuite tests=\"2\" failures=\"0\" errors=\"0\" skipped=\"0\"><attachment path=\"result.bin\" /></testsuite>";
            else if (privacyMode == "embedded-uri")
                junit = "<testsuite tests=\"2\" failures=\"0\" errors=\"0\" skipped=\"0\"><testcase name=\"prefix https://private.example/result suffix\" /></testsuite>";
            else if (privacyMode == "sensitive-attribute")
                junit = "<testsuite tests=\"2\" failures=\"0\" errors=\"0\" skipped=\"0\"><testcase name=\"safe\" token=\"abc\" /></testsuite>";
            else if (privacyMode == "attachment-attribute")
                junit = "<testsuite tests=\"2\" failures=\"0\" errors=\"0\" skipped=\"0\"><testcase name=\"safe\" attachment=\"result.bin\" /></testsuite>";
            else if (privacyMode == "unix-root")
                junit = "<testsuite tests=\"2\" failures=\"0\" errors=\"0\" skipped=\"0\"><testcase name=\"safe\" file=\"/\" /></testsuite>";
            else if (privacyMode == "system-error")
                junit = "<testsuite tests=\"2\" failures=\"0\" errors=\"0\" skipped=\"0\"><system-err>private</system-err></testsuite>";
            else if (privacyMode == "sensitive-allowed-value")
                junit = "<testsuite tests=\"2\" failures=\"0\" errors=\"0\" skipped=\"0\"><testcase name=\"password=secret\" /></testsuite>";
            else if (privacyMode == "artifact-allowed-value")
                junit = "<testsuite tests=\"2\" failures=\"0\" errors=\"0\" skipped=\"0\"><testcase name=\"artifact=result.bin\" /></testsuite>";
            else if (privacyMode == "single-letter-uri")
                junit = "<testsuite tests=\"2\" failures=\"0\" errors=\"0\" skipped=\"0\"><testcase name=\"a:private\" /></testsuite>";
            File.WriteAllText(values["--junit"], junit, new UTF8Encoding(true));

            var tamper = Environment.GetEnvironmentVariable("DEEP_FAKE_TAMPER_BINDING") ?? "";
            var apkSha = tamper == "apk" ? new string('0', 64) : values["--apk-sha256"];
            var certSha = tamper == "cert" ? new string('0', 64) : values["--signing-cert-sha256"];
            var serial = tamper == "serial" ? "wrong-serial" : values["--serial"];
            var runnerSha = tamper == "runner" ? new string('0', 64) : values["--runner-sha256"];
            var junitSha = tamper == "junit" ? new string('0', 64) : Sha256(values["--junit"]);
            var runnerVersion = tamper == "runner-version" ? @"C:\private\runner.exe" : values["--runner-version"];
            var json = "{" +
                "\"schema\":\"deep.survival.android-runner-result.v3\"," +
                "\"sourceCommitSha\":" + Q(values["--commit"]) + "," +
                "\"releaseInvocationId\":" + Q(values["--release-invocation"]) + "," +
                "\"laneInvocationId\":" + Q(values["--lane-invocation"]) + "," +
                "\"labPolicyId\":" + Q(values["--lab-policy-id"]) + "," +
                "\"labPolicySha256\":" + Q(values["--lab-policy-sha256"]) + "," +
                "\"apkSha256\":" + Q(apkSha) + "," +
                "\"packageId\":" + Q(values["--package-id"]) + "," +
                "\"versionCode\":" + Q(values["--version-code"]) + "," +
                "\"versionName\":" + Q(values["--version-name"]) + "," +
                "\"signingCertificateSha256\":" + Q(certSha) + "," +
                "\"runnerSha256\":" + Q(runnerSha) + "," +
                "\"runnerVersion\":" + Q(runnerVersion) + "," +
                "\"junitSha256\":" + Q(junitSha) + "," +
                "\"device\":{" +
                    "\"serial\":" + Q(serial) + "," +
                    "\"fingerprintSha256\":" + Q(values["--device-fingerprint-sha256"]) + "," +
                    "\"productSha256\":" + Q(values["--device-product-sha256"]) + "," +
                    "\"hardwareSha256\":" + Q(values["--device-hardware-sha256"]) + "," +
                    "\"modelSha256\":" + Q(values["--device-model-sha256"]) + "," +
                    "\"kernelQemu\":" + Q(values["--device-kernel-qemu"]) + "," +
                    "\"sdk\":" + values["--device-sdk"] + "," +
                    "\"class\":" + Q(values["--device-class"]) + "," +
                    "\"dedicatedManaged\":true,\"personalDataAbsent\":true," +
                    "\"productionPackageAbsentBefore\":true,\"testPackageClearedBefore\":true," +
                    "\"testPackageRemovedAfter\":true}," +
                "\"status\":\"passed\"," +
                "\"counters\":{\"total\":2,\"executed\":2,\"passed\":2,\"failed\":0,\"skipped\":0}" +
                "}";
            File.WriteAllText(values["--result"], json, new UTF8Encoding(true));
            return 0;
        }

        if (name.Contains("adb"))
        {
            if (args.Length == 1 && args[0] == "version") { Console.WriteLine("Synthetic ADB 1.0"); return 0; }
            if (args.Length == 1 && args[0] == "devices") { Console.WriteLine("List of devices attached"); Console.WriteLine("safe-serial device"); return 0; }
            if (args.Length == 3 && args[0] == "-s" && args[2] == "get-serialno") { Console.WriteLine("safe-serial"); return 0; }
            if (args.Length >= 5 && args[0] == "-s" && args[2] == "shell" && args[3] == "getprop")
            {
                if (args[4] == "ro.build.fingerprint") Console.WriteLine("synthetic/fingerprint/value");
                else if (args[4] == "ro.product.name") Console.WriteLine("synthetic_product");
                else if (args[4] == "ro.build.version.sdk") Console.WriteLine("35");
                else if (args[4] == "ro.build.characteristics") Console.WriteLine("emulator");
                else if (args[4] == "ro.hardware") Console.WriteLine("ranchu");
                else if (args[4] == "ro.product.model") Console.WriteLine("sdk_gphone64_x86_64");
                else if (args[4] == "ro.kernel.qemu") Console.WriteLine("1");
                else return 9;
                return 0;
            }
            if (args.Length >= 4 && args[0] == "-s" && args[2] == "reverse") return 0;
            if (args.Length >= 7 && args[0] == "-s" && args[2] == "shell" && args[3] == "pm") return 0;
            return 9;
        }

        if (name.Contains("aapt") && !name.Contains("signer"))
        {
            if (args.Length == 1 && args[0] == "version") { Console.WriteLine("Synthetic AAPT 1.0"); return 0; }
            if (args.Length == 3 && args[0] == "dump" && args[1] == "badging")
            {
                Console.WriteLine("package: name='network.xpoint.deep.e2e' versionCode='42' versionName='1.2.3'");
                return 0;
            }
            return 7;
        }

        if (name.Contains("apksigner"))
        {
            if (args.Length == 1 && args[0] == "version") { Console.WriteLine("Synthetic APK Signer 1.0"); return 0; }
            if (args.Length == 3 && args[0] == "verify" && args[1] == "--print-certs")
            {
                Console.WriteLine("Signer #1 certificate SHA-256 digest: " + new string('a', 64));
                return 0;
            }
        }
        return 7;
    }
}
'@
    $compiledFixture = Join-Path $tools 'synthetic-android-tool.exe'
    $fixtureSourcePath = Join-Path $tools 'synthetic-android-tool.cs'
    [IO.File]::WriteAllText(
        $fixtureSourcePath,
        $fixtureSource,
        [Text.UTF8Encoding]::new($false))
    $compilerCandidates = @(
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'))
    $compiler = $compilerCandidates |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($compiler)) {
        throw 'The Windows .NET Framework C# compiler was not found.'
    }
    & $compiler /nologo /target:exe "/out:$compiledFixture" $fixtureSourcePath
    if ($LASTEXITCODE -ne 0 -or
        -not (Test-Path -LiteralPath $compiledFixture -PathType Leaf)) {
        throw 'The synthetic Android tool fixture did not compile.'
    }
    $runner = Join-Path $tools 'fake runner.exe'
    $adb = Join-Path $tools 'fake adb.exe'
    $aapt = Join-Path $tools 'fake aapt.exe'
    $apksigner = Join-Path $tools 'fake apksigner.exe'
    Copy-Item -LiteralPath $compiledFixture -Destination $runner
    Copy-Item -LiteralPath $compiledFixture -Destination $adb
    Copy-Item -LiteralPath $compiledFixture -Destination $aapt
    Copy-Item -LiteralPath $compiledFixture -Destination $apksigner

    $runnerVersionProbe = @(& $runner --version)
    if ($LASTEXITCODE -ne 0 -or $runnerVersionProbe.Count -ne 1 -or
        $runnerVersionProbe[0] -cne 'Synthetic Runner 1.0') {
        throw 'The synthetic runner version fixture is not executable.'
    }

    $engine = (Get-Process -Id $PID).Path
    $releaseInvocationId = '11111111111111111111111111111111'
    $commit = (& git -C $repoRoot rev-parse HEAD).Trim()
    $relative = {
        param([string]$Path)
        return ([IO.Path]::GetFullPath($Path).Substring($repoRoot.Length + 1)).Replace('\', '/')
    }
    $policyPath = Join-Path $sandbox 'synthetic-lab-policy.json'
    $policy = [ordered]@{
        schema = 'deep.survival.android-lab-policy.v1'
        provisioned = $true
        synthetic = $true
        sourceCommitSha = $commit
        policyId = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
        approval = [ordered]@{
            state = 'synthetic-contract-fixture'
            approvedBy = 'Synthetic Contract Fixture'
            receiptRelativePath = ''
            receiptSha256 = '0' * 64
        }
        tools = [ordered]@{
            runner = [ordered]@{
                relativePath = & $relative $runner
                sha256 = (Get-FileHash -LiteralPath $runner -Algorithm SHA256).Hash.ToLowerInvariant()
                version = 'Synthetic Runner 1.0'
            }
            adb = [ordered]@{
                relativePath = & $relative $adb
                sha256 = (Get-FileHash -LiteralPath $adb -Algorithm SHA256).Hash.ToLowerInvariant()
                version = 'Synthetic ADB 1.0'
            }
            aapt = [ordered]@{
                kind = 'aapt'
                relativePath = & $relative $aapt
                sha256 = (Get-FileHash -LiteralPath $aapt -Algorithm SHA256).Hash.ToLowerInvariant()
                version = 'Synthetic AAPT 1.0'
            }
            apksigner = [ordered]@{
                relativePath = & $relative $apksigner
                sha256 = (Get-FileHash -LiteralPath $apksigner -Algorithm SHA256).Hash.ToLowerInvariant()
                version = 'Synthetic APK Signer 1.0'
            }
        }
        device = [ordered]@{
            serial = 'safe-serial'
            fingerprint = 'synthetic/fingerprint/value'
            product = 'synthetic_product'
            hardware = 'ranchu'
            model = 'sdk_gphone64_x86_64'
            kernelQemu = '1'
            sdk = 35
            class = 'managed-emulator'
            dedicated = $true
            inventoryState = 'synthetic-contract-fixture'
            inventoryApprovedBy = 'Synthetic Contract Fixture'
            inventoryApprovalReceiptSha256 = '0' * 64
        }
        application = [ordered]@{
            packageId = 'network.xpoint.deep.e2e'
            versionCode = 42
            versionName = '1.2.3'
            apkSha256 = (Get-FileHash -LiteralPath $apk -Algorithm SHA256).Hash.ToLowerInvariant()
            signingCertificateSha256 = 'a' * 64
        }
    }
    $policy | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $policyPath -Encoding utf8

    $protectedSandbox = Join-Path $repoRoot (
        ".secrets\android-lab\contract-test-{0}" -f [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $protectedSandbox | Out-Null
    $receiptPath = Join-Path $protectedSandbox 'mr-x-approval.receipt'
    [IO.File]::WriteAllText($receiptPath, 'synthetic negative physical-policy approval fixture')
    $realEmulatorPolicy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
    $realEmulatorPolicy.synthetic = $false
    $realEmulatorPolicy.approval.state = 'approved'
    $realEmulatorPolicy.approval.approvedBy = 'Mr. X'
    $realEmulatorPolicy.approval.receiptRelativePath = & $relative $receiptPath
    $realEmulatorPolicy.approval.receiptSha256 = (
        Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $realEmulatorPolicy.device.inventoryState = 'approved'
    $realEmulatorPolicy.device.inventoryApprovedBy = 'Mr. X'
    $realEmulatorPolicy.device.inventoryApprovalReceiptSha256 =
        $realEmulatorPolicy.approval.receiptSha256
    $realEmulatorPolicyPath = Join-Path $protectedSandbox 'emulator-policy.json'
    $realEmulatorPolicy | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $realEmulatorPolicyPath -Encoding utf8
    $realEmulatorEvidence = Join-Path $sandbox 'evidence-real-emulator-policy'
    & $engine -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'eng\Invoke-StrictClientLane.ps1') -Lane AndroidDevice `
        -ReleaseInvocationId $releaseInvocationId -ApkPath $apk `
        -AndroidLabPolicyPath $realEmulatorPolicyPath -ArtifactDirectory $realEmulatorEvidence
    if ($LASTEXITCODE -ne 2) {
        throw 'A non-synthetic managed-emulator policy did not block the physical release lane.'
    }
    $realEmulatorPreflight = Get-Content -LiteralPath (
        Join-Path $realEmulatorEvidence 'preflight-androiddevice.json') -Raw | ConvertFrom-Json
    if (($realEmulatorPreflight.checks |
            Where-Object name -eq 'android-lab-policy').status -ne 'blocked') {
        throw 'A non-synthetic managed-emulator policy passed protected policy validation.'
    }

    $templateEvidence = Join-Path $sandbox 'evidence-template'
    & $engine -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'eng\Invoke-StrictClientLane.ps1') -Lane AndroidDevice `
        -ReleaseInvocationId $releaseInvocationId -ApkPath $apk `
        -AndroidLabPolicyPath (Join-Path $repoRoot 'eng\policies\android-lab-policy.template.json') `
        -ArtifactDirectory $templateEvidence
    if ($LASTEXITCODE -ne 2) {
        throw 'The unprovisioned checked-in Android policy template did not block the physical lane.'
    }

    $syntheticDefaultEvidence = Join-Path $sandbox 'evidence-synthetic-default'
    & $engine -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'eng\Invoke-StrictClientLane.ps1') -Lane AndroidDevice `
        -ReleaseInvocationId $releaseInvocationId -ApkPath $apk -AndroidLabPolicyPath $policyPath `
        -ArtifactDirectory $syntheticDefaultEvidence
    if ($LASTEXITCODE -ne 2) {
        throw 'Synthetic policy unexpectedly satisfied the default physical lane.'
    }

    $invalidApk = Join-Path $sandbox 'invalid.apk'
    [IO.File]::WriteAllBytes($invalidApk, [byte[]](1, 2, 3))
    $invalidPolicy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
    $invalidPolicy.application.apkSha256 = (Get-FileHash -LiteralPath $invalidApk -Algorithm SHA256).Hash.ToLowerInvariant()
    $invalidPolicyPath = Join-Path $sandbox 'synthetic-invalid-apk-policy.json'
    $invalidPolicy | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $invalidPolicyPath -Encoding utf8
    $invalidEvidence = Join-Path $sandbox 'evidence-invalid-apk'
    & $engine -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'eng\Invoke-StrictClientLane.ps1') -Lane AndroidDevice `
        -ReleaseInvocationId $releaseInvocationId -ApkPath $invalidApk `
        -AndroidSerial 'safe-serial' -AndroidRunner $runner -AdbPath $adb -AaptPath $aapt `
        -ApkSignerPath $apksigner -AndroidLabPolicyPath $invalidPolicyPath -AllowSyntheticLabPolicyForContractTests `
        -ArtifactDirectory $invalidEvidence
    if ($LASTEXITCODE -ne 2) {
        throw 'A hash-bound three-byte non-ZIP APK was not blocked before runner execution.'
    }

    & $engine -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'eng\Invoke-StrictClientLane.ps1') -Lane AndroidDevice `
        -ReleaseInvocationId $releaseInvocationId `
        -ApkPath $apk -AndroidSerial 'safe-serial' -AndroidRunner $runner -AdbPath $adb -AaptPath $aapt `
        -ApkSignerPath $apksigner -AndroidLabPolicyPath $policyPath -AllowSyntheticLabPolicyForContractTests `
        -ArtifactDirectory $evidence
    if ($LASTEXITCODE -ne 0) {
        $diagnostic = Get-Content -LiteralPath (Join-Path $evidence 'preflight-androiddevice.json') -Raw
        throw "The fake Android runner contract was rejected with exit $LASTEXITCODE. $diagnostic"
    }
    $result = Get-Content -LiteralPath (Join-Path $evidence 'result-androiddevice.json') -Raw | ConvertFrom-Json
    if ($result.status -ne 'passed' -or $result.counters.executed -ne 2) {
        throw 'The strict wrapper did not preserve Android runner counters.'
    }
    try {
        & (Join-Path $repoRoot 'eng\Test-StrictClientEvidence.ps1') -ReleaseInvocationId $releaseInvocationId `
            -AndroidLabPolicyPath $policyPath -AndroidApkPath $apk `
            -RequireComplete -ArtifactDirectory $evidence
    } catch {
    }
    $syntheticSummary = Get-Content -LiteralPath (Join-Path $evidence 'strict-evidence-summary.json') -Raw | ConvertFrom-Json
    if (($syntheticSummary.checks | Where-Object lane -eq 'android-sanitized-summary').status -ne 'failed') {
        throw 'Synthetic fixture policy unexpectedly satisfied release evidence validation.'
    }

    $result.sourceCommitSha = '0000000000000000000000000000000000000000'
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'result-androiddevice.json') -Encoding utf8
    try {
        & (Join-Path $repoRoot 'eng\Test-StrictClientEvidence.ps1') -ReleaseInvocationId $releaseInvocationId `
            -AndroidLabPolicyPath $policyPath -AndroidApkPath $apk -RequireComplete -ArtifactDirectory $evidence
    } catch {
    }
    $summary = Get-Content -LiteralPath (Join-Path $evidence 'strict-evidence-summary.json') -Raw | ConvertFrom-Json
    if (($summary.checks | Where-Object lane -eq 'androiddevice').status -ne 'failed') {
        throw 'Commit tampering was not rejected.'
    }

    $result.sourceCommitSha = (& git -C $repoRoot rev-parse HEAD).Trim()
    $result.generatedAtUtc = [DateTimeOffset]::UtcNow.AddDays(-2).ToString('O')
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'result-androiddevice.json') -Encoding utf8
    try {
        & (Join-Path $repoRoot 'eng\Test-StrictClientEvidence.ps1') -ReleaseInvocationId $releaseInvocationId `
            -AndroidLabPolicyPath $policyPath -AndroidApkPath $apk -RequireComplete -ArtifactDirectory $evidence
    } catch {
    }
    $summary = Get-Content -LiteralPath (Join-Path $evidence 'strict-evidence-summary.json') -Raw | ConvertFrom-Json
    if (($summary.checks | Where-Object lane -eq 'androiddevice').status -ne 'failed') {
        throw 'Stale evidence was not rejected.'
    }

    $scenarios = @(
        [pscustomobject]@{ name = 'apk-binding'; binding = 'apk'; privacy = '' },
        [pscustomobject]@{ name = 'certificate-binding'; binding = 'cert'; privacy = '' },
        [pscustomobject]@{ name = 'serial-binding'; binding = 'serial'; privacy = '' },
        [pscustomobject]@{ name = 'runner-binding'; binding = 'runner'; privacy = '' },
        [pscustomobject]@{ name = 'junit-binding'; binding = 'junit'; privacy = '' },
        [pscustomobject]@{ name = 'runner-version-privacy'; binding = 'runner-version'; privacy = '' },
        [pscustomobject]@{ name = 'system-output-privacy'; binding = ''; privacy = 'system-output' },
        [pscustomobject]@{ name = 'absolute-path-privacy'; binding = ''; privacy = 'absolute-path' },
        [pscustomobject]@{ name = 'sensitive-property-privacy'; binding = ''; privacy = 'sensitive-property' },
        [pscustomobject]@{ name = 'windows-path-privacy'; binding = ''; privacy = 'windows-path' },
        [pscustomobject]@{ name = 'absolute-uri-privacy'; binding = ''; privacy = 'absolute-uri' },
        [pscustomobject]@{ name = 'attachment-privacy'; binding = ''; privacy = 'attachment' },
        [pscustomobject]@{ name = 'embedded-uri-privacy'; binding = ''; privacy = 'embedded-uri' },
        [pscustomobject]@{ name = 'sensitive-attribute-privacy'; binding = ''; privacy = 'sensitive-attribute' },
        [pscustomobject]@{ name = 'attachment-attribute-privacy'; binding = ''; privacy = 'attachment-attribute' },
        [pscustomobject]@{ name = 'unix-root-privacy'; binding = ''; privacy = 'unix-root' },
        [pscustomobject]@{ name = 'system-error-privacy'; binding = ''; privacy = 'system-error' },
        [pscustomobject]@{ name = 'sensitive-allowed-value-privacy'; binding = ''; privacy = 'sensitive-allowed-value' },
        [pscustomobject]@{ name = 'artifact-allowed-value-privacy'; binding = ''; privacy = 'artifact-allowed-value' },
        [pscustomobject]@{ name = 'single-letter-uri-privacy'; binding = ''; privacy = 'single-letter-uri' }
    )
    $scenarioIndex = 0
    foreach ($scenario in $scenarios) {
        # Keep the synthetic artifact root below legacy Windows MAX_PATH. The
        # descriptive scenario name remains in assertions, not in filesystem
        # paths that the generated .NET Framework fixture must open.
        $scenarioEvidence = Join-Path $sandbox ("e-{0:D2}" -f $scenarioIndex)
        $scenarioIndex++
        $env:DEEP_FAKE_TAMPER_BINDING = $scenario.binding
        $env:DEEP_FAKE_PRIVATE_JUNIT = $scenario.privacy
        & $engine -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'eng\Invoke-StrictClientLane.ps1') -Lane AndroidDevice `
            -ReleaseInvocationId $releaseInvocationId `
            -ApkPath $apk -AndroidSerial 'safe-serial' -AndroidRunner $runner -AdbPath $adb -AaptPath $aapt `
            -ApkSignerPath $apksigner -AndroidLabPolicyPath $policyPath -AllowSyntheticLabPolicyForContractTests `
            -ArtifactDirectory $scenarioEvidence
        if ($LASTEXITCODE -ne 4) {
            throw "Android $($scenario.name) tampering was not rejected."
        }
        if (Test-Path -LiteralPath (Join-Path $scenarioEvidence 'android-device-summary.json')) {
            throw "Android $($scenario.name) rejection emitted an uploadable summary."
        }
        Remove-Item Env:DEEP_FAKE_TAMPER_BINDING -ErrorAction SilentlyContinue
        Remove-Item Env:DEEP_FAKE_PRIVATE_JUNIT -ErrorAction SilentlyContinue
    }
} finally {
    if ($null -ne $apkArchive) {
        $apkArchive.Dispose()
    }
    if ($null -ne $apkStream) {
        $apkStream.Dispose()
    }
    if (-not [string]::IsNullOrWhiteSpace($protectedSandbox) -and
        (Test-Path -LiteralPath $protectedSandbox)) {
        $canonicalProtectedSandbox = [IO.Path]::GetFullPath($protectedSandbox)
        $expectedProtectedPrefix = [IO.Path]::GetFullPath((Join-Path $repoRoot '.secrets\android-lab\contract-test-'))
        if (-not $canonicalProtectedSandbox.StartsWith($expectedProtectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to clean an unexpected protected contract-test path.'
        }
        Remove-Item -LiteralPath $canonicalProtectedSandbox -Recurse -Force
    }
    Remove-Item Env:DEEP_FAKE_TAMPER_BINDING -ErrorAction SilentlyContinue
    Remove-Item Env:DEEP_FAKE_PRIVATE_JUNIT -ErrorAction SilentlyContinue
    for ($attempt = 0; $attempt -lt 5 -and (Test-Path -LiteralPath $sandbox); $attempt++) {
        try {
            Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction Stop
        } catch {
            Start-Sleep -Milliseconds 100
        }
    }
}
