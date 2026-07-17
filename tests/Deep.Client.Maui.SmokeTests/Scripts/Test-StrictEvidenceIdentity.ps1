$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$sandbox = Join-Path $repoRoot ("artifacts\evidence-identity-{0}" -f [Guid]::NewGuid().ToString('N'))
$releaseInvocationId = '22222222222222222222222222222222'
$laneInvocationId = '33333333333333333333333333333333'
$commit = (& git -C $repoRoot rev-parse HEAD).Trim()

function New-Result {
    param([string]$Lane, [string]$LaneInvocation = $laneInvocationId)
    return [ordered]@{
        schema = 'deep.survival.strict-lane-result.v1'
        sourceCommitSha = $commit
        releaseInvocationId = $releaseInvocationId
        laneInvocationId = $LaneInvocation
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        lane = $Lane
        status = 'passed'
        counters = [ordered]@{ total = 1; executed = 1; passed = 1; failed = 0; skipped = 0 }
        evidence = ''
        evidenceSha256 = ''
    }
}

function New-Preflight {
    param([string]$Lane, [string]$LaneInvocation = $laneInvocationId)
    return [ordered]@{
        schema = 'deep.survival.strict-preflight.v1'
        sourceCommitSha = $commit
        releaseInvocationId = $releaseInvocationId
        laneInvocationId = $LaneInvocation
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        lane = $Lane
        status = 'passed'
        checks = @([ordered]@{ name = 'fixture'; status = 'passed'; detail = 'synthetic contract fixture' })
    }
}

function Write-Json {
    param([string]$Path, [object]$Value)
    $Value | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $Path -Encoding utf8
}

function Invoke-Validator {
    param([string]$Directory)
    & (Join-Path $repoRoot 'eng\Test-StrictClientEvidence.ps1') `
        -ReleaseInvocationId $releaseInvocationId `
        -ArtifactDirectory $Directory
    return Get-Content -LiteralPath (Join-Path $Directory 'strict-evidence-summary.json') -Raw | ConvertFrom-Json
}

try {
    New-Item -ItemType Directory -Force -Path $sandbox | Out-Null

    # A byte-for-byte Android-to-Windows copy must not acquire Windows authority.
    Write-Json (Join-Path $sandbox 'result-windowsui.json') (New-Result 'AndroidDevice')
    Write-Json (Join-Path $sandbox 'preflight-windowsui.json') (New-Preflight 'AndroidDevice')
    $summary = Invoke-Validator $sandbox
    if (($summary.checks | Where-Object lane -eq 'windowsui').status -ne 'failed') {
        throw 'Cross-lane evidence copy was not rejected.'
    }

    # Missing final preflight is never a completed lane.
    Remove-Item -LiteralPath (Join-Path $sandbox 'preflight-windowsui.json')
    $summary = Invoke-Validator $sandbox
    if (($summary.checks | Where-Object lane -eq 'windowsui').status -ne 'not-run') {
        throw 'Missing final preflight was not rejected.'
    }

    # A mismatched common invocation must fail even when counters pass.
    $androidResult = New-Result 'AndroidDevice'
    $androidPreflight = New-Preflight 'AndroidDevice'
    $androidPreflight.releaseInvocationId = '44444444444444444444444444444444'
    Write-Json (Join-Path $sandbox 'result-androiddevice.json') $androidResult
    Write-Json (Join-Path $sandbox 'preflight-androiddevice.json') $androidPreflight
    $summary = Invoke-Validator $sandbox
    if (($summary.checks | Where-Object lane -eq 'androiddevice').status -ne 'failed') {
        throw 'Release invocation mismatch was not rejected.'
    }

    # Two lanes may not reuse a lane-scoped invocation identity.
    $androidPreflight.releaseInvocationId = $releaseInvocationId
    Write-Json (Join-Path $sandbox 'preflight-androiddevice.json') $androidPreflight
    Write-Json (Join-Path $sandbox 'result-liveinfrastructure.json') (New-Result 'LiveInfrastructure')
    Write-Json (Join-Path $sandbox 'preflight-liveinfrastructure.json') (New-Preflight 'LiveInfrastructure')
    $summary = Invoke-Validator $sandbox
    if (($summary.checks | Where-Object lane -eq 'androiddevice').status -ne 'failed' -or
        ($summary.checks | Where-Object lane -eq 'liveinfrastructure').status -ne 'failed') {
        throw "Duplicate lane invocation identity was not rejected. $($summary | ConvertTo-Json -Depth 7)"
    }

    if ($summary.productionReady -ne $false) {
        throw 'Incomplete/tampered evidence was incorrectly marked production-ready.'
    }
} finally {
    if (Test-Path -LiteralPath $sandbox) {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}
