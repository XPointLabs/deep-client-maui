[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{32}$')]
    [string]$ReleaseInvocationId,
    [string]$ArtifactDirectory,
    [string]$AndroidLabPolicyPath,
    [string]$AndroidApkPath,
    [switch]$RequireComplete,
    [int]$MaximumAgeHours = 24
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'StrictLane.Common.ps1')
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $ArtifactDirectory = Join-Path $repoRoot 'artifacts\survival\I01-MAUI-STRICT-GATES'
}
$ArtifactDirectory = Get-CanonicalContainedPath -Root $repoRoot -Candidate ([IO.Path]::GetFullPath($ArtifactDirectory))
Assert-NoReparsePointInPath -Root $repoRoot -Candidate $ArtifactDirectory
New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null
$commit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') {
    throw 'Strict evidence validation requires a valid local Git commit.'
}
$sourceTreeClean = @(& git -C $repoRoot status --porcelain).Count -eq 0
$lanes = [ordered]@{
    windowsui = 'WindowsUi'
    androiddevice = 'AndroidDevice'
    liveinfrastructure = 'LiveInfrastructure'
}
$checks = [Collections.Generic.List[object]]::new()
$laneRecords = @{}
$now = [DateTimeOffset]::UtcNow

function Test-FreshTimestamp {
    param([object]$Value)
    try {
        $generated = [DateTimeOffset]::Parse(
            [string]$Value,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind)
        return $generated -le $now.AddMinutes(5) -and $generated -ge $now.AddHours(-$MaximumAgeHours)
    } catch {
        return $false
    }
}

function Test-NonZeroSha256Value {
    param([string]$Value)
    return $Value -match '^[0-9a-f]{64}$' -and $Value -ne ('0' * 64)
}

function Test-SafeSummaryVersion {
    param([string]$Value)
    return -not [string]::IsNullOrWhiteSpace($Value) -and
        $Value.Length -le 128 -and
        $Value -match '^[A-Za-z0-9][A-Za-z0-9 ._+(),-]*$' -and
        $Value -notmatch '(?i)(password|passphrase|token|secret|mnemonic|seed|private.?key|authorization|bearer|recovery)'
}

function Get-TextSha256Lower {
    param([Parameter(Mandatory)][string]$Value)
    $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }
}

foreach ($laneKey in $lanes.Keys) {
    $resultPath = Join-Path $ArtifactDirectory ("result-{0}.json" -f $laneKey)
    $preflightPath = Join-Path $ArtifactDirectory ("preflight-{0}.json" -f $laneKey)
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $preflightPath -PathType Leaf)) {
        $checks.Add([ordered]@{
            lane = $laneKey
            status = 'not-run'
            detail = 'matching result and final preflight are required'
        })
        continue
    }
    try {
        $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        $preflight = Get-Content -LiteralPath $preflightPath -Raw | ConvertFrom-Json
        $laneInvocation = [string]$result.laneInvocationId
        $countersValid = $null -ne $result.counters -and
            [int]$result.counters.total -eq ([int]$result.counters.executed + [int]$result.counters.skipped) -and
            [int]$result.counters.executed -eq ([int]$result.counters.passed + [int]$result.counters.failed) -and
            [int]$result.counters.executed -gt 0 -and
            [int]$result.counters.skipped -eq 0 -and
            [int]$result.counters.failed -eq 0
        $preflightChecksPassed = $null -ne $preflight.checks -and
            @($preflight.checks).Count -gt 0 -and
            @($preflight.checks | Where-Object { $_.status -ne 'passed' }).Count -eq 0
        $identityBound = $result.schema -ceq 'deep.survival.strict-lane-result.v1' -and
            $preflight.schema -ceq 'deep.survival.strict-preflight.v1' -and
            $result.lane -ceq $lanes[$laneKey] -and
            $preflight.lane -ceq $lanes[$laneKey] -and
            $result.sourceCommitSha -ceq $commit -and
            $preflight.sourceCommitSha -ceq $commit -and
            $result.releaseInvocationId -ceq $ReleaseInvocationId -and
            $preflight.releaseInvocationId -ceq $ReleaseInvocationId -and
            $laneInvocation -match '^[0-9a-f]{32}$' -and
            $preflight.laneInvocationId -ceq $laneInvocation
        $fresh = (Test-FreshTimestamp $result.generatedAtUtc) -and
            (Test-FreshTimestamp $preflight.generatedAtUtc)
        $passed = $identityBound -and
            $fresh -and
            $countersValid -and
            $preflightChecksPassed -and
            $result.status -ceq 'passed' -and
            $preflight.status -ceq 'passed'
        $entry = [ordered]@{
            lane = $laneKey
            status = $(if ($passed) { 'passed' } else { 'failed' })
            detail = "identityBound=$identityBound; fresh=$fresh; countersValid=$countersValid; finalPreflightPassed=$preflightChecksPassed"
        }
        $checks.Add($entry)
        $laneRecords[$laneKey] = [ordered]@{
            result = $result
            preflight = $preflight
            entry = $entry
            laneInvocationId = $laneInvocation
        }
    } catch {
        $checks.Add([ordered]@{ lane = $laneKey; status = 'failed'; detail = 'invalid or incomplete lane contract' })
    }
}

$seenLaneInvocations = @{}
foreach ($laneKey in $laneRecords.Keys) {
    $record = $laneRecords[$laneKey]
    $identity = [string]$record.laneInvocationId
    if (-not [string]::IsNullOrWhiteSpace($identity) -and $seenLaneInvocations.ContainsKey($identity)) {
        $record.entry['status'] = 'failed'
        $record.entry['detail'] = 'duplicate lane invocation identity rejected'
        $firstRecord = $seenLaneInvocations[$identity]
        $firstRecord.entry['status'] = 'failed'
        $firstRecord.entry['detail'] = 'duplicate lane invocation identity rejected'
    } elseif (-not [string]::IsNullOrWhiteSpace($identity)) {
        $seenLaneInvocations[$identity] = $record
    }
}

$payloadManifestPath = Join-Path $ArtifactDirectory 'windows-payload-manifest.json'
$payloadPassed = $false
if (Test-Path -LiteralPath $payloadManifestPath -PathType Leaf) {
    try {
        $manifest = Get-Content -LiteralPath $payloadManifestPath -Raw | ConvertFrom-Json
        $windowsRecord = $laneRecords['windowsui']
        if ($null -eq $windowsRecord) {
            throw 'Windows lane record is missing.'
        }
        $manifestPaths = @($manifest.files | ForEach-Object { [string]$_.relativePath })
        $uniquePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($relativePath in $manifestPaths) {
            if ([string]::IsNullOrWhiteSpace($relativePath) -or
                $relativePath.Contains('\') -or
                $relativePath.StartsWith('/') -or
                (Test-FullyQualifiedPath $relativePath) -or
                $relativePath.Split('/').Contains('..') -or
                -not $uniquePaths.Add($relativePath)) {
                throw 'Windows manifest contains a duplicate or non-canonical relative path.'
            }
        }
        $payloadRootText = [string]$manifest.payloadRoot
        $executableText = [string]$manifest.executable
        if ($payloadRootText.Contains('\') -or
            $payloadRootText.StartsWith('/') -or
            (Test-FullyQualifiedPath $payloadRootText) -or
            $payloadRootText.Split('/').Contains('..') -or
            $executableText.Contains('\') -or
            $executableText.StartsWith('/') -or
            (Test-FullyQualifiedPath $executableText) -or
            $executableText.Split('/').Contains('..')) {
            throw 'Windows manifest root or executable is non-canonical.'
        }
        $payloadRoot = Get-CanonicalContainedPath -Root $repoRoot -Candidate (
            [IO.Path]::GetFullPath((Join-Path $repoRoot $payloadRootText)))
        $executable = Get-CanonicalContainedPath -Root $payloadRoot -Candidate (
            [IO.Path]::GetFullPath((Join-Path $payloadRoot $executableText)))
        $actual = Get-StrictPayloadSnapshot `
            -RepositoryRoot $repoRoot `
            -PayloadRoot $payloadRoot `
            -ExecutablePath $executable
        $expected = [ordered]@{
            payloadRoot = $payloadRootText
            executable = $executableText
            files = @($manifest.files)
        }
        Assert-StrictPayloadSnapshotsEqual -Expected $expected -Actual $actual
        $payloadPassed = $manifest.schema -ceq 'deep.survival.windows-payload-manifest.v2' -and
            $manifest.sourceCommitSha -ceq $commit -and
            $manifest.releaseInvocationId -ceq $ReleaseInvocationId -and
            $manifest.laneInvocationId -ceq $windowsRecord.laneInvocationId -and
            $manifest.postRunVerified -eq $true -and
            $windowsRecord.result.evidence -ceq 'windows-payload-manifest.json' -and
            $windowsRecord.result.evidenceSha256 -ceq (Get-Sha256Lower -Path $payloadManifestPath)
    } catch {
        $payloadPassed = $false
    }
}
$checks.Add([ordered]@{
    lane = 'windows-payload'
    status = $(if ($payloadPassed) { 'passed' } elseif (Test-Path -LiteralPath $payloadManifestPath) { 'failed' } else { 'not-run' })
    detail = 'exact canonical pre/post payload identity and lane binding'
})

$androidSummaryPath = Join-Path $ArtifactDirectory 'android-device-summary.json'
$androidSummaryPassed = $false
if (Test-Path -LiteralPath $androidSummaryPath -PathType Leaf) {
    try {
        $summary = Get-Content -LiteralPath $androidSummaryPath -Raw | ConvertFrom-Json
        $androidRecord = $laneRecords['androiddevice']
        if ($null -eq $androidRecord) {
            throw 'Android lane record is missing.'
        }
        $summaryCountersValid = [int]$summary.counters.total -eq (
                [int]$summary.counters.executed + [int]$summary.counters.skipped) -and
            [int]$summary.counters.executed -eq (
                [int]$summary.counters.passed + [int]$summary.counters.failed) -and
            [int]$summary.counters.executed -gt 0 -and
            [int]$summary.counters.skipped -eq 0 -and
            [int]$summary.counters.failed -eq 0
        if ([string]::IsNullOrWhiteSpace($AndroidLabPolicyPath) -or
            [string]::IsNullOrWhiteSpace($AndroidApkPath)) {
            throw 'Protected Android policy and exact APK are required for complete validation.'
        }
        $policyPath = Get-CanonicalContainedPath -Root $repoRoot -Candidate (
            [IO.Path]::GetFullPath($AndroidLabPolicyPath))
        Assert-NoReparsePointInPath -Root $repoRoot -Candidate $policyPath
        $policyRelative = (Get-RelativePathCompat -Root $repoRoot -Candidate $policyPath).Replace('\', '/')
        if (-not $policyRelative.StartsWith('.secrets/android-lab/', [StringComparison]::Ordinal)) {
            throw 'Release Android policy is outside the protected lab root.'
        }
        $policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
        $policyHash = Get-Sha256Lower -Path $policyPath
        $receiptRelative = [string]$policy.approval.receiptRelativePath
        if ($receiptRelative.Contains('\') -or
            -not $receiptRelative.StartsWith('.secrets/android-lab/', [StringComparison]::Ordinal) -or
            $receiptRelative.Split('/').Contains('..')) {
            throw 'Release approval receipt path is invalid.'
        }
        $receiptPath = Get-CanonicalContainedPath -Root $repoRoot -Candidate (
            [IO.Path]::GetFullPath((Join-Path $repoRoot $receiptRelative)))
        Assert-NoReparsePointInPath -Root $repoRoot -Candidate $receiptPath
        $policyValid = $policy.schema -ceq 'deep.survival.android-lab-policy.v1' -and
            $policy.provisioned -eq $true -and
            $policy.synthetic -eq $false -and
            $policy.sourceCommitSha -ceq $commit -and
            (Test-NonZeroSha256Value ([string]$policy.policyId)) -and
            $policy.approval.state -ceq 'approved' -and
            $policy.approval.approvedBy -ceq 'Mr. X' -and
            (Test-NonZeroSha256Value ([string]$policy.approval.receiptSha256)) -and
            (Get-Sha256Lower -Path $receiptPath) -ceq [string]$policy.approval.receiptSha256 -and
            $policy.application.packageId -ceq 'network.xpoint.deep.e2e' -and
            [int]$policy.application.versionCode -gt 0 -and
            (Test-SafeSummaryVersion ([string]$policy.application.versionName)) -and
            (Test-NonZeroSha256Value ([string]$policy.application.apkSha256)) -and
            (Test-NonZeroSha256Value ([string]$policy.application.signingCertificateSha256)) -and
            [string]$policy.device.class -in @('managed-emulator', 'managed-physical') -and
            [int]$policy.device.sdk -ge 26 -and [int]$policy.device.sdk -le 100 -and
            [string]$policy.tools.aapt.kind -in @('aapt', 'aapt2')
        foreach ($role in @('runner', 'adb', 'aapt', 'apksigner')) {
            $definition = $policy.tools.$role
            $relativeToolPath = [string]$definition.relativePath
            if ($relativeToolPath.Contains('\') -or
                -not $relativeToolPath.StartsWith('.secrets/android-lab/tools/', [StringComparison]::Ordinal) -or
                $relativeToolPath.Split('/').Contains('..') -or
                -not (Test-NonZeroSha256Value ([string]$definition.sha256)) -or
                -not (Test-SafeSummaryVersion ([string]$definition.version))) {
                $policyValid = $false
                break
            }
            $toolPath = Get-CanonicalContainedPath -Root $repoRoot -Candidate (
                [IO.Path]::GetFullPath((Join-Path $repoRoot $relativeToolPath)))
            Assert-NoReparsePointInPath -Root $repoRoot -Candidate $toolPath
            $expectedToolName = switch ($role) {
                'runner' { 'deep-android-runner.exe' }
                'adb' { 'adb.exe' }
                'aapt' { "$([string]$policy.tools.aapt.kind).exe" }
                'apksigner' { 'apksigner.bat' }
            }
            if ((Get-Sha256Lower -Path $toolPath) -cne [string]$definition.sha256 -or
                -not [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFileName($toolPath), $expectedToolName) -or
                $summary.toolchain.$role.sha256 -cne [string]$definition.sha256 -or
                $summary.toolchain.$role.version -cne [string]$definition.version) {
                $policyValid = $false
                break
            }
        }
        $apkPath = Get-CanonicalContainedPath -Root $repoRoot -Candidate ([IO.Path]::GetFullPath($AndroidApkPath))
        Assert-NoReparsePointInPath -Root $repoRoot -Candidate $apkPath
        $apkValid = (Get-Sha256Lower -Path $apkPath) -ceq [string]$policy.application.apkSha256
        $androidSummaryPassed = $summary.schema -ceq 'deep.survival.android-device-summary.v2' -and
            $summary.sourceCommitSha -ceq $commit -and
            $summary.releaseInvocationId -ceq $ReleaseInvocationId -and
            $summary.laneInvocationId -ceq $androidRecord.laneInvocationId -and
            $summary.status -ceq 'passed' -and
            $summary.labPolicy.synthetic -eq $false -and
            $summary.labPolicy.id -ceq [string]$policy.policyId -and
            $summary.labPolicy.sha256 -ceq $policyHash -and
            $summary.labPolicy.approvalReceiptSha256 -ceq [string]$policy.approval.receiptSha256 -and
            $policyValid -and
            $apkValid -and
            $summary.apk.packageId -ceq 'network.xpoint.deep.e2e' -and
            $summary.apk.packageId -ceq [string]$policy.application.packageId -and
            $summary.apk.sha256 -match '^[0-9a-f]{64}$' -and
            $summary.apk.sha256 -ceq [string]$policy.application.apkSha256 -and
            $summary.apk.signingCertificateSha256 -match '^[0-9a-f]{64}$' -and
            $summary.apk.signingCertificateSha256 -ceq [string]$policy.application.signingCertificateSha256 -and
            [string]$summary.apk.versionCode -ceq [string]$policy.application.versionCode -and
            [string]$summary.apk.versionName -ceq [string]$policy.application.versionName -and
            $summary.runner.sha256 -match '^[0-9a-f]{64}$' -and
            $summary.runner.sha256 -ceq [string]$policy.tools.runner.sha256 -and
            $summary.runner.version -ceq [string]$policy.tools.runner.version -and
            (Test-SafeSummaryVersion ([string]$summary.runner.version)) -and
            $summary.toolchain.aapt.kind -ceq [string]$policy.tools.aapt.kind -and
            $summary.device.serialSha256 -ceq (Get-TextSha256Lower -Value ([string]$policy.device.serial)) -and
            $summary.device.fingerprintSha256 -ceq (Get-TextSha256Lower -Value ([string]$policy.device.fingerprint)) -and
            $summary.device.productSha256 -ceq (Get-TextSha256Lower -Value ([string]$policy.device.product)) -and
            [int]$summary.device.sdk -eq [int]$policy.device.sdk -and
            [string]$summary.device.class -ceq [string]$policy.device.class -and
            $summary.device.dedicatedManaged -eq $true -and
            $summary.device.personalDataAbsent -eq $true -and
            $summary.device.productionPackageAbsentBefore -eq $true -and
            $summary.device.testPackageClearedBefore -eq $true -and
            $summary.device.testPackageRemovedAfter -eq $true -and
            $summary.junitSha256 -match '^[0-9a-f]{64}$' -and
            $summaryCountersValid -and
            $androidRecord.result.evidence -ceq 'android-device-summary.json' -and
            $androidRecord.result.evidenceSha256 -ceq (Get-Sha256Lower -Path $androidSummaryPath)
    } catch {
        $androidSummaryPassed = $false
    }
}
$checks.Add([ordered]@{
    lane = 'android-sanitized-summary'
    status = $(if ($androidSummaryPassed) { 'passed' } elseif (Test-Path -LiteralPath $androidSummaryPath) { 'failed' } else { 'not-run' })
    detail = 'allowlisted APK, runner, managed-device, JUnit, and invocation bindings'
})

$checks.Add([ordered]@{
    lane = 'source-tree'
    status = $(if ($sourceTreeClean) { 'passed' } else { 'failed' })
    detail = 'strict release evidence requires an exact clean commit'
})

$incomplete = @($checks | Where-Object { $_.status -ne 'passed' })
$attemptedFailure = @($checks | Where-Object { $_.status -eq 'failed' }).Count -gt 0
$productionReady = $incomplete.Count -eq 0
$overall = if ($productionReady) {
    'passed'
} elseif ($RequireComplete -or $attemptedFailure) {
    'blocked'
} else {
    'not-run'
}
[ordered]@{
    schema = 'deep.survival.strict-evidence-summary.v2'
    sourceCommitSha = $commit
    releaseInvocationId = $ReleaseInvocationId
    generatedAtUtc = $now.ToString('O')
    status = $overall
    productionReady = $productionReady
    blockers = @($incomplete | ForEach-Object { $_.lane })
    checks = $checks
} | ConvertTo-Json -Depth 7 |
    Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'strict-evidence-summary.json') -Encoding utf8

if ($RequireComplete -and -not $productionReady) {
    throw "Strict release evidence is incomplete or invalid: $($incomplete.lane -join ', ')."
}
