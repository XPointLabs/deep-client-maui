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
    [string]$AndroidRunner,
    [string]$AdbPath,
    [switch]$CaptureStubWelcomeFailure
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'StrictLane.Common.ps1')
$sourceCommitSha = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommitSha -notmatch '^[0-9a-f]{40}$') {
    throw 'The strict lane must be bound to a valid local Git commit.'
}
$invocationId = [Guid]::NewGuid().ToString('N')
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $ArtifactDirectory = Join-Path $repoRoot 'artifacts\survival\I01-MAUI-STRICT-GATES'
}
$ArtifactDirectory = [System.IO.Path]::GetFullPath($ArtifactDirectory)
$ArtifactDirectory = Get-CanonicalContainedPath -Root $repoRoot -Candidate $ArtifactDirectory
Assert-NoReparsePointInPath -Root $repoRoot -Candidate $ArtifactDirectory
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
    $rawRouters = [Environment]::GetEnvironmentVariable('XNODE_URLS')
    $routerEntries = @(if ([string]::IsNullOrWhiteSpace($rawRouters)) {
        @()
    } else {
        @($rawRouters.Split(
            @(';', ',', "`n", "`r", "`t", ' '),
            [StringSplitOptions]::RemoveEmptyEntries -bor [StringSplitOptions]::TrimEntries))
    })
    $parsedRouters = @($routerEntries | ForEach-Object {
        $parts = @($_.Split('|', 2, [StringSplitOptions]::TrimEntries))
        if ($parts.Count -ne 2 -or $parts[0] -notmatch '^[0-9a-fA-F]{64}$') {
            return $null
        }
        $uri = $null
        if (-not [Uri]::TryCreate($parts[1], [UriKind]::Absolute, [ref]$uri)) {
            return $null
        }
        [pscustomobject]@{ routerId = $parts[0].ToLowerInvariant(); url = $uri.AbsoluteUri }
    })
    $validRouters = $routerEntries.Count -eq 3 -and
        $parsedRouters.Count -eq 3 -and
        @($parsedRouters.routerId | Sort-Object -Unique).Count -eq 3 -and
        @($parsedRouters.url | Sort-Object -Unique).Count -eq 3
    Add-Check 'routed-message-endpoint' $validRouters $(if ($validRouters) {
        'exactly three distinct pinned router identities and URLs'
    } else {
        'XNODE_URLS must contain exactly three distinct <64-hex-routerId>|<absolute-url> entries; direct storage cannot satisfy this lane'
    })
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
        sourceCommitSha = $sourceCommitSha
        invocationId = $invocationId
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
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
        sourceCommitSha = $sourceCommitSha
        invocationId = $invocationId
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
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
    Remove-ContainedTree -Root $ArtifactDirectory -Candidate ([System.IO.Path]::GetFullPath($Path))
}

function Write-WindowsPayloadManifest {
    param([Parameter(Mandatory)][string]$Executable)
    $payloadRoot = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Executable))
    $files = @(Get-ChildItem -LiteralPath $payloadRoot -File -Recurse | Sort-Object FullName)
    if ($files.Count -eq 0) {
        throw 'The Windows payload directory is empty.'
    }
    $entries = @($files | ForEach-Object {
        $relative = (Get-RelativePathCompat -Root $payloadRoot -Candidate $_.FullName).Replace('\', '/')
        if ((Test-FullyQualifiedPath $relative) -or $relative.Split('/').Contains('..')) {
            throw 'A Windows payload file escaped its payload root.'
        }
        [ordered]@{
            relativePath = $relative
            length = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
    [ordered]@{
        schema = 'deep.survival.windows-payload-manifest.v1'
        sourceCommitSha = $sourceCommitSha
        invocationId = $invocationId
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        payloadRoot = (Get-RelativePathCompat -Root $repoRoot -Candidate $payloadRoot).Replace('\', '/')
        executable = [IO.Path]::GetFileName($Executable)
        files = $entries
    } | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'windows-payload-manifest.json') -Encoding utf8
}

if ($Lane -eq 'WindowsUi') {
    $isWindowsHost = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
    Add-Check 'host-os' $isWindowsHost $(if ($isWindowsHost) { 'Windows' } else { 'Windows is required' })
    $appExists = -not [string]::IsNullOrWhiteSpace($AppPath) -and (Test-Path -LiteralPath $AppPath -PathType Leaf)
    Add-Check 'maui-executable' $appExists $(if ($appExists) { 'present' } else { 'AppPath is required and must exist' })
    $appPayloadContained = $false
    if ($appExists) {
        try {
            Get-CanonicalContainedPath -Root $repoRoot -Candidate ([IO.Path]::GetFullPath($AppPath)) | Out-Null
            $appPayloadContained = $true
        } catch {
            $appPayloadContained = $false
        }
    }
    Add-Check 'maui-payload-root' $appPayloadContained $(if ($appPayloadContained) { 'repository-local' } else { 'AppPath must be inside the repository' })
    Add-Check 'artifact-root' $true 'canonical repository child without reparse points'
    if ($Bootstrap -eq 'live') {
        Test-LiveConfiguration
    } else {
        Add-Check 'debug-bootstrap' $true 'explicit stub; accepted only by Debug app code'
    }

    $blocked = @($checks.Where({ $_.status -ne 'passed' })).Count -gt 0
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
    $env:DEEP_E2E_CAPTURE_STUB_WELCOME_FAILURE = $(if ($CaptureStubWelcomeFailure -and $Bootstrap -eq 'stub') { '1' } else { '0' })

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
    Write-WindowsPayloadManifest -Executable $AppPath
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
    $adb = if ([string]::IsNullOrWhiteSpace($AdbPath)) {
        Get-Command adb -ErrorAction SilentlyContinue
    } elseif (Test-Path -LiteralPath $AdbPath -PathType Leaf) {
        Get-Command ([IO.Path]::GetFullPath($AdbPath)) -ErrorAction SilentlyContinue
    } else {
        $null
    }
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
            @($deviceLines | Where-Object { $_ -match ("^{0}\s+device$" -f [regex]::Escape($serial)) }).Count -eq 1
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

    $blocked = @($checks.Where({ $_.status -ne 'passed' })).Count -gt 0
    Write-Preflight $(if ($blocked) { 'blocked' } else { 'ready' }) | Out-Null
    if ($blocked) {
        Write-LaneResult 'blocked' $null 'device execution did not run'
        exit 2
    }

    $runnerRoot = Join-Path $ArtifactDirectory ("android-run-{0}" -f $invocationId)
    New-Item -ItemType Directory -Force -Path $runnerRoot | Out-Null
    $runnerResultPath = Join-Path $runnerRoot 'runner-result.json'
    $junitPath = Join-Path $runnerRoot 'android-device.junit.xml'
    $runnerArguments = @(
        '--serial', $serial,
        '--apk', [IO.Path]::GetFullPath($ApkPath),
        '--artifacts', $runnerRoot,
        '--result', $runnerResultPath,
        '--junit', $junitPath,
        '--commit', $sourceCommitSha
    )
    & ([IO.Path]::GetFullPath($AndroidRunner)) @runnerArguments
    $runnerExit = $LASTEXITCODE
    if (-not (Test-Path -LiteralPath $runnerResultPath -PathType Leaf)) {
        Write-LaneResult 'failed' $null 'Android runner result JSON missing'
        exit 3
    }
    if (-not (Test-Path -LiteralPath $junitPath -PathType Leaf)) {
        Write-LaneResult 'failed' $null 'Android runner JUnit missing'
        exit 3
    }
    $runnerResult = Get-Content -LiteralPath $runnerResultPath -Raw | ConvertFrom-Json
    $counters = [ordered]@{
        total = [int]$runnerResult.counters.total
        executed = [int]$runnerResult.counters.executed
        passed = [int]$runnerResult.counters.passed
        failed = [int]$runnerResult.counters.failed
        skipped = [int]$runnerResult.counters.skipped
    }
    $contractPassed = $runnerResult.schema -eq 'deep.survival.android-runner-result.v1' -and
        $runnerResult.sourceCommitSha -eq $sourceCommitSha -and
        $runnerResult.status -eq 'passed' -and
        $counters.executed -gt 0 -and
        $counters.skipped -eq 0 -and
        $counters.failed -eq 0 -and
        $runnerExit -eq 0
    Add-Check 'android-runner-contract' $contractPassed (
        "exit={0}; executed={1}; skipped={2}; failed={3}" -f
            $runnerExit, $counters.executed, $counters.skipped, $counters.failed)
    Write-Preflight $(if ($contractPassed) { 'passed' } else { 'failed' }) | Out-Null
    Write-LaneResult $(if ($contractPassed) { 'passed' } else { 'failed' }) $counters 'physical Android runner plus JUnit'
    if (-not $contractPassed) {
        exit 4
    }
    exit 0
}

Test-LiveConfiguration
$blocked = @($checks.Where({ $_.status -ne 'passed' })).Count -gt 0
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
