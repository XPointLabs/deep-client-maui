$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
. (Join-Path $repoRoot 'eng\StrictLane.Common.ps1')
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ("deep-lab-provision-{0}" -f [Guid]::NewGuid().ToString('N'))
$source = Join-Path $sandbox 'protected-source'
$destination = Join-Path $repoRoot '.secrets\android-lab'
$verifier = Join-Path $repoRoot 'eng\Deep.AndroidLab.PolicyVerifier\Deep.AndroidLab.PolicyVerifier.csproj'
$signer = Join-Path $repoRoot 'tests\Deep.SyntheticPolicySigner\Deep.SyntheticPolicySigner.csproj'
$provisioner = Join-Path $repoRoot 'eng\Provision-AndroidLabPolicy.ps1'
$owner = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$junction = $null
$ancestorJunction = $null
$boundRoot = Join-Path $repoRoot ("artifacts\cross-policy-contract-{0}" -f [Guid]::NewGuid().ToString('N'))
$evidenceRoot = Join-Path $boundRoot 'evidence'
$boundApk = Join-Path $boundRoot 'network.xpoint.deep.e2e-Signed.apk'
$boundWindows = Join-Path $boundRoot 'Deep.Client.Maui.exe'
$boundFixture = Join-Path $boundRoot 'fixture.bin'
$releaseInvocationId = '77777777777777777777777777777777'
$ownsDestination = $false
$ownedDestinationTreeDigest = $null

if ((Test-Path -LiteralPath $destination) -and
    @(Get-ChildItem -LiteralPath $destination -Force).Count -ne 0) {
    Write-Output 'SKIP: protected operator Android lab material is already provisioned.'
    return
}

function Protect-Tree([string]$Root) {
    foreach ($item in Get-ChildItem -LiteralPath $Root -File -Recurse -Force) {
        $item.IsReadOnly = $true
    }
    $icacls = Join-Path $env:SystemRoot 'System32\icacls.exe'
    & $icacls $Root '/inheritance:r' '/grant:r' "${owner}:F" '/T' '/C' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not protect synthetic fixture ACL inheritance.' }
}

function Get-TextSha256Lower([string]$Value) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value)))).Replace('-', '').ToLowerInvariant()
    } finally { $sha.Dispose() }
}

function Get-TreeDigest([string]$Root) {
    $canonical = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $manifest = foreach ($file in Get-ChildItem -LiteralPath $canonical -File -Recurse -Force |
        Sort-Object FullName) {
        $relative = $file.FullName.Substring($canonical.Length + 1).Replace('\', '/')
        "$relative`t$($file.Length)`t$((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant())"
    }
    return Get-TextSha256Lower (($manifest -join "`n") + "`n")
}

function Remove-TestOwnedDestination {
    if (-not $script:ownsDestination) { return }
    if (-not (Test-Path -LiteralPath $destination)) {
        $script:ownsDestination = $false
        $script:ownedDestinationTreeDigest = $null
        return
    }
    if ([string]::IsNullOrWhiteSpace($script:ownedDestinationTreeDigest) -or
        (Get-TreeDigest $destination) -cne $script:ownedDestinationTreeDigest) {
        throw 'Refusing to clean Android lab material that is not the exact test-owned tree.'
    }
    & $provisioner -Clean
    if (Test-Path -LiteralPath $destination) {
        throw 'Test-owned Android lab destination was not removed.'
    }
    $script:ownsDestination = $false
    $script:ownedDestinationTreeDigest = $null
}

New-Item -ItemType Directory -Force -Path (Join-Path $source 'tools') | Out-Null
New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null
[IO.File]::WriteAllBytes($boundApk, [byte[]](1..64))
[IO.File]::WriteAllText($boundWindows, 'synthetic-windows-binary')
[IO.File]::WriteAllText($boundFixture, 'synthetic-attachment')
try {
    if (Test-Path -LiteralPath $destination) {
        Remove-Item -LiteralPath $destination -Force
    }
    dotnet build $verifier -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Pinned production verifier build failed.' }
    foreach ($name in @('deep-android-runner.exe', 'adb.exe', 'aapt.exe', 'apksigner.bat')) {
        [IO.File]::WriteAllText((Join-Path $source "tools\$name"), "synthetic-$name")
    }
    [IO.File]::WriteAllText((Join-Path $source 'approval.receipt'), 'synthetic approval')
    $policy = Get-Content (Join-Path $repoRoot 'eng\policies\android-lab-policy.template.json') -Raw | ConvertFrom-Json
    $policy.provisioned = $true
    $policy.sourceCommitSha = (& git -C $repoRoot rev-parse HEAD).Trim()
    $policy.policyId = 'b' * 64
    $policy.approval.state = 'approved'
    $policy.approval.receiptSha256 = (Get-FileHash (Join-Path $source 'approval.receipt') -Algorithm SHA256).Hash.ToLowerInvariant()
    $policy.device.serial = 'physical-serial'
    $policy.device.fingerprint = 'vendor/device/release'
    $policy.device.product = 'physical_product'
    $policy.device.hardware = 'physical_hardware'
    $policy.device.model = 'Physical Model'
    $policy.device.characteristics = 'nosdcard'
    $policy.device.sdk = 35
    $policy.device.inventoryState = 'approved'
    $policy.device.inventoryApprovalReceiptSha256 = $policy.approval.receiptSha256
    $policy.application.versionCode = 42
    $policy.application.versionName = '1.2.3'
    $policy.application.apkRelativePath = (Get-RelativePathCompat -Root $repoRoot -Candidate $boundApk).Replace('\', '/')
    $policy.application.apkSizeBytes = (Get-Item $boundApk).Length
    $policy.application.apkSha256 = (Get-FileHash $boundApk -Algorithm SHA256).Hash.ToLowerInvariant()
    $policy.application.signingCertificateSha256 = 'd' * 64
    $policy.crossPlatform.windowsExecutableRelativePath = (Get-RelativePathCompat -Root $repoRoot -Candidate $boundWindows).Replace('\', '/')
    $policy.crossPlatform.windowsExecutableSha256 = (Get-FileHash $boundWindows -Algorithm SHA256).Hash.ToLowerInvariant()
    $boundWindowsOutput = Split-Path -Parent $boundWindows
    $policy.crossPlatform.windowsOutputDirectoryRelativePath = (Get-RelativePathCompat -Root $repoRoot -Candidate $boundWindowsOutput).Replace('\', '/')
    $policy.crossPlatform.windowsOutputTreeSha256 = Get-TreeDigest $boundWindowsOutput
    foreach ($role in @('runner', 'adb', 'aapt', 'apksigner')) {
        $definition = $policy.tools.$role
        $toolRelative = ([string]$definition.relativePath).Substring('.secrets/android-lab/'.Length)
        $definition.sha256 = (Get-FileHash (Join-Path $source $toolRelative) -Algorithm SHA256).Hash.ToLowerInvariant()
        $definition.version = "Synthetic $role 1.0"
    }
    [ordered]@{
        schema = $policy.schema; provisioned = $policy.provisioned; synthetic = $policy.synthetic
        sourceCommitSha = $policy.sourceCommitSha; policyId = $policy.policyId; approval = $policy.approval
        tools = $policy.tools; device = $policy.device; application = $policy.application
        crossPlatform = $policy.crossPlatform
    } | ConvertTo-Json -Depth 8 -Compress | Set-Content (Join-Path $source 'signed-payload.json') -Encoding utf8
    dotnet run --project $signer -c Release -- `
        (Join-Path $source 'mr-x-public-key.bin') (Join-Path $source 'policy.signature') `
        (Join-Path $source 'signed-payload.json')
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic Ed25519 fixture generation failed.' }
    $keyHash = (Get-FileHash (Join-Path $source 'mr-x-public-key.bin') -Algorithm SHA256).Hash.ToLowerInvariant()
    $policy.signature.publicKeySha256 = $keyHash
    $policy.signature.signedPayloadSha256 = (Get-FileHash (Join-Path $source 'signed-payload.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    $policy | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $source 'approved-policy.json') -Encoding utf8
    Protect-Tree $source

    $zeroRejected = $false
    try { & $provisioner -SourceRoot $source -ExpectedOwner $owner -MrXPublicKeySha256 ('0' * 64) } catch { $zeroRejected = $true }
    if (-not $zeroRejected) { throw 'Zero public-key pin was accepted.' }
    $expectedDestinationTreeDigest = Get-TreeDigest $source
    $policyPath = & $provisioner -SourceRoot $source -ExpectedOwner $owner -MrXPublicKeySha256 $keyHash
    if (-not (Test-Path -LiteralPath $policyPath -PathType Leaf)) { throw 'Valid signed bundle was not provisioned.' }
    if ((Get-TreeDigest $destination) -cne $expectedDestinationTreeDigest) {
        throw 'Provisioned Android lab tree does not exactly match the test-owned source.'
    }
    $ownsDestination = $true
    $ownedDestinationTreeDigest = $expectedDestinationTreeDigest

    $crossResult = [ordered]@{
        schema = 'deep.strict-cross-platform-ui.v2'
        status = 'passed'
        sourceCommit = $policy.sourceCommitSha
        releaseInvocationId = $releaseInvocationId
        invocationId = '88888888888888888888888888888888'
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        androidPackage = $policy.application.packageId
        androidVersionCode = [string]$policy.application.versionCode
        androidVersion = $policy.application.versionName
        cleanupCompleted = $true
        apkSizeBytes = [string]$policy.application.apkSizeBytes
        apkSha256 = $policy.application.apkSha256
        apkSigningDigest = $policy.application.signingCertificateSha256
        windowsExeSha256 = $policy.crossPlatform.windowsExecutableSha256
        windowsOutputTreeSha256 = $policy.crossPlatform.windowsOutputTreeSha256
        fixtureSha256 = (Get-FileHash $boundFixture -Algorithm SHA256).Hash.ToLowerInvariant()
        approvedPolicySha256 = (Get-FileHash $policyPath -Algorithm SHA256).Hash.ToLowerInvariant()
        policyId = $policy.policyId
        approvalReceiptSha256 = $policy.approval.receiptSha256
        mrXPublicKeySha256 = $keyHash
        adbSha256 = $policy.tools.adb.sha256
        aaptSha256 = $policy.tools.aapt.sha256
        apksignerSha256 = $policy.tools.apksigner.sha256
        adbVersionHash = Get-TextSha256Lower $policy.tools.adb.version
        aaptVersionHash = Get-TextSha256Lower $policy.tools.aapt.version
        apksignerVersionHash = Get-TextSha256Lower $policy.tools.apksigner.version
        androidSerialHash = Get-TextSha256Lower $policy.device.serial
        androidFingerprintHash = Get-TextSha256Lower $policy.device.fingerprint
        androidModelHash = Get-TextSha256Lower $policy.device.model
        androidProductHash = Get-TextSha256Lower $policy.device.product
        androidHardwareHash = Get-TextSha256Lower $policy.device.hardware
        androidCharacteristicsHash = Get-TextSha256Lower $policy.device.characteristics
        androidSdk = [string]$policy.device.sdk
    }
    $resultPath = Join-Path $evidenceRoot 'cross-platform-ui-result.json'
    $originalCrossResultText = $crossResult | ConvertTo-Json -Depth 8
    $originalCrossResultText | Set-Content $resultPath -Encoding utf8
    function Invoke-CrossValidator {
        & (Join-Path $repoRoot 'eng\Test-StrictClientEvidence.ps1') `
            -ReleaseInvocationId $releaseInvocationId -ArtifactDirectory $evidenceRoot `
            -AndroidApkPath $boundApk -MrXPublicKeySha256 $keyHash `
            -CrossPlatformPolicyPath $policyPath -CrossPlatformWindowsExePath $boundWindows `
            -CrossPlatformAttachmentFixturePath $boundFixture
        return Get-Content (Join-Path $evidenceRoot 'strict-evidence-summary.json') -Raw | ConvertFrom-Json
    }
    $summary = Invoke-CrossValidator
    if (($summary.checks | Where-Object lane -eq 'cross-platform-ui').status -ne 'passed') {
        throw 'Valid signed cross-platform inventory was not accepted.'
    }

    $originalProvisionedPolicy = [IO.File]::ReadAllBytes($policyPath)
    $originalProvisionedPolicyText = Get-Content -LiteralPath $policyPath -Raw
    $trustMutations = @(
        'sourceCommitSha', 'policyId', 'approval.state', 'approval.receiptSha256',
        'signature.publicKeySha256',
        'tools.adb.relativePath', 'tools.adb.sha256', 'tools.adb.version', 'tools.adb.versionArguments',
        'tools.aapt.relativePath', 'tools.aapt.sha256', 'tools.aapt.version', 'tools.aapt.versionArguments',
        'tools.apksigner.relativePath', 'tools.apksigner.sha256', 'tools.apksigner.version', 'tools.apksigner.versionArguments',
        'application.packageId', 'application.versionCode', 'application.versionName',
        'application.apkRelativePath', 'application.apkSizeBytes', 'application.apkSha256',
        'application.signingCertificateSha256',
        'device.serial', 'device.fingerprint', 'device.model', 'device.product', 'device.hardware',
        'device.sdk', 'device.characteristics', 'device.kernelQemu', 'device.class',
        'device.dedicated', 'device.inventoryState', 'device.inventoryApprovedBy',
        'crossPlatform.windowsExecutableRelativePath', 'crossPlatform.windowsExecutableSha256',
        'crossPlatform.windowsOutputDirectoryRelativePath', 'crossPlatform.windowsOutputTreeSha256')
    foreach ($mutation in $trustMutations) {
        (Get-Item $policyPath -Force).IsReadOnly = $false
        $mutated = $originalProvisionedPolicyText | ConvertFrom-Json
        switch -Wildcard ($mutation) {
            'sourceCommitSha' { $mutated.sourceCommitSha = 'f' * 40 }
            'policyId' { $mutated.policyId = 'f' * 64 }
            'approval.state' { $mutated.approval.state = 'pending' }
            'approval.receiptSha256' { $mutated.approval.receiptSha256 = 'f' * 64 }
            'signature.publicKeySha256' { $mutated.signature.publicKeySha256 = 'f' * 64 }
            'tools.adb.*' { $field = $mutation.Split('.')[-1]; $mutated.tools.adb.$field = $(if ($field -eq 'versionArguments') { @('mutated') } else { 'f' * 64 }) }
            'tools.aapt.*' { $field = $mutation.Split('.')[-1]; $mutated.tools.aapt.$field = $(if ($field -eq 'versionArguments') { @('mutated') } else { 'f' * 64 }) }
            'tools.apksigner.*' { $field = $mutation.Split('.')[-1]; $mutated.tools.apksigner.$field = $(if ($field -eq 'versionArguments') { @('mutated') } else { 'f' * 64 }) }
            'application.packageId' { $mutated.application.packageId = 'mutated.package' }
            'application.versionCode' { $mutated.application.versionCode = 999 }
            'application.versionName' { $mutated.application.versionName = 'mutated' }
            'application.apkRelativePath' { $mutated.application.apkRelativePath = 'artifacts/mutated.apk' }
            'application.apkSizeBytes' { $mutated.application.apkSizeBytes = 999 }
            'application.apkSha256' { $mutated.application.apkSha256 = 'f' * 64 }
            'application.signingCertificateSha256' { $mutated.application.signingCertificateSha256 = 'f' * 64 }
            'device.serial' { $mutated.device.serial = 'mutated' }
            'device.fingerprint' { $mutated.device.fingerprint = 'mutated' }
            'device.model' { $mutated.device.model = 'mutated' }
            'device.product' { $mutated.device.product = 'mutated' }
            'device.hardware' { $mutated.device.hardware = 'mutated' }
            'device.sdk' { $mutated.device.sdk = 99 }
            'device.characteristics' { $mutated.device.characteristics = 'mutated' }
            'device.kernelQemu' { $mutated.device.kernelQemu = '1' }
            'device.class' { $mutated.device.class = 'mutated' }
            'device.dedicated' { $mutated.device.dedicated = $false }
            'device.inventoryState' { $mutated.device.inventoryState = 'mutated' }
            'device.inventoryApprovedBy' { $mutated.device.inventoryApprovedBy = 'mutated' }
            'crossPlatform.windowsExecutableRelativePath' { $mutated.crossPlatform.windowsExecutableRelativePath = 'artifacts/mutated.exe' }
            'crossPlatform.windowsExecutableSha256' { $mutated.crossPlatform.windowsExecutableSha256 = 'f' * 64 }
            'crossPlatform.windowsOutputDirectoryRelativePath' { $mutated.crossPlatform.windowsOutputDirectoryRelativePath = 'artifacts' }
            'crossPlatform.windowsOutputTreeSha256' { $mutated.crossPlatform.windowsOutputTreeSha256 = 'f' * 64 }
        }
        $mutated | ConvertTo-Json -Depth 8 | Set-Content $policyPath -Encoding utf8
        (Get-Item $policyPath -Force).IsReadOnly = $true
        $summary = Invoke-CrossValidator
        if (($summary.checks | Where-Object lane -eq 'cross-platform-ui').status -ne 'failed') {
            throw "Signed trust-field mutation was accepted: $mutation"
        }
        (Get-Item $policyPath -Force).IsReadOnly = $false
        [IO.File]::WriteAllBytes($policyPath, $originalProvisionedPolicy)
        (Get-Item $policyPath -Force).IsReadOnly = $true
    }
    $evidenceMutations = [ordered]@{
        schema = 'mutated'
        status = 'failed'
        sourceCommit = ('f' * 40)
        releaseInvocationId = '99999999999999999999999999999999'
        invocationId = 'not-an-invocation'
        generatedAtUtc = '2000-01-01T00:00:00.0000000Z'
        androidPackage = 'mutated.package'
        androidVersionCode = '999'
        androidVersion = 'mutated'
        cleanupCompleted = $false
        apkSizeBytes = '999'
        apkSha256 = ('f' * 64)
        apkSigningDigest = ('f' * 64)
        windowsExeSha256 = ('f' * 64)
        windowsOutputTreeSha256 = ('f' * 64)
        fixtureSha256 = ('f' * 64)
        approvedPolicySha256 = ('f' * 64)
        policyId = ('f' * 64)
        approvalReceiptSha256 = ('f' * 64)
        mrXPublicKeySha256 = ('f' * 64)
        adbSha256 = ('f' * 64)
        aaptSha256 = ('f' * 64)
        apksignerSha256 = ('f' * 64)
        adbVersionHash = ('f' * 64)
        aaptVersionHash = ('f' * 64)
        apksignerVersionHash = ('f' * 64)
        androidSerialHash = ('f' * 64)
        androidFingerprintHash = ('f' * 64)
        androidModelHash = ('f' * 64)
        androidProductHash = ('f' * 64)
        androidHardwareHash = ('f' * 64)
        androidCharacteristicsHash = ('f' * 64)
        androidSdk = '99'
    }
    foreach ($mutation in $evidenceMutations.GetEnumerator()) {
        $mutatedResult = $originalCrossResultText | ConvertFrom-Json
        $mutatedResult.($mutation.Key) = $mutation.Value
        $mutatedResult | ConvertTo-Json -Depth 8 | Set-Content $resultPath -Encoding utf8
        $summary = Invoke-CrossValidator
        if (($summary.checks | Where-Object lane -eq 'cross-platform-ui').status -ne 'failed') {
            throw "Direct cross-platform evidence mutation was accepted: $($mutation.Key)"
        }
    }
    foreach ($removedField in @(
        'apkSizeBytes', 'apkSha256', 'apkSigningDigest', 'windowsExeSha256',
        'windowsOutputTreeSha256',
        'approvalReceiptSha256', 'mrXPublicKeySha256', 'androidFingerprintHash'))
    {
        $mutatedResult = $originalCrossResultText | ConvertFrom-Json
        $mutatedResult.PSObject.Properties.Remove($removedField)
        $mutatedResult | ConvertTo-Json -Depth 8 | Set-Content $resultPath -Encoding utf8
        $summary = Invoke-CrossValidator
        if (($summary.checks | Where-Object lane -eq 'cross-platform-ui').status -ne 'failed') {
            throw "Removed cross-platform evidence field was accepted: $removedField"
        }
    }
    $originalCrossResultText | Set-Content $resultPath -Encoding utf8
    Remove-TestOwnedDestination

    Get-ChildItem $source -File -Recurse -Force | ForEach-Object { $_.IsReadOnly = $false }
    $flippedPolicy = Get-Content (Join-Path $source 'approved-policy.json') -Raw | ConvertFrom-Json
    $flippedPolicy.synthetic = $true
    $flippedPolicy | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $source 'approved-policy.json') -Encoding utf8
    Protect-Tree $source
    $semanticFlipRejected = $false
    try { & $provisioner -SourceRoot $source -ExpectedOwner $owner -MrXPublicKeySha256 $keyHash } catch { $semanticFlipRejected = $true }
    if (-not $semanticFlipRejected) { throw 'Unsigned synthetic gate-field flip was accepted.' }

    Get-ChildItem $source -File -Recurse -Force | ForEach-Object { $_.IsReadOnly = $false }
    $policy.synthetic = $false
    $policy | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $source 'approved-policy.json') -Encoding utf8
    [IO.File]::WriteAllText((Join-Path $source 'extra.exe'), 'unbound')
    Protect-Tree $source
    $extraRejected = $false
    try { & $provisioner -SourceRoot $source -ExpectedOwner $owner -MrXPublicKeySha256 $keyHash } catch { $extraRejected = $true }
    if (-not $extraRejected) { throw 'Unbound extra executable was accepted.' }
    Get-ChildItem $source -File -Recurse -Force | ForEach-Object { $_.IsReadOnly = $false }
    Remove-Item -LiteralPath (Join-Path $source 'extra.exe') -Force
    Protect-Tree $source

    & (Join-Path $env:SystemRoot 'System32\icacls.exe') $source `
        '/grant' '*S-1-5-32-545:(OI)(CI)M' '/T' '/C' | Out-Null
    $writerRejected = $false
    try { & $provisioner -SourceRoot $source -ExpectedOwner $owner -MrXPublicKeySha256 $keyHash } catch { $writerRejected = $true }
    if (-not $writerRejected) { throw 'Untrusted SID writer was accepted.' }
    & (Join-Path $env:SystemRoot 'System32\icacls.exe') $source '/remove:g' '*S-1-5-32-545' '/T' '/C' | Out-Null
    Protect-Tree $source

    $ancestorJunction = Join-Path $sandbox 'ancestor-link'
    New-Item -ItemType Junction -Path $ancestorJunction -Target $sandbox | Out-Null
    $ancestorRejected = $false
    try {
        & $provisioner -SourceRoot (Join-Path $ancestorJunction 'protected-source') `
            -ExpectedOwner $owner -MrXPublicKeySha256 $keyHash
    } catch { $ancestorRejected = $true }
    if (-not $ancestorRejected) { throw 'Reparse-point source ancestor was accepted.' }
    [IO.Directory]::Delete($ancestorJunction)
    $ancestorJunction = $null

    Get-ChildItem $source -File -Recurse -Force | ForEach-Object { $_.IsReadOnly = $false }
    $outside = Join-Path $sandbox 'outside'
    New-Item -ItemType Directory -Path $outside | Out-Null
    $junction = Join-Path $source 'linked-directory'
    New-Item -ItemType Junction -Path $junction -Target $outside | Out-Null
    $reparseRejected = $false
    try { & $provisioner -SourceRoot $source -ExpectedOwner $owner -MrXPublicKeySha256 $keyHash } catch { $reparseRejected = $true }
    if (-not $reparseRejected) { throw 'Directory reparse point was accepted.' }
} finally {
    Remove-TestOwnedDestination
    if (-not [string]::IsNullOrWhiteSpace($junction) -and (Test-Path -LiteralPath $junction)) {
        [IO.Directory]::Delete($junction)
    }
    if (-not [string]::IsNullOrWhiteSpace($ancestorJunction) -and (Test-Path -LiteralPath $ancestorJunction)) {
        [IO.Directory]::Delete($ancestorJunction)
    }
    if (Test-Path -LiteralPath $sandbox) {
        & (Join-Path $env:SystemRoot 'System32\icacls.exe') $sandbox '/grant:r' "${owner}:(OI)(CI)F" '/T' '/C' | Out-Null
        Get-ChildItem $sandbox -File -Recurse -Force -ErrorAction SilentlyContinue |
            ForEach-Object { try { $_.IsReadOnly = $false } catch {} }
        Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $boundRoot) {
        Remove-Item $boundRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
