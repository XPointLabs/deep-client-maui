[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('WindowsUi', 'AndroidDevice', 'LiveInfrastructure')]
    [string]$Lane,

    [string]$AppPath,
    [string]$ApkPath,
    [string]$ArtifactDirectory,
    [ValidateSet('stub', 'live')]
    [string]$Bootstrap = 'stub',
    [string]$AndroidSerial,
    [int[]]$ReversePort = @(28100, 28101, 28102, 28103, 29281, 29282, 29283),
    [switch]$ConfigureAdbReverse,
    [string]$AndroidRunner
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $ArtifactDirectory = Join-Path $repoRoot 'artifacts\survival\I01-MAUI-STRICT-GATES'
}
$ArtifactDirectory = [System.IO.Path]::GetFullPath($ArtifactDirectory)
New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null

$checks = [System.Collections.Generic.List[object]]::new()
function Add-Check {
    param(
        [string]$Name,
        [bool]$Passed,
        [string]$Detail
    )
    $checks.Add([ordered]@{
        name = $Name
        status = $(if ($Passed) { 'passed' } else { 'blocked' })
        detail = $Detail
    })
}

function Has-Value {
    param([string]$Name)
    return -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($Name))
}

function Test-LiveConfiguration {
    $hasMessageEndpoint = (Has-Value 'XNODE_URLS') -or (Has-Value 'DEEP_STORAGE_URL')
    Add-Check 'message-endpoint' $hasMessageEndpoint $(if ($hasMessageEndpoint) { 'configured' } else { 'XNODE_URLS or DEEP_STORAGE_URL is required' })
    foreach ($name in @('DEEP_FILE_URL', 'DEEP_PUSH_URL', 'DEEP_CALL_SIGNALING_BASE_URL')) {
        $present = Has-Value $name
        Add-Check $name $present $(if ($present) { 'configured' } else { 'required' })
    }
}

function Write-Preflight {
    param([string]$Status)
    $payload = [ordered]@{
        schema = 'deep.survival.strict-preflight.v1'
        programRevisionSha = 'ca5ad9f0c9d4dfb509dedcbf8133524c15867fce5534816da21ff86a07057383'
        lane = $Lane
        status = $Status
        checks = $checks
    }
    $path = Join-Path $ArtifactDirectory ("preflight-{0}.json" -f $Lane.ToLowerInvariant())
    $payload | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $path -Encoding utf8
    return $path
}

function Read-TrxCounters {
    param([string]$Path)
    [xml]$trx = Get-Content -Raw -LiteralPath $Path
    $counters = $trx.TestRun.ResultSummary.Counters
    if ($null -eq $counters) {
        throw 'TRX counters were not found.'
    }
    return [ordered]@{
        total = [int]$counters.total
        executed = [int]$counters.executed
        passed = [int]$counters.passed
        failed = [int]$counters.failed
        skipped = [int]$counters.notExecuted
    }
}

function Write-LaneResult {
    param(
        [string]$Status,
        [object]$Counters,
        [string]$Evidence
    )
    [ordered]@{
        schema = 'deep.survival.strict-lane-result.v1'
        programRevisionSha = 'ca5ad9f0c9d4dfb509dedcbf8133524c15867fce5534816da21ff86a07057383'
        lane = $Lane
        status = $Status
        counters = $Counters
        evidence = $Evidence
    } | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $ArtifactDirectory ("result-{0}.json" -f $Lane.ToLowerInvariant())) -Encoding utf8
}

function Remove-PrivateArtifact {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $resolved = [System.IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($ArtifactDirectory, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to remove a path outside the selected artifact directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

if ($Lane -eq 'WindowsUi') {
    $isWindowsHost = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
    Add-Check 'host-os' $isWindowsHost $(if ($isWindowsHost) { 'Windows' } else { 'Windows is required' })
    $appExists = -not [string]::IsNullOrWhiteSpace($AppPath) -and (Test-Path -LiteralPath $AppPath -PathType Leaf)
    Add-Check 'maui-executable' $appExists $(if ($appExists) { 'present' } else { 'AppPath is required and must exist' })
    $artifactRootSafe = $ArtifactDirectory.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)
    Add-Check 'artifact-root' $artifactRootSafe $(if ($artifactRootSafe) { 'repository-local' } else { 'must be inside the repository' })
    if ($Bootstrap -eq 'live') {
        Test-LiveConfiguration
    } else {
        Add-Check 'debug-bootstrap' $true 'explicit stub; accepted only by Debug app code'
    }

    $blocked = $checks.Where({ $_.status -ne 'passed' }).Count -gt 0
    Write-Preflight $(if ($blocked) { 'blocked' } else { 'ready' }) | Out-Null
    if ($blocked) {
        Write-LaneResult 'blocked' $null 'machine-readable preflight'
        exit 2
    }

    $runRoot = Join-Path $ArtifactDirectory ("run-{0}" -f ([Guid]::NewGuid().ToString('N')))
    $appDataRoot = Join-Path $runRoot 'app-data'
    $uiEvidenceRoot = Join-Path $ArtifactDirectory 'windows-ui'
    New-Item -ItemType Directory -Force -Path $appDataRoot, $uiEvidenceRoot | Out-Null

    $env:DEEP_STRICT_WINDOWS_UI = '1'
    $env:DEEP_MAUI_EXE = [System.IO.Path]::GetFullPath($AppPath)
    $env:DEEP_E2E_BOOTSTRAP = $Bootstrap
    $env:DEEP_E2E_APPDATA_ROOT = $appDataRoot
    $env:DEEP_E2E_ARTIFACTS = $uiEvidenceRoot

    $trxName = 'strict-windows-ui.trx'
    dotnet test (Join-Path $repoRoot 'tests\Deep.Client.Maui.UiTests\Deep.Client.Maui.UiTests.csproj') `
        --no-restore `
        --logger "trx;LogFileName=$trxName" `
        --results-directory $ArtifactDirectory
    $testExit = $LASTEXITCODE
    $trxPath = Join-Path $ArtifactDirectory $trxName
    if (-not (Test-Path -LiteralPath $trxPath)) {
        Remove-PrivateArtifact $runRoot
        Write-LaneResult 'failed' $null 'TRX missing'
        exit 3
    }

    $counters = Read-TrxCounters $trxPath
    Remove-PrivateArtifact $trxPath
    Remove-PrivateArtifact $runRoot
    $zeroSkipPassed = $counters.executed -gt 0 -and $counters.skipped -eq 0 -and $counters.failed -eq 0
    Add-Check 'zero-skip-gate' $zeroSkipPassed ("executed={0}; skipped={1}; failed={2}" -f $counters.executed, $counters.skipped, $counters.failed)
    Write-Preflight $(if ($zeroSkipPassed -and $testExit -eq 0) { 'passed' } else { 'failed' }) | Out-Null
    Write-LaneResult $(if ($zeroSkipPassed -and $testExit -eq 0) { 'passed' } else { 'failed' }) $counters 'real MAUI process plus UIA3'
    if (-not $zeroSkipPassed -or $testExit -ne 0) {
        exit 4
    }
    exit 0
}

if ($Lane -eq 'AndroidDevice') {
    $adb = Get-Command adb -ErrorAction SilentlyContinue
    Add-Check 'adb' ($null -ne $adb) $(if ($null -ne $adb) { 'present' } else { 'adb is required' })
    $apkExists = -not [string]::IsNullOrWhiteSpace($ApkPath) -and (Test-Path -LiteralPath $ApkPath -PathType Leaf)
    Add-Check 'apk' $apkExists $(if ($apkExists) { 'present' } else { 'ApkPath is required and must exist' })

    $serial = $AndroidSerial
    if ($null -ne $adb) {
        $deviceLines = @(& $adb.Source devices | Select-Object -Skip 1 | Where-Object { $_ -match '\S+\s+device$' })
        if ([string]::IsNullOrWhiteSpace($serial) -and $deviceLines.Count -eq 1) {
            $serial = ($deviceLines[0] -split '\s+')[0]
        }
        $attached = -not [string]::IsNullOrWhiteSpace($serial) -and
            ($deviceLines | Where-Object { $_ -match ("^{0}\s+device$" -f [regex]::Escape($serial)) }).Count -eq 1
        Add-Check 'android-device' $attached $(if ($attached) { 'one authorized device selected' } else { 'no unique authorized device attached' })

        if ($attached -and $ConfigureAdbReverse) {
            $reverseSucceeded = $true
            foreach ($port in $ReversePort | Sort-Object -Unique) {
                & $adb.Source -s $serial reverse "tcp:$port" "tcp:$port" | Out-Null
                if ($LASTEXITCODE -ne 0) {
                    $reverseSucceeded = $false
                    break
                }
            }
            Add-Check 'adb-reverse' $reverseSucceeded $(if ($reverseSucceeded) { 'configured for selected ports' } else { 'device rejected adb reverse' })
        } elseif ($attached) {
            Add-Check 'adb-reverse' $true 'supported; use -ConfigureAdbReverse to configure'
        }
    }

    $runnerExists = -not [string]::IsNullOrWhiteSpace($AndroidRunner) -and (Test-Path -LiteralPath $AndroidRunner -PathType Leaf)
    Add-Check 'android-e2e-runner' $runnerExists $(if ($runnerExists) { 'present' } else { 'real device runner is required; APK build alone is not execution' })

    $blocked = $checks.Where({ $_.status -ne 'passed' }).Count -gt 0
    Write-Preflight $(if ($blocked) { 'blocked' } else { 'ready' }) | Out-Null
    if ($blocked) {
        Write-LaneResult 'blocked' $null 'device execution did not run'
        exit 2
    }

    Write-LaneResult 'blocked' $null 'runner handoff is intentionally not treated as executed by this package'
    exit 2
}

Test-LiveConfiguration
$blocked = $checks.Where({ $_.status -ne 'passed' }).Count -gt 0
Write-Preflight $(if ($blocked) { 'blocked' } else { 'ready' }) | Out-Null
if ($blocked) {
    Write-LaneResult 'blocked' $null 'machine-readable preflight'
    exit 2
}

$env:DEEP_STRICT_LIVE = '1'
$trxName = 'strict-live-infrastructure.trx'
dotnet test (Join-Path $repoRoot 'tests\Deep.Client.Maui.ViewModels.Tests\Deep.Client.Maui.ViewModels.Tests.csproj') `
    --no-restore `
    --filter 'FullyQualifiedName~ClientLiveAcceptanceTests' `
    --logger "trx;LogFileName=$trxName" `
    --results-directory $ArtifactDirectory
$testExit = $LASTEXITCODE
$trxPath = Join-Path $ArtifactDirectory $trxName
if (-not (Test-Path -LiteralPath $trxPath)) {
    Write-LaneResult 'failed' $null 'TRX missing'
    exit 3
}

$counters = Read-TrxCounters $trxPath
Remove-PrivateArtifact $trxPath
$zeroSkipPassed = $counters.executed -gt 0 -and $counters.skipped -eq 0 -and $counters.failed -eq 0
Add-Check 'zero-skip-gate' $zeroSkipPassed ("executed={0}; skipped={1}; failed={2}" -f $counters.executed, $counters.skipped, $counters.failed)
Write-Preflight $(if ($zeroSkipPassed -and $testExit -eq 0) { 'passed' } else { 'failed' }) | Out-Null
Write-LaneResult $(if ($zeroSkipPassed -and $testExit -eq 0) { 'passed' } else { 'failed' }) $counters 'live infrastructure acceptance'
if (-not $zeroSkipPassed -or $testExit -ne 0) {
    exit 4
}
