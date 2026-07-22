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
    [string]$AndroidLabPolicyPath,
    [string]$MrXPublicKeySha256,
    [switch]$AllowSyntheticLabPolicyForContractTests,
    [switch]$CaptureStubWelcomeFailure,
    [switch]$ValidateLiveConfigurationOnly
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop
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

function Test-StrictLiveUrl {
    param(
        [string]$Value,
        [switch]$RequireRootPath
    )
    $uri = $null
    if ([string]::IsNullOrWhiteSpace($Value) -or
        -not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri)) {
        return $false
    }
    if ([string]::IsNullOrWhiteSpace($uri.Host) -or
        -not [string]::IsNullOrEmpty($uri.UserInfo) -or
        -not [string]::IsNullOrEmpty($uri.Query) -or
        -not [string]::IsNullOrEmpty($uri.Fragment) -or
        ($RequireRootPath -and $uri.AbsolutePath -cne '/')) {
        return $false
    }
    if ($uri.Scheme -ceq 'https') {
        return $true
    }
    $literalAddress = $null
    return $uri.Scheme -ceq 'http' -and
        [Net.IPAddress]::TryParse($uri.DnsSafeHost, [ref]$literalAddress) -and
        [Net.IPAddress]::IsLoopback($literalAddress)
}

function Test-LiveConfiguration {
    $rawRouters = [Environment]::GetEnvironmentVariable('XNODE_URLS')
    $routerEntries = @(if ([string]::IsNullOrWhiteSpace($rawRouters)) {
        @()
    } else {
        @($rawRouters.Split(
            @(';', ',', "`n", "`r", "`t", ' '),
            [StringSplitOptions]::RemoveEmptyEntries) |
            ForEach-Object { $_.Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    })
    $parsedRouters = @($routerEntries | ForEach-Object {
        $separator = $_.IndexOf('|')
        if ($separator -le 0 -or $separator -eq $_.Length - 1) {
            return $null
        }
        $routerId = $_.Substring(0, $separator).Trim()
        $routerUrl = $_.Substring($separator + 1).Trim()
        if ($routerId -cnotmatch '^[0-9a-f]{64}$') {
            return $null
        }
        $uri = $null
        if (-not [Uri]::TryCreate($routerUrl, [UriKind]::Absolute, [ref]$uri) -or
            -not (Test-StrictLiveUrl -Value $routerUrl -RequireRootPath)) {
            return $null
        }
        [pscustomobject]@{ routerId = $routerId; url = $uri.AbsoluteUri }
    })
    $validRouters = $routerEntries.Count -eq 3 -and
        $parsedRouters.Count -eq 3 -and
        @($parsedRouters | Where-Object { $null -eq $_ }).Count -eq 0 -and
        @($parsedRouters | ForEach-Object { $_.routerId } | Sort-Object -Unique).Count -eq 3 -and
        @($parsedRouters | ForEach-Object { $_.url } | Sort-Object -Unique).Count -eq 3
    Add-Check 'routed-message-endpoint' $validRouters $(if ($validRouters) {
        'exactly three distinct pinned router identities and URLs'
    } else {
        'XNODE_URLS must contain exactly three distinct <64-lowerhex-routerId>|<https-or-loopback-http-url> entries'
    })
    $directStorageAbsent = -not (Has-Value 'DEEP_STORAGE_URL')
    Add-Check 'direct-storage-absent' $directStorageAbsent $(if ($directStorageAbsent) {
        'DEEP_STORAGE_URL is absent'
    } else {
        'DEEP_STORAGE_URL is forbidden in routed live evidence'
    })
    foreach ($name in @('DEEP_FILE_URL', 'DEEP_PUSH_URL', 'DEEP_CALL_SIGNALING_BASE_URL')) {
        $valid = Test-StrictLiveUrl -Value ([Environment]::GetEnvironmentVariable($name))
        Add-Check $name $valid $(if ($valid) {
            'configured with HTTPS or explicit loopback HTTP'
        } else {
            'required and must use HTTPS or explicit loopback HTTP'
        })
    }
}

if ($ValidateLiveConfigurationOnly -and
    ($Lane -cne 'LiveInfrastructure' -or $Bootstrap -cne 'live')) {
    throw 'ValidateLiveConfigurationOnly requires Lane=LiveInfrastructure and Bootstrap=live.'
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
    if ($raw -match '(?is)<!DOCTYPE|<!ENTITY|<\s*(system-out|system-err|attachments?|attachment)\b') {
        throw 'Android JUnit contains prohibited private or non-allowlisted content.'
    }
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create([IO.StringReader]::new($raw), $settings)
    $document = [Xml.XmlDocument]::new()
    $document.XmlResolver = $null
    $document.Load($reader)
    if ($null -ne $document.SelectSingleNode('//comment() | //processing-instruction()')) {
        throw 'Android JUnit comments and processing instructions are not allowlisted.'
    }
    $allowedAttributes = @{
        testsuites = @('name', 'tests', 'failures', 'errors', 'skipped', 'time')
        testsuite = @('name', 'id', 'package', 'tests', 'failures', 'errors', 'skipped', 'time')
        testcase = @('name', 'classname', 'time', 'status', 'assertions')
        properties = @()
        property = @('name', 'value')
    }
    foreach ($node in @($document.SelectNodes('//*'))) {
        $nodeName = $node.LocalName.ToLowerInvariant()
        if (-not $allowedAttributes.ContainsKey($nodeName)) {
            throw 'Android JUnit contains a non-allowlisted element.'
        }
        $isProperty = $nodeName -match '(?i)^property$'
        foreach ($attribute in @($node.Attributes)) {
            $attributeName = $attribute.LocalName.ToLowerInvariant()
            $value = [string]$attribute.Value
            if ($attribute.Prefix -or $attribute.NamespaceURI -or
                $attributeName -notin $allowedAttributes[$nodeName]) {
                throw 'Android JUnit contains a non-allowlisted attribute.'
            }
            $propertyNameIsSensitive = $isProperty -and
                $attributeName -eq 'name' -and
                $value -match '(?i)(password|passphrase|token|secret|mnemonic|seed|private.?key|authorization|bearer|recovery)'
            $sensitiveAttributeName = $attributeName -match '(?i)(password|passphrase|token|secret|mnemonic|seed|key|auth|bearer|recovery)'
            $sensitiveValue = $value -match '(?i)(password|passphrase|token|secret|mnemonic|seed\s+phrase|private\s+key|authorization|bearer|recovery)'
            $attachmentValue = $attributeName -match '(?i)(attachment|artifact|file|path)' -or
                $value -match '(?i)(attachment|artifact)'
            $absoluteWindowsPath = $value -match '(?i)(^|[\s="''])([a-z]:[\\/]|\\\\)'
            $absoluteUnixPath = $value -match '(^|[\s="'':(])/(?:$|[A-Za-z0-9._~-])'
            $absoluteUri = $value -match '(?i)[a-z][a-z0-9+.-]{0,31}:(?://|[^\s"''<>]+)'
            if ($propertyNameIsSensitive -or $sensitiveAttributeName -or
                $sensitiveValue -or $attachmentValue -or
                $absoluteWindowsPath -or $absoluteUnixPath -or $absoluteUri) {
                throw 'Android JUnit contains a prohibited property, path, URI, attachment, or sensitive value.'
            }
        }
        foreach ($child in @($node.ChildNodes | Where-Object {
            $_.NodeType -in @([Xml.XmlNodeType]::Text, [Xml.XmlNodeType]::CDATA)
        })) {
            $text = [string]$child.Value
            if (-not [string]::IsNullOrWhiteSpace($text)) {
                throw 'Android JUnit text output is not allowlisted.'
            }
        }
    }
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

function Test-CanonicalPolicyRelativePath {
    param([string]$Value)
    return -not [string]::IsNullOrWhiteSpace($Value) -and
        -not $Value.Contains('\') -and
        -not $Value.StartsWith('/') -and
        -not (Test-FullyQualifiedPath $Value) -and
        -not $Value.Split('/').Contains('..')
}

function Test-NonZeroSha256 {
    param([string]$Value)
    return $Value -match '^[0-9a-f]{64}$' -and $Value -ne ('0' * 64)
}

function Test-PrivacySafeVersion {
    param([string]$Value)
    return -not [string]::IsNullOrWhiteSpace($Value) -and
        $Value.Length -le 128 -and
        $Value -match '^[A-Za-z0-9][A-Za-z0-9 ._+(),-]*$' -and
        $Value -notmatch '(?i)(password|passphrase|token|secret|mnemonic|seed|private.?key|authorization|bearer|recovery)'
}

function Assert-PrivacySafeSerializedEvidence {
    param([Parameter(Mandatory)][string]$Serialized)
    if ($Serialized -match '(?i)([a-z]:[\\/]|\\\\)' -or
        $Serialized -match '(?i)[a-z][a-z0-9+.-]{1,31}://' -or
        $Serialized -match '(?i)"[^"]*(password|passphrase|token|secret|mnemonic|seed\s+phrase|private\s+key|authorization|bearer|recovery)[^"]*"' -or
        $Serialized -match '(?i)"[^"]*(attachment|artifact)[^"]*"' -or
        $Serialized -match '(?i)(:\s*|,\s*)"/(?:[^"]*)?"') {
        throw 'Sanitized Android evidence contains a prohibited path, URI, sensitive value, or attachment reference.'
    }
}

function Open-ReadOnlyLease {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ContainmentRoot
    )
    $canonical = Get-CanonicalContainedPath -Root $ContainmentRoot -Candidate ([IO.Path]::GetFullPath($Path))
    Assert-NoReparsePointInPath -Root $ContainmentRoot -Candidate $canonical
    $item = Get-Item -LiteralPath $canonical -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        -not (Test-Path -LiteralPath $canonical -PathType Leaf)) {
        throw 'Trusted lab file must be a regular non-reparse file.'
    }
    $stream = [IO.FileStream]::new(
        $canonical,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    return [pscustomobject]@{
        path = $canonical
        stream = $stream
        sha256 = Get-StreamSha256Lower -Stream $stream
    }
}

function Read-AndroidLabPolicy {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw 'A provisioned protected Android lab policy is required.'
    }
    $lease = Open-ReadOnlyLease -Path $Path -ContainmentRoot $repoRoot
    $signatureLeases = [Collections.Generic.List[object]]::new()
    try {
        $lease.stream.Position = 0
        $reader = [IO.StreamReader]::new($lease.stream, [Text.Encoding]::UTF8, $true, 4096, $true)
        try {
            $policy = $reader.ReadToEnd() | ConvertFrom-Json
        } finally {
            $reader.Dispose()
        }
        $relativePolicyPath = (Get-RelativePathCompat -Root $repoRoot -Candidate $lease.path).Replace('\', '/')
        $synthetic = $policy.synthetic -eq $true
        if ($policy.schema -cne 'deep.survival.android-lab-policy.v1' -or
            $policy.provisioned -ne $true -or
            $policy.sourceCommitSha -cne $sourceCommitSha -or
            -not (Test-NonZeroSha256 ([string]$policy.policyId)) -or
            $policy.application.packageId -cne 'network.xpoint.deep.e2e' -or
            -not (Test-NonZeroSha256 ([string]$policy.application.apkSha256)) -or
            -not (Test-NonZeroSha256 ([string]$policy.application.signingCertificateSha256)) -or
            [int]$policy.application.versionCode -le 0 -or
            -not (Test-PrivacySafeVersion ([string]$policy.application.versionName))) {
            throw 'Android lab policy schema, commit, identity, or application binding is invalid.'
        }
        if ($synthetic) {
            if (-not $AllowSyntheticLabPolicyForContractTests -or
                $policy.approval.state -cne 'synthetic-contract-fixture') {
                throw 'Synthetic Android lab policy is accepted only by the explicit contract-test lane.'
            }
        } else {
            if (-not $relativePolicyPath.StartsWith('.secrets/android-lab/', [StringComparison]::Ordinal) -or
                $policy.approval.state -cne 'approved' -or
                $policy.approval.approvedBy -cne 'Mr. X' -or
                -not (Test-CanonicalPolicyRelativePath ([string]$policy.approval.receiptRelativePath)) -or
                -not ([string]$policy.approval.receiptRelativePath).StartsWith('.secrets/android-lab/', [StringComparison]::Ordinal) -or
                -not (Test-NonZeroSha256 ([string]$policy.approval.receiptSha256))) {
                throw 'Protected Android lab policy lacks the required Mr. X approval receipt.'
            }
            if (-not (Test-NonZeroSha256 $MrXPublicKeySha256) -or
                [string]$policy.signature.algorithm -cne 'Ed25519' -or
                [string]$policy.signature.publicKeySha256 -cne $MrXPublicKeySha256 -or
                -not (Test-NonZeroSha256 ([string]$policy.signature.signedPayloadSha256))) {
                throw 'Protected Android lab policy lacks a pinned Mr. X Ed25519 signature.'
            }
            foreach ($field in @('publicKeyRelativePath', 'signatureRelativePath', 'signedPayloadRelativePath')) {
                $relative = [string]$policy.signature.$field
                if (-not (Test-CanonicalPolicyRelativePath $relative) -or
                    -not $relative.StartsWith('.secrets/android-lab/', [StringComparison]::Ordinal)) {
                    throw 'Protected Android signature path is invalid.'
                }
                $signatureLeases.Add((Open-ReadOnlyLease -Path (Join-Path $repoRoot $relative) -ContainmentRoot $repoRoot))
            }
            if ($signatureLeases[0].sha256 -cne $MrXPublicKeySha256 -or
                $signatureLeases[2].sha256 -cne [string]$policy.signature.signedPayloadSha256) {
                throw 'Protected Android signature material hash is invalid.'
            }
            $signedPayload = Get-Content -LiteralPath $signatureLeases[2].path -Raw | ConvertFrom-Json
            if ([string]$signedPayload.schema -cne [string]$policy.schema -or
                $signedPayload.provisioned -ne $policy.provisioned -or
                $signedPayload.synthetic -ne $policy.synthetic -or
                [string]$signedPayload.sourceCommitSha -cne [string]$policy.sourceCommitSha -or
                [string]$signedPayload.policyId -cne [string]$policy.policyId -or
                ($signedPayload.approval | ConvertTo-Json -Depth 8 -Compress) -cne ($policy.approval | ConvertTo-Json -Depth 8 -Compress) -or
                ($signedPayload.tools | ConvertTo-Json -Depth 8 -Compress) -cne ($policy.tools | ConvertTo-Json -Depth 8 -Compress) -or
                ($signedPayload.device | ConvertTo-Json -Depth 8 -Compress) -cne ($policy.device | ConvertTo-Json -Depth 8 -Compress) -or
                ($signedPayload.application | ConvertTo-Json -Depth 8 -Compress) -cne ($policy.application | ConvertTo-Json -Depth 8 -Compress)) {
                throw 'Mr. X signed payload does not exactly bind policy inventory.'
            }
            dotnet run --project (Join-Path $PSScriptRoot 'Deep.AndroidLab.PolicyVerifier\Deep.AndroidLab.PolicyVerifier.csproj') `
                -c Release --no-build --no-restore -- verify $signatureLeases[0].path $signatureLeases[1].path $signatureLeases[2].path
            if ($LASTEXITCODE -ne 0) {
                throw 'Mr. X Ed25519 signature verification failed.'
            }
        }
        $requiredDeviceClass = if ($synthetic) { 'managed-emulator' } else { 'physical-managed-dedicated' }
        $requiredInventoryState = if ($synthetic) { 'synthetic-contract-fixture' } else { 'approved' }
        $requiredInventoryApprover = if ($synthetic) { 'Synthetic Contract Fixture' } else { 'Mr. X' }
        if ([string]::IsNullOrWhiteSpace([string]$policy.device.serial) -or
            [string]::IsNullOrWhiteSpace([string]$policy.device.fingerprint) -or
            [string]::IsNullOrWhiteSpace([string]$policy.device.product) -or
            [string]::IsNullOrWhiteSpace([string]$policy.device.hardware) -or
            [string]::IsNullOrWhiteSpace([string]$policy.device.model) -or
            [string]$policy.device.kernelQemu -cne $(if ($synthetic) { '1' } else { '0' }) -or
            [int]$policy.device.sdk -lt 26 -or [int]$policy.device.sdk -gt 100 -or
            [string]$policy.device.class -cne $requiredDeviceClass -or
            $policy.device.dedicated -ne $true -or
            [string]$policy.device.inventoryState -cne $requiredInventoryState -or
            [string]$policy.device.inventoryApprovedBy -cne $requiredInventoryApprover -or
            ($synthetic -and [string]$policy.device.inventoryApprovalReceiptSha256 -cne ('0' * 64)) -or
            (-not $synthetic -and [string]$policy.device.inventoryApprovalReceiptSha256 -cne
                [string]$policy.approval.receiptSha256)) {
            throw 'Android lab policy device identity is incomplete or invalid.'
        }
        return [pscustomobject]@{
            policy = $policy
            lease = $lease
            path = $lease.path
            relativePath = $relativePolicyPath
            sha256 = $lease.sha256
            synthetic = $synthetic
            signatureLeases = $signatureLeases
        }
    } catch {
        foreach ($signatureLease in $signatureLeases) {
            $signatureLease.stream.Dispose()
        }
        $lease.stream.Dispose()
        throw
    }
}

function Get-TrustedToolVersion {
    param(
        [Parameter(Mandatory)][string]$Role,
        [Parameter(Mandatory)][string]$Path
    )
    try {
        $rawOutput = if ($Role -eq 'runner') {
            @(& $Path '--version' 2>&1)
        } else {
            @(& $Path 'version' 2>&1)
        }
        $commandSucceeded = $?
        $commandExitCode = $LASTEXITCODE
    } catch {
        throw "Trusted $Role version query could not start."
    }
    $lines = @($rawOutput | ForEach-Object { [string]$_ } | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_)
        })
    if (-not $commandSucceeded -or $commandExitCode -ne 0) {
        $safeExitCode = if ($null -eq $commandExitCode) { 'unset' } else { [string][int]$commandExitCode }
        throw "Trusted $Role version query returned a failure status ($safeExitCode)."
    }
    if ($lines.Count -eq 0) {
        throw "Trusted $Role version query returned no output."
    }
    $version = $lines[0].Trim()
    if (-not (Test-PrivacySafeVersion $version)) {
        throw "Trusted $Role returned a non-allowlisted version string."
    }
    return $version
}

function Open-TrustedPolicyTool {
    param(
        [Parameter(Mandatory)][string]$Role,
        [Parameter(Mandatory)][object]$Definition,
        [string]$CallerPath,
        [Parameter(Mandatory)][bool]$Synthetic
    )
    $stage = 'definition'
    $lease = $null
    try {
        $relativePath = [string]$Definition.relativePath
        if (-not (Test-CanonicalPolicyRelativePath $relativePath) -or
            -not (Test-NonZeroSha256 ([string]$Definition.sha256)) -or
            -not (Test-PrivacySafeVersion ([string]$Definition.version))) {
            throw "definition"
        }
        if (-not $Synthetic -and
            -not $relativePath.StartsWith('.secrets/android-lab/tools/', [StringComparison]::Ordinal)) {
            throw "location"
        }
        $candidate = [IO.Path]::GetFullPath((Join-Path $repoRoot $relativePath))
        if (-not [string]::IsNullOrWhiteSpace($CallerPath) -and
            -not [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($CallerPath), $candidate)) {
            throw "caller-binding"
        }
        $stage = 'lease'
        $lease = Open-ReadOnlyLease -Path $candidate -ContainmentRoot $repoRoot
        $stage = 'hash'
        if ($lease.sha256 -cne [string]$Definition.sha256) {
            throw "hash"
        }
        # PowerShell script fixtures cannot be executed while their source is leased on
        # Windows. Release the first hash-verified handle for the version probe, then
        # reacquire and re-hash the exact policy binary. The second handle is retained
        # through execution, so a probe-time substitution cannot become the used tool.
        $lease.stream.Dispose()
        $lease = $null
        $stage = 'version-query'
        $version = Get-TrustedToolVersion -Role $Role -Path $candidate
        $stage = 'version-binding'
        if ($version -cne [string]$Definition.version) {
            throw "version-binding"
        }
        $stage = 'lease-after-version'
        $lease = Open-ReadOnlyLease -Path $candidate -ContainmentRoot $repoRoot
        $stage = 'hash-after-version'
        if ($lease.sha256 -cne [string]$Definition.sha256) {
            throw "hash-after-version"
        }
        return [pscustomobject]@{
            role = $Role
            path = $lease.path
            sha256 = $lease.sha256
            version = $version
            stream = $lease.stream
        }
    } catch {
        if ($null -ne $lease) {
            $lease.stream.Dispose()
        }
        $category = if ($_.Exception.Message -match '^Trusted [a-z0-9]+ version query (could not start|returned a failure status( \((-?[0-9]+|unset)\))?|returned no output)\.$') {
            $_.Exception.Message
        } elseif ($_.Exception.Message -match '^Trusted [a-z0-9]+ returned a non-allowlisted version string\.$') {
            $_.Exception.Message
        } else {
            "Trusted $Role policy validation failed"
        }
        throw "$category at $stage."
    }
}

function Open-ValidatedApkLease {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedSha256
    )
    $lease = Open-ReadOnlyLease -Path $Path -ContainmentRoot $repoRoot
    try {
        if ($lease.stream.Length -lt 22 -or $lease.sha256 -cne $ExpectedSha256) {
            throw 'APK is too small, unbound, or has an unexpected SHA-256.'
        }
        $lease.stream.Position = 0
        $archive = [IO.Compression.ZipArchive]::new(
            $lease.stream,
            [IO.Compression.ZipArchiveMode]::Read,
            $true)
        try {
            $entryNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            $hasManifest = $false
            foreach ($entry in $archive.Entries) {
                $name = $entry.FullName.Replace('\', '/')
                if ([string]::IsNullOrWhiteSpace($name) -or
                    $name.StartsWith('/') -or
                    $name.Split('/').Contains('..') -or
                    -not $entryNames.Add($name)) {
                    throw 'APK archive contains a duplicate or non-canonical entry.'
                }
                if ($name -ceq 'AndroidManifest.xml') {
                    $hasManifest = $true
                }
            }
            if (-not $hasManifest -or $archive.Entries.Count -eq 0) {
                throw 'APK archive is missing AndroidManifest.xml.'
            }
        } finally {
            $archive.Dispose()
            $lease.stream.Position = 0
        }
        return $lease
    } catch {
        $lease.stream.Dispose()
        throw
    }
}

function Invoke-AdbSingleValue {
    param(
        [Parameter(Mandatory)][string]$Adb,
        [Parameter(Mandatory)][string[]]$Arguments
    )
    $lines = @(& $Adb @Arguments 2>$null | ForEach-Object { ([string]$_).Trim() } | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_)
    })
    if ($LASTEXITCODE -ne 0 -or $lines.Count -ne 1) {
        throw 'Managed-device identity query failed.'
    }
    return $lines[0]
}

function Get-TextSha256Lower {
    param([Parameter(Mandatory)][string]$Value)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString(
            $algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value)))).Replace('-', '').ToLowerInvariant()
    } finally {
        $algorithm.Dispose()
    }
}

if ($Lane -eq 'WindowsUi') {
    $isWindowsHost = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
    Add-Check 'host-os' $isWindowsHost $(if ($isWindowsHost) { 'Windows' } else { 'Windows is required' })
    $interactiveDesktop = $false
    if ($isWindowsHost) {
        try {
            Add-Type -Path (Join-Path $PSScriptRoot 'WindowsInteractiveSessionProbe.cs') -ErrorAction Stop
            $interactiveDesktop = [Deep.Client.Maui.StrictGates.WindowsInteractiveSessionProbe]::IsCurrentSessionUnlocked()
        } catch {
            $interactiveDesktop = $false
        }
    }
    Add-Check 'interactive-desktop' $interactiveDesktop $(if ($interactiveDesktop) {
        'current Windows session is unlocked'
    } else {
        'an unlocked interactive Windows session is required for real input'
    })
    $appExists = -not [string]::IsNullOrWhiteSpace($AppPath) -and (Test-Path -LiteralPath $AppPath -PathType Leaf)
    Add-Check 'maui-executable' $appExists $(if ($appExists) { 'present' } else { 'AppPath is required and must exist' })
    $appPayloadContained = $false
    $canonicalAppPath = $null
    $payloadRoot = $null
    $preRunSnapshot = $null
    $preRunLease = $null
    if ($isWindowsHost -and $appExists) {
        try {
            $canonicalAppPath = Get-CanonicalContainedPath -Root $repoRoot -Candidate ([IO.Path]::GetFullPath($AppPath))
            $payloadRoot = [IO.Path]::GetDirectoryName($canonicalAppPath)
            Assert-NoReparsePointInPath -Root $repoRoot -Candidate $payloadRoot
            Assert-NoReparsePointInPath -Root $payloadRoot -Candidate $canonicalAppPath
            $preRunLease = Open-StrictPayloadLease `
                -RepositoryRoot $repoRoot `
                -PayloadRoot $payloadRoot `
                -ExecutablePath $canonicalAppPath
            $preRunSnapshot = $preRunLease.snapshot
            if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
                [string]$preRunLease.executablePath,
                $canonicalAppPath)) {
                throw 'The executable path is not the exact leased payload file.'
            }
            $appPayloadContained = $true
        } catch {
            Close-StrictPayloadLease -Lease $preRunLease
            $preRunLease = $null
            $appPayloadContained = $false
        }
    }
    Add-Check 'maui-payload-root' $appPayloadContained $(if ($appPayloadContained) { 'repository-local' } else { 'AppPath must be inside the repository' })
    Add-Check 'windows-payload-read-leases' ($null -ne $preRunLease) $(if ($null -ne $preRunLease) {
        'all payload files opened read-only with write/delete sharing denied'
    } else {
        'the platform could not guarantee strict payload read leases'
    })
    Add-Check 'artifact-root' $true 'canonical repository child without reparse points'
    if ($Bootstrap -eq 'live') {
        Test-LiveConfiguration
    } else {
        Add-Check 'debug-bootstrap' $true 'explicit stub; accepted only by Debug app code'
    }

    $blocked = @($checks.Where({ $_.status -ne 'passed' })).Count -gt 0
    Write-Preflight $(if ($blocked) { 'failed' } else { 'ready' }) | Out-Null
    if ($blocked) {
        Close-StrictPayloadLease -Lease $preRunLease
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
        Close-StrictPayloadLease -Lease $preRunLease
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
    Close-StrictPayloadLease -Lease $preRunLease
    $preRunLease = $null
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
    $androidTrustStreams = [Collections.Generic.List[IO.Stream]]::new()
    function Close-AndroidTrustStreams {
        foreach ($stream in $androidTrustStreams) {
            $stream.Dispose()
        }
        $androidTrustStreams.Clear()
    }

    $policyContext = $null
    $policy = $null
    try {
        $policyContext = Read-AndroidLabPolicy -Path $AndroidLabPolicyPath
        $policy = $policyContext.policy
        $androidTrustStreams.Add($policyContext.lease.stream)
        foreach ($signatureLease in $policyContext.signatureLeases) {
            $androidTrustStreams.Add($signatureLease.stream)
        }
        if (-not $policyContext.synthetic) {
            $receiptPath = [IO.Path]::GetFullPath((
                Join-Path $repoRoot ([string]$policy.approval.receiptRelativePath)))
            $receiptLease = Open-ReadOnlyLease -Path $receiptPath -ContainmentRoot $repoRoot
            if ($receiptLease.sha256 -cne [string]$policy.approval.receiptSha256) {
                $receiptLease.stream.Dispose()
                throw 'Mr. X approval receipt hash does not match the protected policy.'
            }
            $androidTrustStreams.Add($receiptLease.stream)
        }
    } catch {
        $policyContext = $null
        $policy = $null
    }
    Add-Check 'android-lab-policy' ($null -ne $policyContext) $(if ($null -ne $policyContext) {
        $(if ($policyContext.synthetic) { 'synthetic contract fixture; never release-valid' } else { 'protected, commit-bound, and Mr. X approved' })
    } else {
        'a provisioned protected commit-bound policy is required; the checked-in template cannot pass'
    })

    $trustedTools = @{}
    $toolchainStage = 'policy-unavailable'
    if ($null -ne $policyContext) {
        try {
            $toolchainStage = 'validate-aapt-kind'
            if ([string]$policy.tools.aapt.kind -notin @('aapt', 'aapt2')) {
                throw 'The lab policy must select exactly aapt or aapt2.'
            }
            $definitions = [ordered]@{
                runner = $policy.tools.runner
                adb = $policy.tools.adb
                aapt = $policy.tools.aapt
                apksigner = $policy.tools.apksigner
            }
            $callerPaths = @{
                runner = $AndroidRunner
                adb = $AdbPath
                aapt = $AaptPath
                apksigner = $ApkSignerPath
            }
            foreach ($role in $definitions.Keys) {
                $toolchainStage = "open-$role"
                try {
                    $trustedTool = Open-TrustedPolicyTool `
                        -Role $role `
                        -Definition $definitions[$role] `
                        -CallerPath $callerPaths[$role] `
                        -Synthetic $policyContext.synthetic
                } catch {
                    $toolchainStage = "$toolchainStage/$($_.Exception.Message)"
                    throw
                }
                $trustedTools[$role] = $trustedTool
                $androidTrustStreams.Add($trustedTool.stream)
            }
            if (-not $policyContext.synthetic) {
                $toolchainStage = 'validate-role-basenames'
                $expectedAaptName = "$([string]$policy.tools.aapt.kind).exe"
                if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
                        [IO.Path]::GetFileName([string]$trustedTools.aapt.path),
                        $expectedAaptName) -or
                    -not [StringComparer]::OrdinalIgnoreCase.Equals(
                        [IO.Path]::GetFileName([string]$trustedTools.runner.path),
                        'deep-android-runner.exe') -or
                    -not [StringComparer]::OrdinalIgnoreCase.Equals(
                        [IO.Path]::GetFileName([string]$trustedTools.adb.path),
                        'adb.exe') -or
                    -not [StringComparer]::OrdinalIgnoreCase.Equals(
                        [IO.Path]::GetFileName([string]$trustedTools.apksigner.path),
                        'apksigner.bat')) {
                    throw 'Protected lab tool basenames do not match their fixed roles.'
                }
            }
        } catch {
            foreach ($tool in @($trustedTools.Values)) {
                $tool.stream.Dispose()
                $null = $androidTrustStreams.Remove($tool.stream)
            }
            $trustedTools = @{}
        }
    }
    $toolchainTrusted = $trustedTools.Count -eq 4
    Add-Check 'android-trusted-toolchain' $toolchainTrusted $(if ($toolchainTrusted) {
        'runner, adb, aapt/aapt2, and apksigner exact paths, hashes, and versions matched policy'
    } else {
        "the complete protected toolchain did not match policy; stage=$toolchainStage"
    })

    $apkExists = -not [string]::IsNullOrWhiteSpace($ApkPath) -and (Test-Path -LiteralPath $ApkPath -PathType Leaf)
    Add-Check 'apk' $apkExists $(if ($apkExists) { 'present' } else { 'ApkPath is required and must exist' })
    $canonicalApkPath = $null
    $apkLease = $null
    $apkMetadata = $null
    if ($apkExists -and $toolchainTrusted -and $null -ne $policyContext) {
        try {
            $canonicalApkPath = Get-CanonicalContainedPath -Root $repoRoot -Candidate ([IO.Path]::GetFullPath($ApkPath))
            Assert-NoReparsePointInPath -Root $repoRoot -Candidate $canonicalApkPath
            $apkLease = Open-ValidatedApkLease `
                -Path $canonicalApkPath `
                -ExpectedSha256 ([string]$policy.application.apkSha256)
            $androidTrustStreams.Add($apkLease.stream)
            $apkMetadata = Get-AndroidApkMetadata `
                -Aapt $trustedTools.aapt.path `
                -Apk $canonicalApkPath `
                -ApkSigner $trustedTools.apksigner.path
        } catch {
            if ($null -ne $apkLease) {
                $apkLease.stream.Dispose()
                $null = $androidTrustStreams.Remove($apkLease.stream)
            }
            $apkMetadata = $null
        }
    }
    $testPackage = $null -ne $apkMetadata -and
        $apkMetadata.apkSha256 -ceq [string]$policy.application.apkSha256 -and
        $apkMetadata.packageId -ceq [string]$policy.application.packageId -and
        [string]$apkMetadata.versionCode -ceq [string]$policy.application.versionCode -and
        [string]$apkMetadata.versionName -ceq [string]$policy.application.versionName -and
        $apkMetadata.signingCertificateSha256 -ceq [string]$policy.application.signingCertificateSha256
    Add-Check 'apk-archive-and-identity' $testPackage $(if ($testPackage) {
        'valid leased APK archive exactly matched policy hash, E2E package, version, and signing certificate'
    } else {
        'APK is invalid/non-ZIP or does not match the protected E2E application policy'
    })

    $adb = if ($toolchainTrusted) { [string]$trustedTools.adb.path } else { $null }
    $serial = if ($null -ne $policy) { [string]$policy.device.serial } else { '' }
    $attached = $false
    $devicePolicyMatched = $false
    $productionPackageAbsent = $false
    $deviceFingerprint = ''
    $deviceProduct = ''
    $deviceHardware = ''
    $deviceModel = ''
    $deviceKernelQemu = ''
    $deviceSdk = 0
    $deviceClass = ''
    if ($null -ne $adb -and $null -ne $policy) {
        try {
            if (-not [string]::IsNullOrWhiteSpace($AndroidSerial) -and
                $AndroidSerial -cne $serial) {
                throw 'Caller-selected serial does not match policy.'
            }
            $deviceLines = @(& $adb devices | Select-Object -Skip 1 | Where-Object { $_ -match '\S+\s+device$' })
            $attached = @($deviceLines | Where-Object {
                $_ -match ("^{0}\s+device$" -f [regex]::Escape($serial))
            }).Count -eq 1
            if (-not $attached) {
                throw 'The policy device is not uniquely attached.'
            }
            $reportedSerial = Invoke-AdbSingleValue -Adb $adb -Arguments @('-s', $serial, 'get-serialno')
            $deviceFingerprint = Invoke-AdbSingleValue -Adb $adb -Arguments @(
                '-s', $serial, 'shell', 'getprop', 'ro.build.fingerprint')
            $deviceProduct = Invoke-AdbSingleValue -Adb $adb -Arguments @(
                '-s', $serial, 'shell', 'getprop', 'ro.product.name')
            $deviceHardware = Invoke-AdbSingleValue -Adb $adb -Arguments @(
                '-s', $serial, 'shell', 'getprop', 'ro.hardware')
            $deviceModel = Invoke-AdbSingleValue -Adb $adb -Arguments @(
                '-s', $serial, 'shell', 'getprop', 'ro.product.model')
            $deviceKernelQemu = Invoke-AdbSingleValue -Adb $adb -Arguments @(
                '-s', $serial, 'shell', 'getprop', 'ro.kernel.qemu')
            $sdkText = Invoke-AdbSingleValue -Adb $adb -Arguments @(
                '-s', $serial, 'shell', 'getprop', 'ro.build.version.sdk')
            $characteristics = Invoke-AdbSingleValue -Adb $adb -Arguments @(
                '-s', $serial, 'shell', 'getprop', 'ro.build.characteristics')
            $deviceSdk = [int]$sdkText
            $emulatorPattern = '(?i)(emulator|sdk[_-]?gphone|generic|goldfish|ranchu|vbox|qemu|simulator)'
            $emulatorDetected = $characteristics -match $emulatorPattern -or
                $deviceProduct -match $emulatorPattern -or
                $deviceHardware -match $emulatorPattern -or
                $deviceModel -match $emulatorPattern -or
                $deviceKernelQemu -cne '0'
            $deviceClass = if ($emulatorDetected) {
                'managed-emulator'
            } else {
                'physical-managed-dedicated'
            }
            $devicePolicyMatched = $reportedSerial -ceq $serial -and
                $deviceFingerprint -ceq [string]$policy.device.fingerprint -and
                $deviceProduct -ceq [string]$policy.device.product -and
                $deviceHardware -ceq [string]$policy.device.hardware -and
                $deviceModel -ceq [string]$policy.device.model -and
                $deviceKernelQemu -ceq [string]$policy.device.kernelQemu -and
                $deviceSdk -eq [int]$policy.device.sdk -and
                $deviceClass -ceq [string]$policy.device.class
            if (-not $devicePolicyMatched) {
                throw 'Attached device identity does not match policy.'
            }
            $productionPackages = @(& $adb -s $serial shell pm list packages 'network.xpoint.deep' 2>$null)
            $packageQuerySucceeded = $LASTEXITCODE -eq 0
            $productionPackageAbsent = $packageQuerySucceeded -and
                @($productionPackages | Where-Object { $_.Trim() -ceq 'package:network.xpoint.deep' }).Count -eq 0
            if (-not $productionPackageAbsent) {
                throw 'Production package is present or query failed.'
            }
            if ($ConfigureAdbReverse) {
                foreach ($port in $ReversePort | Sort-Object -Unique) {
                    & $adb -s $serial reverse "tcp:$port" "tcp:$port" | Out-Null
                    if ($LASTEXITCODE -ne 0) {
                        throw 'Managed device rejected adb reverse.'
                    }
                }
            }
        } catch {
            $devicePolicyMatched = $false
            $productionPackageAbsent = $false
        }
    }
    Add-Check 'android-managed-device-policy' $devicePolicyMatched $(if ($devicePolicyMatched) {
        'serial, fingerprint, product, hardware, model, qemu state, SDK, and dedicated physical class exactly matched protected inventory policy'
    } else {
        'caller switches and runner self-attestation cannot satisfy managed-device trust'
    })
    Add-Check 'production-package-absent' $productionPackageAbsent $(if ($productionPackageAbsent) {
        'production package is absent; wrapper will not inspect or modify it'
    } else {
        'production package is installed or independently queried device state failed'
    })

    $blocked = @($checks.Where({ $_.status -ne 'passed' })).Count -gt 0
    Write-Preflight $(if ($blocked) { 'failed' } else { 'ready' }) | Out-Null
    if ($blocked) {
        Close-AndroidTrustStreams
        Write-LaneResult 'blocked' $null 'device execution did not run'
        exit 2
    }

    $runnerRoot = Join-Path $ArtifactDirectory ("android-run-{0}-{1}" -f $ReleaseInvocationId, $laneInvocationId)
    $rawRoot = Join-Path $runnerRoot 'quarantine\raw'
    New-Item -ItemType Directory -Force -Path $rawRoot | Out-Null
    $runnerResultPath = Join-Path $rawRoot 'runner-result.json'
    $junitPath = Join-Path $rawRoot 'android-device.junit.xml'
    $canonicalRunner = [string]$trustedTools.runner.path
    $runnerSha256 = [string]$trustedTools.runner.sha256
    $runnerVersion = [string]$trustedTools.runner.version
    $deviceFingerprintSha256 = Get-TextSha256Lower -Value $deviceFingerprint
    $deviceProductSha256 = Get-TextSha256Lower -Value $deviceProduct
    $deviceHardwareSha256 = Get-TextSha256Lower -Value $deviceHardware
    $deviceModelSha256 = Get-TextSha256Lower -Value $deviceModel
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
        '--runner-version', $runnerVersion,
        '--lab-policy-id', [string]$policy.policyId,
        '--lab-policy-sha256', $policyContext.sha256,
        '--device-fingerprint-sha256', $deviceFingerprintSha256,
        '--device-product-sha256', $deviceProductSha256,
        '--device-hardware-sha256', $deviceHardwareSha256,
        '--device-model-sha256', $deviceModelSha256,
        '--device-kernel-qemu', $deviceKernelQemu,
        '--device-sdk', [string]$deviceSdk,
        '--device-class', $deviceClass
    )
    & $canonicalRunner @runnerArguments
    $runnerExit = $LASTEXITCODE
    if (-not (Test-Path -LiteralPath $runnerResultPath -PathType Leaf)) {
        Close-AndroidTrustStreams
        Add-Check 'android-runner-contract' $false 'runner result missing'
        Write-Preflight 'failed' | Out-Null
        Write-LaneResult 'failed' $null 'Android runner result JSON missing'
        exit 3
    }
    if (-not (Test-Path -LiteralPath $junitPath -PathType Leaf)) {
        Close-AndroidTrustStreams
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
        $bindingContract = $runnerResult.schema -eq 'deep.survival.android-runner-result.v3' -and
            $runnerResult.sourceCommitSha -ceq $sourceCommitSha -and
            $runnerResult.releaseInvocationId -ceq $ReleaseInvocationId -and
            $runnerResult.laneInvocationId -ceq $laneInvocationId -and
            $runnerResult.labPolicyId -ceq [string]$policy.policyId -and
            $runnerResult.labPolicySha256 -ceq $policyContext.sha256 -and
            $runnerResult.apkSha256 -ceq $apkMetadata.apkSha256 -and
            $runnerResult.packageId -ceq $apkMetadata.packageId -and
            [string]$runnerResult.versionCode -ceq [string]$apkMetadata.versionCode -and
            [string]$runnerResult.versionName -ceq [string]$apkMetadata.versionName -and
            $runnerResult.signingCertificateSha256 -ceq $apkMetadata.signingCertificateSha256 -and
            $runnerResult.runnerSha256 -ceq $runnerSha256 -and
            $runnerResult.runnerVersion -ceq $runnerVersion -and
            $runnerResult.junitSha256 -ceq $junitSha256 -and
            $runnerResult.device.serial -ceq $serial -and
            $runnerResult.device.fingerprintSha256 -ceq $deviceFingerprintSha256 -and
            $runnerResult.device.productSha256 -ceq $deviceProductSha256 -and
            $runnerResult.device.hardwareSha256 -ceq $deviceHardwareSha256 -and
            $runnerResult.device.modelSha256 -ceq $deviceModelSha256 -and
            $runnerResult.device.kernelQemu -ceq $deviceKernelQemu -and
            [int]$runnerResult.device.sdk -eq $deviceSdk -and
            $runnerResult.device.class -ceq $deviceClass -and
            $runnerResult.device.dedicatedManaged -eq $true -and
            $runnerResult.device.personalDataAbsent -eq $true -and
            $runnerResult.device.productionPackageAbsentBefore -eq $true -and
            $runnerResult.device.testPackageClearedBefore -eq $true -and
            $runnerResult.device.testPackageRemovedAfter -eq $true
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
            $sanitizedSummary = [ordered]@{
                schema = 'deep.survival.android-device-summary.v2'
                sourceCommitSha = $sourceCommitSha
                releaseInvocationId = $ReleaseInvocationId
                laneInvocationId = $laneInvocationId
                generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
                status = 'passed'
                labPolicy = [ordered]@{
                    id = [string]$policy.policyId
                    sha256 = $policyContext.sha256
                    synthetic = [bool]$policyContext.synthetic
                    approvalReceiptSha256 = $(if ($policyContext.synthetic) {
                        '0' * 64
                    } else {
                        [string]$policy.approval.receiptSha256
                    })
                }
                toolchain = [ordered]@{
                    runner = [ordered]@{ sha256 = $trustedTools.runner.sha256; version = $trustedTools.runner.version }
                    adb = [ordered]@{ sha256 = $trustedTools.adb.sha256; version = $trustedTools.adb.version }
                    aapt = [ordered]@{
                        kind = [string]$policy.tools.aapt.kind
                        sha256 = $trustedTools.aapt.sha256
                        version = $trustedTools.aapt.version
                    }
                    apksigner = [ordered]@{ sha256 = $trustedTools.apksigner.sha256; version = $trustedTools.apksigner.version }
                }
                apk = [ordered]@{
                    sha256 = $apkMetadata.apkSha256
                    packageId = $apkMetadata.packageId
                    versionCode = $apkMetadata.versionCode
                    versionName = $apkMetadata.versionName
                    signingCertificateSha256 = $apkMetadata.signingCertificateSha256
                }
                runner = [ordered]@{
                    sha256 = $runnerSha256
                    version = $runnerVersion
                }
                device = [ordered]@{
                    serialSha256 = Get-TextSha256Lower -Value $serial
                    fingerprintSha256 = $deviceFingerprintSha256
                    productSha256 = $deviceProductSha256
                    hardwareSha256 = $deviceHardwareSha256
                    modelSha256 = $deviceModelSha256
                    kernelQemuIsZero = $deviceKernelQemu -ceq '0'
                    sdk = $deviceSdk
                    class = $deviceClass
                    dedicatedManaged = $true
                    personalDataAbsent = $true
                    productionPackageAbsentBefore = $true
                    testPackageClearedBefore = $true
                    testPackageRemovedAfter = $true
                }
                counters = $counters
                junitSha256 = $junitSha256
            } | ConvertTo-Json -Depth 7
            Assert-PrivacySafeSerializedEvidence -Serialized $sanitizedSummary
            $sanitizedSummary | Set-Content -LiteralPath $sanitizedSummaryPath -Encoding utf8
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
    Close-AndroidTrustStreams
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
if ($ValidateLiveConfigurationOnly) {
    Write-LaneResult 'passed' $null 'live configuration contract'
    exit 0
}

$env:DEEP_STRICT_LIVE = '1'
$trxName = 'strict-live-infrastructure.trx'
dotnet test (Join-Path $repoRoot 'tests\Deep.Client.Maui.ViewModels.Tests\Deep.Client.Maui.ViewModels.Tests.csproj') `
    --configuration Release `
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
