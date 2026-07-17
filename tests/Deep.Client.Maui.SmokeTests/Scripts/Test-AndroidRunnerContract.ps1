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
exit 9
'@ | Set-Content -LiteralPath $adb -Encoding utf8
    $runner = Join-Path $tools 'fake runner.ps1'
    @'
$values = @{}
for ($i = 0; $i -lt $args.Count; $i += 2) { $values[$args[$i]] = $args[$i + 1] }
if ($values['--serial'] -ne 'safe-serial' -or -not (Test-Path -LiteralPath $values['--apk'])) { exit 8 }
[ordered]@{
  schema = 'deep.survival.android-runner-result.v1'
  sourceCommitSha = $values['--commit']
  status = 'passed'
  counters = [ordered]@{ total = 2; executed = 2; passed = 2; failed = 0; skipped = 0 }
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $values['--result'] -Encoding utf8
'<testsuite tests="2" failures="0" skipped="0" />' | Set-Content -LiteralPath $values['--junit'] -Encoding utf8
exit 0
'@ | Set-Content -LiteralPath $runner -Encoding utf8

    $engine = (Get-Process -Id $PID).Path
    & $engine -NoLogo -NoProfile -File (Join-Path $repoRoot 'eng\Invoke-StrictClientLane.ps1') -Lane AndroidDevice `
        -ApkPath $apk -AndroidSerial 'safe-serial' -AndroidRunner $runner -AdbPath $adb `
        -ArtifactDirectory $evidence
    if ($LASTEXITCODE -ne 0) {
        throw "The fake Android runner contract was rejected with exit $LASTEXITCODE."
    }
    $result = Get-Content -LiteralPath (Join-Path $evidence 'result-androiddevice.json') -Raw | ConvertFrom-Json
    if ($result.status -ne 'passed' -or $result.counters.executed -ne 2) {
        throw 'The strict wrapper did not preserve Android runner counters.'
    }

    $result.sourceCommitSha = '0000000000000000000000000000000000000000'
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'result-androiddevice.json') -Encoding utf8
    try {
        & (Join-Path $repoRoot 'eng\Test-StrictClientEvidence.ps1') -RequireComplete -ArtifactDirectory $evidence
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
        & (Join-Path $repoRoot 'eng\Test-StrictClientEvidence.ps1') -RequireComplete -ArtifactDirectory $evidence
    } catch {
    }
    $summary = Get-Content -LiteralPath (Join-Path $evidence 'strict-evidence-summary.json') -Raw | ConvertFrom-Json
    if (($summary.checks | Where-Object lane -eq 'androiddevice').status -ne 'failed') {
        throw 'Stale evidence was not rejected.'
    }
} finally {
    Remove-Item -LiteralPath $sandbox -Recurse -Force
}
