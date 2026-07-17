[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('WindowsUi', 'AndroidDevice', 'LiveInfrastructure')]
    [string]$Lane,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{32}$')]
    [string]$ReleaseInvocationId,
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
    [string]$AaptPath,
    [string]$ApkSignerPath,
    [switch]$DedicatedManagedDevice,
    [switch]$CaptureStubWelcomeFailure
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'StrictLane.Common.ps1')
$sourceCommitSha = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommitSha -notmatch '^[0-9a-f]{40}$') {
    throw 'The strict lane must be bound to a valid local Git commit.'
}
$laneInvocationId = [Guid]::NewGuid().ToString('N')
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
        releaseInvocationId = $ReleaseInvocationId
        laneInvocationId = $laneInvocationId
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
        [string]$Evidence,
        [string]$EvidenceSha256 = ''
    )
    [ordered]@{
        schema = 'deep.survival.strict-lane-result.v1'
        programRevisionSha = 'ca5ad9f0c9d4dfb509dedcbf8133524c15867fce5534816da21ff86a07057383'
        sourceCommitSha = $sourceCommitSha
        releaseInvocationId = $ReleaseInvocationId
        laneInvocationId = $laneInvocationId
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        lane = $Lane
        status = $Status
        counters = $Counters
        evidence = $Evidence
        evidenceSha256 = $EvidenceSha256
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
    param(
        [Parameter(Mandatory)][object]$Snapshot,
        [Parameter(Mandatory)][bool]$PostRunVerified
    )
    [ordered]@{
        schema = 'deep.survival.windows-payload-manifest.v2'
        sourceCommitSha = $sourceCommitSha
        releaseInvocationId = $ReleaseInvocationId
        laneInvocationId = $laneInvocationId
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        postRunVerified = $PostRunVerified
        payloadRoot = $Snapshot.payloadRoot
        executable = $Snapshot.executable
        files = $Snapshot.files
    } | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'windows-payload-manifest.json') -Encoding utf8
}

function Resolve-RequiredTool {
    param(
        [string]$ExplicitPath,
        [string]$CommandName
    )
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        if (-not (Test-Path -LiteralPath $ExplicitPath -PathType Leaf)) {
            return $null
        }
        return [IO.Path]::GetFullPath($ExplicitPath)
    }
    $command = Get-Command $CommandName -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return [IO.Path]::GetFullPath($command.Source)
    }
    if (-not [string]::IsNullOrWhiteSpace($env:ANDROID_HOME)) {
        $candidate = Get-ChildItem -LiteralPath (Join-Path $env:ANDROID_HOME 'build-tools') -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName $CommandName } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Select-Object -First 1
        if ($null -ne $candidate) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }
    return $null
}

function Get-AndroidApkMetadata {
    param(
        [Parameter(Mandatory)][string]$Aapt,
        [Parameter(Mandatory)][string]$Apk,
        [Parameter(Mandatory)][string]$ApkSigner
    )
    $badging = (& $Aapt dump badging $Apk 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw 'aapt rejected the selected APK.'
    }
    $packageMatch = [regex]::Match(
        $badging,
        "package:\s+name='(?<package>[^']+)'\s+versionCode='(?<code>[^']+)'\s+versionName='(?<name>[^']+)'")
    if (-not $packageMatch.Success) {
        throw 'aapt did not return the required APK identity.'
    }
    $signing = (& $ApkSigner verify --print-certs $Apk 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw 'apksigner rejected the selected APK.'
    }
    $certificateMatch = [regex]::Match(
        $signing,
        '(?im)Signer #1 certificate SHA-256 digest:\s*(?<digest>[0-9a-f]{64})')
    if (-not $certificateMatch.Success) {
        throw 'apksigner did not return a SHA-256 signing certificate digest.'
    }
    return [ordered]@{
        apkSha256 = Get-Sha256Lower -Path $Apk
        packageId = $packageMatch.Groups['package'].Value
        versionCode = $packageMatch.Groups['code'].Value
        versionName = $packageMatch.Groups['name'].Value
        signingCertificateSha256 = $certificateMatch.Groups['digest'].Value.ToLowerInvariant()
    }
}

function Read-SanitizedJUnitCounters {
    param([Parameter(Mandatory)][string]$Path)
    $raw = Get-Content -LiteralPath $Path -Raw
    if ($raw -match '(?is)<!DOCTYPE|<!ENTITY|<\s*(system-out|system-err|attachments?|attachment)\b' -or
        $raw -match '(?i)([a-z]:\\|\\\\[^\\\s]+\\|/(home|users|data|storage|sdcard)/)' -or
        $raw -match '(?i)(mnemonic|seed\s+phrase|recovery\s+phrase|private\s+key|authorization\s*:|bearer\s+|password\s*[=:])') {
        throw 'Android JUnit contains prohibited private or non-allowlisted content.'
    }
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create([IO.StringReader]::new($raw), $settings)
    $document = [Xml.XmlDocument]::new()
    $document.XmlResolver = $null
    $document.Load($reader)
    $suites = @(if ($document.DocumentElement.LocalName -eq 'testsuite') {
        @($document.DocumentElement)
    } else {
        @($document.DocumentElement.ChildNodes | Where-Object { $_.LocalName -eq 'testsuite' })
    })
    if ($suites.Count -eq 0) {
        throw 'Android JUnit has no test-suite counters.'
    }
    $total = 0
    $failed = 0
    $skipped = 0
    foreach ($suite in $suites) {
        $total += [int]$suite.GetAttribute('tests')
        $failed += [int]$suite.GetAttribute('failures') + [int]$suite.GetAttribute('errors')
        $skipped += [int]$suite.GetAttribute('skipped')
    }
    return [ordered]@{
        total = $total
        executed = $total - $skipped
        passed = $total - $skipped - $failed
        failed = $failed
        skipped = $skipped
    }
}

if ($Lane -eq 'WindowsUi') {
    $isWindowsHost = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
    Add-Check 'host-os' $isWindowsHost $(if ($isWindowsHost) { 'Windows' } else { 'Windows is required' })
    $appExists = -not [string]::IsNullOrWhiteSpace($AppPath) -and (Test-Path -LiteralPath $AppPath -PathType Leaf)
    Add-Check 'maui-executable' $appExists $(if ($appExists) { 'present' } else { 'AppPath is required and must exist' })
    $appPayloadContained = $false
    $canonicalAppPath = $null
    $payloadRoot = $null
    $preRunSnapshot = $null
    if ($appExists) {
        try {
            $canonicalAppPath = Get-CanonicalContainedPath -Root $repoRoot -Candidate ([IO.Path]::GetFullPath($AppPath))
            $payloadRoot = [IO.Path]::GetDirectoryName($canonicalAppPath)
            Assert-NoReparsePointInPath -Root $repoRoot -Candidate $payloadRoot
            Assert-NoReparsePointInPath -Root $payloadRoot -Candidate $canonicalAppPath
            $preRunSnapshot = Get-StrictPayloadSnapshot `
                -RepositoryRoot $repoRoot `
                -PayloadRoot $payloadRoot `
                -ExecutablePath $canonicalAppPath
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
    Write-Preflight $(if ($blocked) { 'failed' } else { 'ready' }) | Out-Null
    if ($blocked) {
        Write-LaneResult 'blocked' $null 'machine-readable preflight'
        exit 2
    }

    $runRoot = Join-Path $ArtifactDirectory ("run-{0}" -f ([Guid]::NewGuid().ToString('N')))
    $appDataRoot = Join-Path $runRoot 'app-data'
    $uiEvidenceRoot = Join-Path $ArtifactDirectory 'windows-ui'
    New-Item -ItemType Directory -Force -Path $appDataRoot, $uiEvidenceRoot | Out-Null

    $env:DEEP_STRICT_WINDOWS_UI = '1'
    $env:DEEP_MAUI_EXE = $canonicalAppPath
    $env:DEEP_E2E_BOOTSTRAP = $Bootstrap
    $env:DEEP_E2E_APPDATA_ROOT = $appDataRoot
    $env:DEEP_E2E_ARTIFACTS = $uiEvidenceRoot
    $env:DEEP_E2E_CAPTURE_STUB_WELCOME_FAILURE = $(if ($CaptureStubWelcomeFailure -and $Bootstrap -eq 'stub') { '1' } else { '0' })
    Write-WindowsPayloadManifest -Snapshot $preRunSnapshot -PostRunVerified $false

    $trxName = 'strict-windows-ui.trx'
    dotnet test (Join-Path $repoRoot 'tests\Deep.Client.Maui.UiTests\Deep.Client.Maui.UiTests.csproj') `
        --no-restore `
        --logger "trx;LogFileName=$trxName" `
        --results-directory $ArtifactDirectory
    $testExit = $LASTEXITCODE
    $trxPath = Join-Path $ArtifactDirectory $trxName
    if (-not (Test-Path -LiteralPath $trxPath)) {
        Remove-PrivateArtifact $runRoot
        Add-Check 'windows-payload-post-run' $false 'TRX was missing; post-run payload verification was not completed'
        Write-Preflight 'failed' | Out-Null
        Write-LaneResult 'failed' $null 'TRX missing'
        exit 3
    }

    $counters = Read-TrxCounters $trxPath
    $payloadUnchanged = $true
    try {
        $postRunSnapshot = Get-StrictPayloadSnapshot `
            -RepositoryRoot $repoRoot `
            -PayloadRoot $payloadRoot `
            -ExecutablePath $canonicalAppPath
        Assert-StrictPayloadSnapshotsEqual -Expected $preRunSnapshot -Actual $postRunSnapshot
        Write-WindowsPayloadManifest -Snapshot $preRunSnapshot -PostRunVerified $true
    } catch {
        $payloadUnchanged = $false
    }
    Add-Check 'windows-payload-post-run' $payloadUnchanged $(if ($payloadUnchanged) {
        'exact pre-launch file set, lengths, paths, and hashes preserved'
    } else {
        'payload changed or acquired a reparse/duplicate path during execution'
    })
    Remove-PrivateArtifact $trxPath
    Remove-PrivateArtifact $runRoot
    $zeroSkipPassed = $counters.executed -gt 0 -and $counters.skipped -eq 0 -and $counters.failed -eq 0
    Add-Check 'zero-skip-gate' $zeroSkipPassed ("executed={0}; skipped={1}; failed={2}" -f $counters.executed, $counters.skipped, $counters.failed)
    $windowsPassed = $zeroSkipPassed -and $testExit -eq 0 -and $payloadUnchanged
    $manifestPath = Join-Path $ArtifactDirectory 'windows-payload-manifest.json'
    Write-Preflight $(if ($windowsPassed) { 'passed' } else { 'failed' }) | Out-Null
    Write-LaneResult `
        $(if ($windowsPassed) { 'passed' } else { 'failed' }) `
        $counters `
        'windows-payload-manifest.json' `
        $(if (Test-Path -LiteralPath $manifestPath) { Get-Sha256Lower -Path $manifestPath } else { '' })
    if (-not $windowsPassed) {
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
    $dedicated = [bool]$DedicatedManagedDevice
    Add-Check 'dedicated-managed-device' $dedicated $(if ($dedicated) {
        'caller attested a dedicated managed test device'
    } else {
        'explicit -DedicatedManagedDevice attestation is required; personal devices are prohibited'
    })

    $canonicalApkPath = $null
    $apkMetadata = $null
    $aapt = Resolve-RequiredTool -ExplicitPath $AaptPath -CommandName 'aapt.exe'
    $apkSigner = Resolve-RequiredTool -ExplicitPath $ApkSignerPath -CommandName 'apksigner.bat'
    Add-Check 'aapt' ($null -ne $aapt) $(if ($null -ne $aapt) { 'present' } else { 'aapt is required' })
    Add-Check 'apksigner' ($null -ne $apkSigner) $(if ($null -ne $apkSigner) { 'present' } else { 'apksigner is required' })
    if ($apkExists -and $null -ne $aapt -and $null -ne $apkSigner) {
        try {
            $canonicalApkPath = Get-CanonicalContainedPath -Root $repoRoot -Candidate ([IO.Path]::GetFullPath($ApkPath))
            Assert-NoReparsePointInPath -Root $repoRoot -Candidate $canonicalApkPath
            $apkMetadata = Get-AndroidApkMetadata -Aapt $aapt -Apk $canonicalApkPath -ApkSigner $apkSigner
        } catch {
            $apkMetadata = $null
        }
    }
    $testPackage = $null -ne $apkMetadata -and $apkMetadata.packageId -ceq 'network.xpoint.deep.e2e'
    Add-Check 'apk-debug-test-identity' $testPackage $(if ($testPackage) {
        'network.xpoint.deep.e2e'
    } else {
        'strict physical E2E accepts only the Debug test package; the production package is prohibited'
    })

    $serial = $AndroidSerial
    $attached = $false
    $productionPackageAbsent = $false
    if ($null -ne $adb) {
        $deviceLines = @(& $adb.Source devices | Select-Object -Skip 1 | Where-Object { $_ -match '\S+\s+device$' })
        if ([string]::IsNullOrWhiteSpace($serial) -and $deviceLines.Count -eq 1) {
            $serial = ($deviceLines[0] -split '\s+')[0]
        }
        $attached = -not [string]::IsNullOrWhiteSpace($serial) -and
            @($deviceLines | Where-Object { $_ -match ("^{0}\s+device$" -f [regex]::Escape($serial)) }).Count -eq 1
        Add-Check 'android-device' $attached $(if ($attached) { 'one authorized device selected' } else { 'no unique authorized device attached' })
        if ($attached) {
            $productionPackages = @(& $adb.Source -s $serial shell pm list packages 'network.xpoint.deep' 2>$null)
            $packageQuerySucceeded = $LASTEXITCODE -eq 0
            $productionPackageAbsent = $packageQuerySucceeded -and
                @($productionPackages | Where-Object { $_.Trim() -ceq 'package:network.xpoint.deep' }).Count -eq 0
        }
        Add-Check 'production-package-absent' $productionPackageAbsent $(if ($productionPackageAbsent) {
            'production package is absent; wrapper will not inspect or modify it'
        } else {
            'production package is installed or package-state attestation failed; device rejected'
        })

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
    $canonicalRunner = $null
    $runnerSha256 = $null
    if ($runnerExists) {
        try {
            $canonicalRunner = [IO.Path]::GetFullPath($AndroidRunner)
            $runnerItem = Get-Item -LiteralPath $canonicalRunner -Force
            if (($runnerItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Android runner may not be a reparse point.'
            }
            $runnerSha256 = Get-Sha256Lower -Path $canonicalRunner
        } catch {
            $runnerExists = $false
        }
    }
    Add-Check 'android-runner-hash' ($null -ne $runnerSha256) $(if ($null -ne $runnerSha256) {
        'runner SHA-256 captured'
    } else {
        'runner identity could not be captured'
    })

    $blocked = @($checks.Where({ $_.status -ne 'passed' })).Count -gt 0
    Write-Preflight $(if ($blocked) { 'failed' } else { 'ready' }) | Out-Null
    if ($blocked) {
        Write-LaneResult 'blocked' $null 'device execution did not run'
        exit 2
    }

    $runnerRoot = Join-Path $ArtifactDirectory ("android-run-{0}-{1}" -f $ReleaseInvocationId, $laneInvocationId)
    $rawRoot = Join-Path $runnerRoot 'quarantine\raw'
    New-Item -ItemType Directory -Force -Path $rawRoot | Out-Null
    $runnerResultPath = Join-Path $rawRoot 'runner-result.json'
    $junitPath = Join-Path $rawRoot 'android-device.junit.xml'
    $runnerArguments = @(
        '--serial', $serial,
        '--apk', $canonicalApkPath,
        '--artifacts', $runnerRoot,
        '--result', $runnerResultPath,
        '--junit', $junitPath,
        '--commit', $sourceCommitSha,
        '--release-invocation', $ReleaseInvocationId,
        '--lane-invocation', $laneInvocationId,
        '--apk-sha256', $apkMetadata.apkSha256,
        '--package-id', $apkMetadata.packageId,
        '--version-code', $apkMetadata.versionCode,
        '--version-name', $apkMetadata.versionName,
        '--signing-cert-sha256', $apkMetadata.signingCertificateSha256,
        '--runner-sha256', $runnerSha256,
        '--dedicated-managed', 'true'
    )
    & $canonicalRunner @runnerArguments
    $runnerExit = $LASTEXITCODE
    if (-not (Test-Path -LiteralPath $runnerResultPath -PathType Leaf)) {
        Add-Check 'android-runner-contract' $false 'runner result missing'
        Write-Preflight 'failed' | Out-Null
        Write-LaneResult 'failed' $null 'Android runner result JSON missing'
        exit 3
    }
    if (-not (Test-Path -LiteralPath $junitPath -PathType Leaf)) {
        Add-Check 'android-runner-contract' $false 'runner JUnit missing'
        Write-Preflight 'failed' | Out-Null
        Write-LaneResult 'failed' $null 'Android runner JUnit missing'
        exit 3
    }
    $contractPassed = $false
    $contractDiagnostic = 'contract processing failed'
    $counters = $null
    $sanitizedSummaryPath = Join-Path $ArtifactDirectory 'android-device-summary.json'
    $contractStage = 'parse-result'
    try {
        $runnerResult = Get-Content -LiteralPath $runnerResultPath -Raw | ConvertFrom-Json
        $counters = [ordered]@{
            total = [int]$runnerResult.counters.total
            executed = [int]$runnerResult.counters.executed
            passed = [int]$runnerResult.counters.passed
            failed = [int]$runnerResult.counters.failed
            skipped = [int]$runnerResult.counters.skipped
        }
        $contractStage = 'parse-junit'
        $junitCounters = Read-SanitizedJUnitCounters -Path $junitPath
        $contractStage = 'bind-junit'
        $junitSha256 = Get-Sha256Lower -Path $junitPath
        $counterContract = $counters.total -eq ($counters.executed + $counters.skipped) -and
            $counters.executed -eq ($counters.passed + $counters.failed) -and
            $counters.total -eq $junitCounters.total -and
            $counters.executed -eq $junitCounters.executed -and
            $counters.passed -eq $junitCounters.passed -and
            $counters.failed -eq $junitCounters.failed -and
            $counters.skipped -eq $junitCounters.skipped
        $bindingContract = $runnerResult.schema -eq 'deep.survival.android-runner-result.v2' -and
            $runnerResult.sourceCommitSha -ceq $sourceCommitSha -and
            $runnerResult.releaseInvocationId -ceq $ReleaseInvocationId -and
            $runnerResult.laneInvocationId -ceq $laneInvocationId -and
            $runnerResult.apkSha256 -ceq $apkMetadata.apkSha256 -and
            $runnerResult.packageId -ceq $apkMetadata.packageId -and
            [string]$runnerResult.versionCode -ceq [string]$apkMetadata.versionCode -and
            [string]$runnerResult.versionName -ceq [string]$apkMetadata.versionName -and
            $runnerResult.signingCertificateSha256 -ceq $apkMetadata.signingCertificateSha256 -and
            $runnerResult.runnerSha256 -ceq $runnerSha256 -and
            $runnerResult.junitSha256 -ceq $junitSha256 -and
            $runnerResult.device.serial -ceq $serial -and
            $runnerResult.device.dedicatedManaged -eq $true -and
            $runnerResult.device.personalDataAbsent -eq $true -and
            $runnerResult.device.productionPackageAbsentBefore -eq $true -and
            $runnerResult.device.testPackageClearedBefore -eq $true -and
            $runnerResult.device.testPackageRemovedAfter -eq $true -and
            -not [string]::IsNullOrWhiteSpace([string]$runnerResult.runnerVersion)
        $contractDiagnostic = "bindings=$bindingContract; counters=$counterContract; junitHashBound=$($runnerResult.junitSha256 -ceq $junitSha256)"
        $contractPassed = $bindingContract -and
            $counterContract -and
            $runnerResult.status -eq 'passed' -and
            $counters.executed -gt 0 -and
            $counters.skipped -eq 0 -and
            $counters.failed -eq 0 -and
            $runnerExit -eq 0
        if ($contractPassed) {
            $contractStage = 'write-sanitized-summary'
            [ordered]@{
                schema = 'deep.survival.android-device-summary.v1'
                sourceCommitSha = $sourceCommitSha
                releaseInvocationId = $ReleaseInvocationId
                laneInvocationId = $laneInvocationId
                generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
                status = 'passed'
                apk = [ordered]@{
                    sha256 = $apkMetadata.apkSha256
                    packageId = $apkMetadata.packageId
                    versionCode = $apkMetadata.versionCode
                    versionName = $apkMetadata.versionName
                    signingCertificateSha256 = $apkMetadata.signingCertificateSha256
                }
                runner = [ordered]@{
                    sha256 = $runnerSha256
                    version = [string]$runnerResult.runnerVersion
                }
                device = [ordered]@{
                    serialSha256 = $((
                        [BitConverter]::ToString(
                            [Security.Cryptography.SHA256]::Create().ComputeHash(
                                [Text.Encoding]::UTF8.GetBytes($serial)))).Replace('-', '').ToLowerInvariant())
                    dedicatedManaged = $true
                    personalDataAbsent = $true
                    productionPackageAbsentBefore = $true
                    testPackageClearedBefore = $true
                    testPackageRemovedAfter = $true
                }
                counters = $counters
                junitSha256 = $junitSha256
            } | ConvertTo-Json -Depth 7 |
                Set-Content -LiteralPath $sanitizedSummaryPath -Encoding utf8
        }
    } catch {
        $contractPassed = $false
        $contractDiagnostic = "stage=$contractStage"
    }
    $counterDetail = if ($null -eq $counters) {
        'unavailable'
    } else {
        "executed={0}; skipped={1}; failed={2}" -f
            $counters.executed, $counters.skipped, $counters.failed
    }
    Add-Check 'android-runner-contract' $contractPassed (
        "exit={0}; counters={1}; {2}" -f $runnerExit, $counterDetail, $contractDiagnostic)
    Write-Preflight $(if ($contractPassed) { 'passed' } else { 'failed' }) | Out-Null
    Write-LaneResult `
        $(if ($contractPassed) { 'passed' } else { 'failed' }) `
        $counters `
        $(if ($contractPassed) { 'android-device-summary.json' } else { 'quarantined raw Android evidence rejected' }) `
        $(if ($contractPassed) { Get-Sha256Lower -Path $sanitizedSummaryPath } else { '' })
    if (-not $contractPassed) {
        exit 4
    }
    exit 0
}

Test-LiveConfiguration
$blocked = @($checks.Where({ $_.status -ne 'passed' })).Count -gt 0
Write-Preflight $(if ($blocked) { 'failed' } else { 'ready' }) | Out-Null
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
    Add-Check 'live-test-contract' $false 'TRX was missing'
    Write-Preflight 'failed' | Out-Null
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
