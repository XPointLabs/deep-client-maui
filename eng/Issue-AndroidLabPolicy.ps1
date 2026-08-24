[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProtectedSourceRoot,
    [Parameter(Mandatory)][string]$MrXPrivateKeyPath,
    [Parameter(Mandatory)][string]$MrXPublicKeyPath,
    [Parameter(Mandatory)][string]$AndroidSerial,
    [Parameter(Mandatory)][string]$ApkPath,
    [Parameter(Mandatory)][string]$WindowsExecutablePath,
    [Parameter(Mandatory)][string]$RunnerPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Get-ExactFile([string]$Path, [string]$Label, [Nullable[long]]$Length) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not [IO.Path]::IsPathRooted($full) -or
        -not (Test-Path -LiteralPath $full -PathType Leaf) -or
        ((Get-Item -Force -LiteralPath $full).Attributes -band
            [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        ($null -ne $Length -and (Get-Item -Force -LiteralPath $full).Length -ne $Length)) {
        throw "$Label is not an exact regular file."
    }
    return $full
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-ExactToolVersion([string]$Path, [object[]]$Arguments, [string]$Role) {
    if ($Arguments.Count -lt 1 -or $Arguments.Count -gt 4 -or
        @($Arguments | Where-Object { [string]$_ -cnotmatch '^[A-Za-z0-9._-]+$' }).Count -ne 0) {
        throw "$Role version arguments are outside the closed issuer grammar."
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = [Diagnostics.ProcessStartInfo]::new()
    $process.StartInfo.FileName = $Path
    $process.StartInfo.Arguments = (($Arguments | ForEach-Object { [string]$_ }) -join ' ')
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    try {
        if (-not $process.Start()) { throw "$Role version process did not start." }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) {
            try { $process.Kill() } catch { }
            throw "$Role version process exceeded its bounded deadline."
        }
        if (-not [Threading.Tasks.Task]::WaitAll(@($stdout, $stderr), 15000) -or
            $process.ExitCode -ne 0) {
            throw "$Role version process failed or did not close its output."
        }
        $version = ($stdout.Result + $stderr.Result).Trim()
        if ([string]::IsNullOrWhiteSpace($version) -or $version.Length -gt 8192) {
            throw "$Role version output is empty or unbounded."
        }
        return $version
    } finally {
        $process.Dispose()
    }
}

function Get-RelativeRepositoryPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = $repoRoot.TrimEnd('\') + '\'
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Policy-bound application files must be inside the repository.'
    }
    return $full.Substring($prefix.Length).Replace('\', '/')
}

function Assert-NoReparseTree([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if (Test-Path -LiteralPath $current) {
            if (((Get-Item -Force -LiteralPath $current).Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Android lab policy path traverses a reparse point.'
            }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -ceq $current) { break }
        $current = $parent
    }
    foreach ($item in Get-ChildItem -Force -Recurse -LiteralPath $Path) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Android lab policy tree contains a reparse point.'
        }
    }
}

function Assert-ProtectedPrivateKey([string]$Path) {
    if (-not (Get-Item -Force -LiteralPath $Path).IsReadOnly) {
        throw 'Mr. X private key must be read-only.'
    }
    $allowed = @(
        [Security.Principal.WindowsIdentity]::GetCurrent().User.Value,
        'S-1-5-18',
        'S-1-5-32-544')
    $acl = Get-Acl -LiteralPath $Path
    if (-not $acl.AreAccessRulesProtected) {
        throw 'Mr. X private key ACL inheritance must be disabled.'
    }
    foreach ($rule in $acl.Access) {
        $sid = $rule.IdentityReference.Translate(
            [Security.Principal.SecurityIdentifier]).Value
        $writes = $rule.FileSystemRights -band (
            [Security.AccessControl.FileSystemRights]::WriteData -bor
            [Security.AccessControl.FileSystemRights]::AppendData -bor
            [Security.AccessControl.FileSystemRights]::Delete -bor
            [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
            [Security.AccessControl.FileSystemRights]::TakeOwnership)
        if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            $writes -ne 0 -and $sid -notin $allowed) {
            throw 'Mr. X private key grants write access outside the trusted SID set.'
        }
    }
}

function Write-Utf8NoBom([string]$Path, [string]$Value) {
    [IO.File]::WriteAllText($Path, $Value, [Text.UTF8Encoding]::new($false))
}

function Get-TreeDigest([string]$Root) {
    $canonical = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $canonical -PathType Container) -or
        ((Get-Item -Force -LiteralPath $canonical).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Tree digest root must be an existing regular directory.'
    }
    $entries = @(Get-ChildItem -LiteralPath $canonical -Recurse -Force)
    if (@($entries | Where-Object {
        ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    }).Count -ne 0) {
        throw 'Tree digest input must not contain reparse points.'
    }
    $lines = foreach ($file in Get-ChildItem -LiteralPath $canonical -File -Recurse -Force |
        Sort-Object FullName) {
        $relative = $file.FullName.Substring($canonical.Length + 1).Replace('\', '/')
        "$relative`t$($file.Length)`t$(Get-Sha256 $file.FullName)"
    }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($lines -join "`n") + "`n")
    try {
        $hasher = [Security.Cryptography.SHA256]::Create()
        try {
            return ([BitConverter]::ToString($hasher.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
        } finally { $hasher.Dispose() }
    } finally { [Array]::Clear($bytes, 0, $bytes.Length) }
}

function Remove-OwnedTree([string]$Path, [string]$ExpectedLeaf) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    if ((Split-Path -Leaf $Path) -cne $ExpectedLeaf) {
        throw 'Refusing to remove an Android lab tree with an unexpected name.'
    }
    Assert-NoReparseTree $Path
    Get-ChildItem -LiteralPath $Path -File -Recurse -Force |
        ForEach-Object { $_.IsReadOnly = $false }
    Remove-Item -LiteralPath $Path -Recurse -Force
}

$source = [IO.Path]::GetFullPath($ProtectedSourceRoot).TrimEnd('\')
if (-not (Test-Path -LiteralPath $source -PathType Container)) {
    throw 'Existing protected Android lab source is required.'
}
Assert-NoReparseTree $source
$privateKey = Get-ExactFile $MrXPrivateKeyPath 'Mr. X private key' 64
$publicKey = Get-ExactFile $MrXPublicKeyPath 'Mr. X public key' 32
Assert-ProtectedPrivateKey $privateKey
$apk = Get-ExactFile $ApkPath 'Android APK' $null
$windowsExe = Get-ExactFile $WindowsExecutablePath 'Windows executable' $null
$runner = Get-ExactFile $RunnerPath 'Android runner' $null
$commit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -cnotmatch '^[0-9a-f]{40}$' -or
    @(& git -C $repoRoot status --porcelain=v1 --untracked-files=all).Count -ne 0) {
    throw 'Android lab policy issuance requires one clean committed source tree.'
}

$stage = Join-Path (Split-Path -Parent $source) '.protected-source.stage'
$backup = Join-Path (Split-Path -Parent $source) '.protected-source.backup'
if ((Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $source)) {
    [IO.Directory]::Move($backup, $source)
}
if (Test-Path -LiteralPath $stage) {
    Remove-OwnedTree $stage '.protected-source.stage'
}
[IO.Directory]::CreateDirectory($stage) | Out-Null
$published = $false
try {
    Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $stage -Recurse -Force
    Get-ChildItem -LiteralPath $stage -File -Recurse -Force |
        ForEach-Object { $_.IsReadOnly = $false }
    $stageRunner = Join-Path $stage 'tools\deep-android-runner.exe'
    [IO.File]::Copy($runner, $stageRunner, $true)
    [IO.File]::Copy($publicKey, (Join-Path $stage 'mr-x-public-key.bin'), $true)

    $policy = Get-Content -Raw -LiteralPath (Join-Path $source 'approved-policy.json') |
        ConvertFrom-Json
    $receipt = Join-Path $stage 'approval.receipt'
    $adb = Join-Path $stage 'tools\adb.exe'
    $aapt = Join-Path $stage 'tools\aapt.exe'
    $apksigner = Join-Path $stage 'tools\apksigner.bat'
    foreach ($tool in @($adb, $aapt, $apksigner, $stageRunner)) {
        $null = Get-ExactFile $tool 'Pinned policy tool' $null
    }

    $badging = @(& $aapt dump badging $apk 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0 -or $badging -notmatch
        "package:\s+name='(?<package>[^']+)'\s+versionCode='(?<code>[^']+)'\s+versionName='(?<name>[^']+)'") {
        throw 'aapt did not return canonical APK package metadata.'
    }
    $packageId = $Matches.package
    $versionCode = [int]$Matches.code
    $versionName = $Matches.name
    if ($packageId -cne 'network.xpoint.deep.e2e' -or $versionCode -le 0 -or
        [string]::IsNullOrWhiteSpace($versionName)) {
        throw 'The policy APK is not the physical E2E package.'
    }
    $certOutput = @(& $apksigner verify --print-certs $apk 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'apksigner rejected the policy APK.' }
    $certs = @([regex]::Matches($certOutput,
        'Signer #(?<number>[1-9][0-9]*) certificate SHA-256 digest:\s*(?<hash>[0-9a-fA-F]{64})'))
    if ($certs.Count -ne 1 -or $certs[0].Groups['number'].Value -cne '1') {
        throw 'The policy APK must have exactly one signer.'
    }

    $props = @{}
    foreach ($name in @('ro.build.fingerprint','ro.product.name','ro.hardware',
            'ro.product.model','ro.build.characteristics','ro.kernel.qemu','ro.build.version.sdk')) {
        $value = (@(& $adb -s $AndroidSerial shell getprop $name 2>&1) -join '').Trim()
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($value)) {
            throw "Android device property '$name' is unavailable."
        }
        $props[$name] = $value
    }
    if ($props['ro.kernel.qemu'] -cne '0' -or
        [int]$props['ro.build.version.sdk'] -lt 26) {
        throw 'Android lab policy requires a supported physical device.'
    }

    $random = [byte[]]::new(32)
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($random) } finally { $rng.Dispose() }
    try {
        $policy.policyId = ([BitConverter]::ToString($random)).Replace('-', '').ToLowerInvariant()
    }
    finally { [Array]::Clear($random, 0, $random.Length) }
    $receiptHash = Get-Sha256 $receipt
    $policy.provisioned = $true
    $policy.synthetic = $false
    $policy.sourceCommitSha = $commit
    $policy.approval.state = 'approved'
    $policy.approval.receiptSha256 = $receiptHash
    $policy.signature.publicKeySha256 = Get-Sha256 (Join-Path $stage 'mr-x-public-key.bin')
    $policy.device.serial = $AndroidSerial
    $policy.device.fingerprint = $props['ro.build.fingerprint']
    $policy.device.product = $props['ro.product.name']
    $policy.device.hardware = $props['ro.hardware']
    $policy.device.model = $props['ro.product.model']
    $policy.device.characteristics = $props['ro.build.characteristics']
    $policy.device.kernelQemu = $props['ro.kernel.qemu']
    $policy.device.sdk = [int]$props['ro.build.version.sdk']
    $policy.device.class = 'physical-managed-dedicated'
    $policy.device.dedicated = $true
    $policy.device.inventoryState = 'approved'
    $policy.device.inventoryApprovalReceiptSha256 = $receiptHash
    $policy.application.packageId = $packageId
    $policy.application.versionCode = $versionCode
    $policy.application.versionName = $versionName
    $policy.application.apkRelativePath = Get-RelativeRepositoryPath $apk
    $policy.application.apkSizeBytes = (Get-Item -LiteralPath $apk).Length
    $policy.application.apkSha256 = Get-Sha256 $apk
    $policy.application.signingCertificateSha256 =
        $certs[0].Groups['hash'].Value.ToLowerInvariant()
    $policy.crossPlatform.windowsExecutableRelativePath =
        Get-RelativeRepositoryPath $windowsExe
    $policy.crossPlatform.windowsExecutableSha256 = Get-Sha256 $windowsExe
    $windowsOutputDirectory = [IO.Path]::GetFullPath((Split-Path -Parent $windowsExe))
    $policy.crossPlatform.windowsOutputDirectoryRelativePath =
        Get-RelativeRepositoryPath $windowsOutputDirectory
    $policy.crossPlatform.windowsOutputTreeSha256 = Get-TreeDigest $windowsOutputDirectory
    foreach ($role in @('runner','adb','aapt','apksigner')) {
        $relative = ([string]$policy.tools.$role.relativePath).Substring(
            '.secrets/android-lab/'.Length).Replace('/', '\')
        $toolPath = Join-Path $stage $relative
        $policy.tools.$role.sha256 = Get-Sha256 $toolPath
    }
    foreach ($role in @('adb','aapt','apksigner')) {
        $relative = ([string]$policy.tools.$role.relativePath).Substring(
            '.secrets/android-lab/'.Length).Replace('/', '\')
        $toolPath = Join-Path $stage $relative
        $policy.tools.$role.version = Get-ExactToolVersion $toolPath `
            @($policy.tools.$role.versionArguments) $role
    }

    $payload = [ordered]@{
        schema = $policy.schema
        provisioned = $policy.provisioned
        synthetic = $policy.synthetic
        sourceCommitSha = $policy.sourceCommitSha
        policyId = $policy.policyId
        approval = $policy.approval
        tools = $policy.tools
        device = $policy.device
        application = $policy.application
        crossPlatform = $policy.crossPlatform
    }
    $payloadPath = Join-Path $stage 'signed-payload.json'
    $signaturePath = Join-Path $stage 'policy.signature'
    Remove-Item -LiteralPath $signaturePath -Force
    Write-Utf8NoBom $payloadPath ($payload | ConvertTo-Json -Depth 8 -Compress)
    $issuer = Join-Path $PSScriptRoot `
        'Deep.AndroidLab.PolicyIssuer\Deep.AndroidLab.PolicyIssuer.csproj'
    & dotnet run --project $issuer -c Release --no-build --no-restore -- sign `
        $privateKey (Join-Path $stage 'mr-x-public-key.bin') $payloadPath $signaturePath
    if ($LASTEXITCODE -ne 0) { throw 'Mr. X policy signing failed.' }
    $policy.signature.signedPayloadSha256 = Get-Sha256 $payloadPath
    Write-Utf8NoBom (Join-Path $stage 'approved-policy.json') (
        $policy | ConvertTo-Json -Depth 8)

    $verifier = Join-Path $PSScriptRoot `
        'Deep.AndroidLab.PolicyVerifier\Deep.AndroidLab.PolicyVerifier.csproj'
    & dotnet run --project $verifier -c Release --no-build --no-restore -- verify `
        (Join-Path $stage 'mr-x-public-key.bin') $signaturePath $payloadPath
    if ($LASTEXITCODE -ne 0) { throw 'New Android lab policy signature self-check failed.' }

    $owner = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $icacls = Join-Path $env:SystemRoot 'System32\icacls.exe'
    & $icacls $stage '/inheritance:r' '/grant:r' "${owner}:F" 'SYSTEM:F' 'Administrators:F' '/T' '/C' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not protect the new Android lab policy source.' }
    foreach ($file in Get-ChildItem -LiteralPath $stage -File -Recurse -Force) {
        $file.IsReadOnly = $true
    }
    $stageDigest = Get-TreeDigest $stage

    if (Test-Path -LiteralPath $backup) {
        if ((Get-TreeDigest $source) -cne $stageDigest) {
            throw 'An ambiguous interrupted Android lab policy rotation requires operator review.'
        }
        Remove-OwnedTree $stage '.protected-source.stage'
        Remove-OwnedTree $backup '.protected-source.backup'
        $published = $true
    } elseif ((Get-TreeDigest $source) -ceq $stageDigest) {
        Remove-OwnedTree $stage '.protected-source.stage'
        $published = $true
    } else {
        [IO.Directory]::Move($source, $backup)
        try {
            [IO.Directory]::Move($stage, $source)
            if ((Get-TreeDigest $source) -cne $stageDigest) {
                throw 'Published Android lab policy source failed its final reread.'
            }
        } catch {
            if (Test-Path -LiteralPath $source) {
                [IO.Directory]::Move($source, $stage)
            }
            [IO.Directory]::Move($backup, $source)
            throw
        }
        Remove-OwnedTree $backup '.protected-source.backup'
        $published = $true
    }
} finally {
    if (-not $published -and (Test-Path -LiteralPath $stage)) {
        Remove-OwnedTree $stage '.protected-source.stage'
    }
}

Write-Output (Join-Path $source 'approved-policy.json')
