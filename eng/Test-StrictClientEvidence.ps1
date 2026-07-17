[CmdletBinding()]
param(
    [string]$ArtifactDirectory,
    [switch]$RequireComplete,
    [int]$MaximumAgeHours = 24
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'StrictLane.Common.ps1')
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $ArtifactDirectory = Join-Path $repoRoot 'artifacts\survival\I01-MAUI-STRICT-GATES'
}
$ArtifactDirectory = Get-CanonicalContainedPath -Root $repoRoot -Candidate ([IO.Path]::GetFullPath($ArtifactDirectory))
New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null
$commit = (& git -C $repoRoot rev-parse HEAD).Trim()
$sourceTreeClean = @(& git -C $repoRoot status --porcelain).Count -eq 0
$requiredLanes = @('windowsui', 'androiddevice', 'liveinfrastructure')
$checks = [Collections.Generic.List[object]]::new()
$now = [DateTimeOffset]::UtcNow

foreach ($lane in $requiredLanes) {
    $path = Join-Path $ArtifactDirectory ("result-{0}.json" -f $lane)
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $checks.Add([ordered]@{ lane = $lane; status = 'not-run'; detail = 'result missing' })
        continue
    }
    try {
        $result = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $generated = [DateTimeOffset]::Parse(
            [string]$result.generatedAtUtc,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind)
        $fresh = $generated -le $now.AddMinutes(5) -and $generated -ge $now.AddHours(-$MaximumAgeHours)
        $countersValid = $null -ne $result.counters -and
            [int]$result.counters.executed -gt 0 -and
            [int]$result.counters.skipped -eq 0 -and
            [int]$result.counters.failed -eq 0
        $passed = $result.schema -eq 'deep.survival.strict-lane-result.v1' -and
            $result.sourceCommitSha -eq $commit -and
            $result.status -eq 'passed' -and
            $fresh -and
            $countersValid
        $checks.Add([ordered]@{
            lane = $lane
            status = $(if ($passed) { 'passed' } else { 'failed' })
            detail = "reportedStatus=$($result.status); commitBound=$($result.sourceCommitSha -eq $commit); fresh=$fresh; countersValid=$countersValid"
        })
    } catch {
        $checks.Add([ordered]@{ lane = $lane; status = 'failed'; detail = 'invalid result JSON' })
    }
}

$payloadManifestPath = Join-Path $ArtifactDirectory 'windows-payload-manifest.json'
$payloadPassed = $false
if (Test-Path -LiteralPath $payloadManifestPath -PathType Leaf) {
    try {
        $manifest = Get-Content -LiteralPath $payloadManifestPath -Raw | ConvertFrom-Json
        $payloadRoot = Get-CanonicalContainedPath -Root $repoRoot -Candidate (
            [IO.Path]::GetFullPath((Join-Path $repoRoot ([string]$manifest.payloadRoot))))
        $manifestPaths = @($manifest.files.relativePath)
        $actualFiles = @(Get-ChildItem -LiteralPath $payloadRoot -File -Recurse)
        $payloadPassed = $manifest.sourceCommitSha -eq $commit -and
            $manifestPaths.Count -eq $actualFiles.Count
        if ($payloadPassed) {
            foreach ($entry in $manifest.files) {
                $candidate = Get-CanonicalContainedPath -Root $payloadRoot -Candidate (
                    [IO.Path]::GetFullPath((Join-Path $payloadRoot ([string]$entry.relativePath))))
                if (-not (Test-Path -LiteralPath $candidate -PathType Leaf) -or
                    (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256 -or
                    (Get-Item -LiteralPath $candidate).Length -ne [long]$entry.length) {
                    $payloadPassed = $false
                    break
                }
            }
        }
    } catch {
        $payloadPassed = $false
    }
}
$checks.Add([ordered]@{
    lane = 'windows-payload'
    status = $(if ($payloadPassed) { 'passed' } elseif (Test-Path -LiteralPath $payloadManifestPath) { 'failed' } else { 'not-run' })
    detail = 'full payload manifest commit and hashes'
})
$checks.Add([ordered]@{
    lane = 'source-tree'
    status = $(if ($sourceTreeClean) { 'passed' } else { 'failed' })
    detail = 'strict release evidence requires an exact clean commit'
})

$incomplete = @($checks | Where-Object { $_.status -ne 'passed' })
$attemptedFailure = @($checks | Where-Object { $_.status -eq 'failed' }).Count -gt 0
$overall = if ($incomplete.Count -eq 0) {
    'passed'
} elseif ($RequireComplete -or $attemptedFailure) {
    'blocked'
} else {
    'not-run'
}
[ordered]@{
    schema = 'deep.survival.strict-evidence-summary.v1'
    sourceCommitSha = $commit
    generatedAtUtc = $now.ToString('O')
    status = $overall
    blockers = @($incomplete | ForEach-Object { $_.lane })
    checks = $checks
} | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'strict-evidence-summary.json') -Encoding utf8

if ($RequireComplete -and $incomplete.Count -gt 0) {
    throw "Strict release evidence is incomplete or invalid: $($incomplete.lane -join ', ')."
}
