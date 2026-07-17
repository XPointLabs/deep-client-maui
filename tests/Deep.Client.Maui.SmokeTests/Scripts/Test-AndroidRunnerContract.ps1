$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$sandbox = Join-Path $repoRoot ("artifacts\contract-test-{0}" -f [Guid]::NewGuid().ToString('N'))
$tools = Join-Path $sandbox 'tools'
$evidence = Join-Path $sandbox 'evidence'
New-Item -ItemType Directory -Force -Path $tools, $evidence | Out-Null
try {
    $apk = Join-Path $sandbox 'client test.apk'
    [IO.File]::WriteAllBytes($apk, [byte[]](1, 2, 3))
    $adb = Join-Path $tools 'fake adb.ps1'
    @'
if ($args.Count -eq 1 -and $args[0] -eq 'devices') {
    "List of devices attached"
    "safe-serial device"
    exit 0
}
if ($args.Count -ge 4 -and $args[0] -eq '-s' -and $args[2] -eq 'reverse') { exit 0 }
if ($args.Count -ge 7 -and $args[0] -eq '-s' -and $args[2] -eq 'shell' -and $args[3] -eq 'pm') { exit 0 }
exit 9
'@ | Set-Content -LiteralPath $adb -Encoding utf8
    $aapt = Join-Path $tools 'fake aapt.ps1'
    @'
if ($args.Count -eq 3 -and $args[0] -eq 'dump' -and $args[1] -eq 'badging') {
  "package: name='network.xpoint.deep.e2e' versionCode='42' versionName='1.2.3'"
  exit 0
}
exit 7
'@ | Set-Content -LiteralPath $aapt -Encoding utf8
    $apksigner = Join-Path $tools 'fake apksigner.ps1'
    @'
if ($args.Count -eq 3 -and $args[0] -eq 'verify' -and $args[1] -eq '--print-certs') {
  'Signer #1 certificate SHA-256 digest: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
  exit 0
}
exit 7
'@ | Set-Content -LiteralPath $apksigner -Encoding utf8
    $runner = Join-Path $tools 'fake runner.ps1'
    @'
$values = @{}
for ($i = 0; $i -lt $args.Count; $i += 2) { $values[$args[$i]] = $args[$i + 1] }
if ($values['--serial'] -ne 'safe-serial' -or -not (Test-Path -LiteralPath $values['--apk'])) { exit 8 }
if ($env:DEEP_FAKE_PRIVATE_JUNIT -eq '1') {
  '<testsuite tests="2" failures="0" errors="0" skipped="0"><system-out>password=secret</system-out></testsuite>' |
    Set-Content -LiteralPath $values['--junit'] -Encoding utf8
} else {
  '<testsuite tests="2" failures="0" errors="0" skipped="0" />' |
    Set-Content -LiteralPath $values['--junit'] -Encoding utf8
}
$junitSha = (Get-FileHash -LiteralPath $values['--junit'] -Algorithm SHA256).Hash.ToLowerInvariant()
$apkSha = if ($env:DEEP_FAKE_TAMPER_BINDING -eq '1') { '0' * 64 } else { $values['--apk-sha256'] }
[ordered]@{
  schema = 'deep.survival.android-runner-result.v2'
  sourceCommitSha = $values['--commit']
  releaseInvocationId = $values['--release-invocation']
  laneInvocationId = $values['--lane-invocation']
  apkSha256 = $apkSha
  packageId = $values['--package-id']
  versionCode = $values['--version-code']
  versionName = $values['--version-name']
  signingCertificateSha256 = $values['--signing-cert-sha256']
  runnerSha256 = $values['--runner-sha256']
  runnerVersion = 'fake-runner/1.0'
  junitSha256 = $junitSha
  device = [ordered]@{
    serial = $values['--serial']
    dedicatedManaged = $true
    personalDataAbsent = $true
    productionPackageAbsentBefore = $true
    testPackageClearedBefore = $true
    testPackageRemovedAfter = $true
  }
  status = 'passed'
  counters = [ordered]@{ total = 2; executed = 2; passed = 2; failed = 0; skipped = 0 }
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $values['--result'] -Encoding utf8
exit 0
'@ | Set-Content -LiteralPath $runner -Encoding utf8

    $engine = (Get-Process -Id $PID).Path
    $releaseInvocationId = '11111111111111111111111111111111'
    & $engine -NoLogo -NoProfile -File (Join-Path $repoRoot 'eng\Invoke-StrictClientLane.ps1') -Lane AndroidDevice `
        -ReleaseInvocationId $releaseInvocationId `
        -ApkPath $apk -AndroidSerial 'safe-serial' -AndroidRunner $runner -AdbPath $adb -AaptPath $aapt `
        -ApkSignerPath $apksigner -DedicatedManagedDevice `
        -ArtifactDirectory $evidence
    if ($LASTEXITCODE -ne 0) {
        $diagnostic = Get-Content -LiteralPath (Join-Path $evidence 'preflight-androiddevice.json') -Raw
        throw "The fake Android runner contract was rejected with exit $LASTEXITCODE. $diagnostic"
    }
    $result = Get-Content -LiteralPath (Join-Path $evidence 'result-androiddevice.json') -Raw | ConvertFrom-Json
    if ($result.status -ne 'passed' -or $result.counters.executed -ne 2) {
        throw 'The strict wrapper did not preserve Android runner counters.'
    }

    $result.sourceCommitSha = '0000000000000000000000000000000000000000'
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'result-androiddevice.json') -Encoding utf8
    try {
        & (Join-Path $repoRoot 'eng\Test-StrictClientEvidence.ps1') -ReleaseInvocationId $releaseInvocationId `
            -RequireComplete -ArtifactDirectory $evidence
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
            -RequireComplete -ArtifactDirectory $evidence
    } catch {
    }
    $summary = Get-Content -LiteralPath (Join-Path $evidence 'strict-evidence-summary.json') -Raw | ConvertFrom-Json
    if (($summary.checks | Where-Object lane -eq 'androiddevice').status -ne 'failed') {
        throw 'Stale evidence was not rejected.'
    }

    foreach ($scenario in @('binding', 'privacy')) {
        $scenarioEvidence = Join-Path $sandbox ("evidence-{0}" -f $scenario)
        if ($scenario -eq 'binding') {
            $env:DEEP_FAKE_TAMPER_BINDING = '1'
        } else {
            $env:DEEP_FAKE_PRIVATE_JUNIT = '1'
        }
        & $engine -NoLogo -NoProfile -File (Join-Path $repoRoot 'eng\Invoke-StrictClientLane.ps1') -Lane AndroidDevice `
            -ReleaseInvocationId $releaseInvocationId `
            -ApkPath $apk -AndroidSerial 'safe-serial' -AndroidRunner $runner -AdbPath $adb -AaptPath $aapt `
            -ApkSignerPath $apksigner -DedicatedManagedDevice `
            -ArtifactDirectory $scenarioEvidence
        if ($LASTEXITCODE -ne 4) {
            throw "Android $scenario tampering was not rejected."
        }
        if (Test-Path -LiteralPath (Join-Path $scenarioEvidence 'android-device-summary.json')) {
            throw "Android $scenario rejection emitted an uploadable summary."
        }
        Remove-Item Env:DEEP_FAKE_TAMPER_BINDING -ErrorAction SilentlyContinue
        Remove-Item Env:DEEP_FAKE_PRIVATE_JUNIT -ErrorAction SilentlyContinue
    }
} finally {
    Remove-Item Env:DEEP_FAKE_TAMPER_BINDING -ErrorAction SilentlyContinue
    Remove-Item Env:DEEP_FAKE_PRIVATE_JUNIT -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $sandbox -Recurse -Force
}
