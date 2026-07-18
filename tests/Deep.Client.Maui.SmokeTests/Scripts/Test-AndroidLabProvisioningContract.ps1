$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ("deep-lab-provision-{0}" -f [Guid]::NewGuid().ToString('N'))
$source = Join-Path $sandbox 'protected-source'
$destination = Join-Path $repoRoot '.secrets\android-lab'
$verifier = Join-Path $repoRoot 'eng\Deep.AndroidLab.PolicyVerifier\Deep.AndroidLab.PolicyVerifier.csproj'
$signer = Join-Path $repoRoot 'tests\Deep.SyntheticPolicySigner\Deep.SyntheticPolicySigner.csproj'
$provisioner = Join-Path $repoRoot 'eng\Provision-AndroidLabPolicy.ps1'
$owner = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$junction = $null
$ancestorJunction = $null

function Protect-Tree([string]$Root) {
    foreach ($item in Get-ChildItem -LiteralPath $Root -File -Recurse -Force) {
        $item.IsReadOnly = $true
    }
    $icacls = Join-Path $env:SystemRoot 'System32\icacls.exe'
    & $icacls $Root '/inheritance:r' '/grant:r' "${owner}:F" '/T' '/C' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not protect synthetic fixture ACL inheritance.' }
}

New-Item -ItemType Directory -Force -Path (Join-Path $source 'tools') | Out-Null
try {
    if (Test-Path -LiteralPath $destination) {
        if (@(Get-ChildItem -LiteralPath $destination -Force).Count -ne 0) {
            throw 'Operator Android lab material exists; contract refuses to touch it.'
        }
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
    $policy.device.sdk = 35
    $policy.device.inventoryState = 'approved'
    $policy.device.inventoryApprovalReceiptSha256 = $policy.approval.receiptSha256
    $policy.application.versionCode = 42
    $policy.application.versionName = '1.2.3'
    $policy.application.apkSha256 = 'c' * 64
    $policy.application.signingCertificateSha256 = 'd' * 64
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
    $policyPath = & $provisioner -SourceRoot $source -ExpectedOwner $owner -MrXPublicKeySha256 $keyHash
    if (-not (Test-Path -LiteralPath $policyPath -PathType Leaf)) { throw 'Valid signed bundle was not provisioned.' }
    & $provisioner -Clean

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
    if (Test-Path -LiteralPath $destination) { & $provisioner -Clean }
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
}
