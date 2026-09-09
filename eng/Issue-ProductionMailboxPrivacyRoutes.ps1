[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateJsonPath,
    [Parameter(Mandatory)][string]$MrXPrivateKeyPath,
    [Parameter(Mandatory)][string]$MrXPublicKeyPath,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$artifactNames = @(
    'production-mailbox-privacy-routes.v2.json',
    'production-mailbox-privacy-routes.v2.sig',
    'production-mailbox-privacy-routes.v2.pub')

function Assert-NoReparseAncestors([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if ((Test-Path -LiteralPath $current) -and
            ((Get-Item -Force -LiteralPath $current).Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Production privacy-route issuance path traverses a reparse point.'
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -ceq $current) { break }
        $current = $parent
    }
}

function Get-ExactFile([string]$Path, [string]$Label, [Nullable[long]]$Length) {
    $full = [IO.Path]::GetFullPath($Path)
    Assert-NoReparseAncestors $full
    if (-not (Test-Path -LiteralPath $full -PathType Leaf) -or
        ((Get-Item -Force -LiteralPath $full).Attributes -band
            [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        ($null -ne $Length -and (Get-Item -Force -LiteralPath $full).Length -ne $Length)) {
        throw "$Label is not an exact regular file."
    }
    return $full
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

function Assert-ExactPublishedTree([string]$Path) {
    Assert-NoReparseAncestors $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Container) -or
        ((Get-Item -Force -LiteralPath $Path).Attributes -band
            [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Production privacy-route output is not an exact regular directory.'
    }
    $items = @(Get-ChildItem -Force -LiteralPath $Path)
    if ($items.Count -ne 3 -or @($items | Where-Object {
        $_.PSIsContainer -or
        ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $_.Name -cnotin $artifactNames
    }).Count -ne 0) {
        throw 'Production privacy-route output must contain exactly the three signed artifacts.'
    }
    if ((Get-Item -Force -LiteralPath (Join-Path $Path $artifactNames[0])).Length -le 1 -or
        (Get-Item -Force -LiteralPath (Join-Path $Path $artifactNames[0])).Length -gt 16384 -or
        (Get-Item -Force -LiteralPath (Join-Path $Path $artifactNames[1])).Length -ne 64 -or
        (Get-Item -Force -LiteralPath (Join-Path $Path $artifactNames[2])).Length -ne 32) {
        throw 'Production privacy-route output has invalid artifact lengths.'
    }
}

function Get-PublishedDigest([string]$Path) {
    Assert-ExactPublishedTree $Path
    $lines = foreach ($name in $artifactNames) {
        $file = Get-Item -Force -LiteralPath (Join-Path $Path $name)
        "$name`t$($file.Length)`t$((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant())"
    }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($lines -join "`n") + "`n")
    try {
        $hasher = [Security.Cryptography.SHA256]::Create()
        try {
            $hash = $hasher.ComputeHash($bytes)
            try {
                return ([BitConverter]::ToString($hash)).Replace('-', '').ToLowerInvariant()
            }
            finally { [Array]::Clear($hash, 0, $hash.Length) }
        } finally { $hasher.Dispose() }
    } finally { [Array]::Clear($bytes, 0, $bytes.Length) }
}

function Remove-OwnedDirectory([string]$Path, [string]$ExpectedLeaf) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if ((Split-Path -Leaf $full) -cne $ExpectedLeaf) {
        throw 'Refusing to remove an issuer directory with an unexpected name.'
    }
    Assert-NoReparseAncestors $full
    foreach ($item in Get-ChildItem -Force -File -Recurse -LiteralPath $full) {
        $item.IsReadOnly = $false
    }
    Remove-Item -Force -Recurse -LiteralPath $full
}

$candidate = Get-ExactFile $CandidateJsonPath 'Candidate JSON' $null
$privateKey = Get-ExactFile $MrXPrivateKeyPath 'Mr. X private key' 64
$publicKey = Get-ExactFile $MrXPublicKeyPath 'Mr. X public key' 32
Assert-ProtectedPrivateKey $privateKey

$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\', '/')
$outputLeaf = Split-Path -Leaf $output
$parent = Split-Path -Parent $output
if ([string]::IsNullOrWhiteSpace($outputLeaf) -or
    [string]::IsNullOrWhiteSpace($parent) -or
    -not (Test-Path -LiteralPath $parent -PathType Container)) {
    throw 'A dedicated production privacy-route output directory is required.'
}
Assert-NoReparseAncestors $parent
if (((Get-Item -Force -LiteralPath $parent).Attributes -band
    [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'Production privacy-route output parent must not be a reparse point.'
}
$outputPrefix = $output + [IO.Path]::DirectorySeparatorChar
foreach ($input in @($candidate, $privateKey, $publicKey)) {
    if ($input -ceq $output -or $input.StartsWith($outputPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Production privacy-route inputs must be outside the replaceable output directory.'
    }
}
if (Test-Path -LiteralPath $output) { Assert-ExactPublishedTree $output }

$stageLeaf = ".$outputLeaf.stage"
$backupLeaf = ".$outputLeaf.backup"
$lockLeaf = ".$outputLeaf.issue.lock"
$stage = Join-Path $parent $stageLeaf
$backup = Join-Path $parent $backupLeaf
$lockPath = Join-Path $parent $lockLeaf
if ((Test-Path -LiteralPath $stage) -or (Test-Path -LiteralPath $backup)) {
    throw 'Interrupted production privacy-route issuance requires operator review.'
}

$lock = $null
$published = $false
try {
    $lock = [IO.FileStream]::new($lockPath, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::None, 1,
        [IO.FileOptions]::WriteThrough)
    [IO.Directory]::CreateDirectory($stage) | Out-Null

    $issuer = Join-Path $PSScriptRoot `
        'Deep.ProductionMailboxPrivacyRoutesIssuer\Deep.ProductionMailboxPrivacyRoutesIssuer.csproj'
    & dotnet run --project $issuer -c Release --no-build --no-restore -- issue `
        $candidate $privateKey $publicKey $stage
    if ($LASTEXITCODE -ne 0) {
        throw 'Production privacy-route validation or signing failed.'
    }
    Assert-ExactPublishedTree $stage
    foreach ($name in $artifactNames) {
        (Get-Item -Force -LiteralPath (Join-Path $stage $name)).IsReadOnly = $true
    }
    $stageDigest = Get-PublishedDigest $stage

    if ((Test-Path -LiteralPath $output) -and
        (Get-PublishedDigest $output) -ceq $stageDigest) {
        Remove-OwnedDirectory $stage $stageLeaf
        $published = $true
    } elseif (Test-Path -LiteralPath $output) {
        [IO.Directory]::Move($output, $backup)
        try {
            [IO.Directory]::Move($stage, $output)
            if ((Get-PublishedDigest $output) -cne $stageDigest) {
                throw 'Published production privacy-route artifacts failed their final reread.'
            }
        } catch {
            if (Test-Path -LiteralPath $output) {
                [IO.Directory]::Move($output, $stage)
            }
            [IO.Directory]::Move($backup, $output)
            throw
        }
        Remove-OwnedDirectory $backup $backupLeaf
        $published = $true
    } else {
        [IO.Directory]::Move($stage, $output)
        if ((Get-PublishedDigest $output) -cne $stageDigest) {
            [IO.Directory]::Move($output, $stage)
            throw 'Published production privacy-route artifacts failed their final reread.'
        }
        $published = $true
    }
} finally {
    if ($null -ne $lock) { $lock.Dispose() }
    if (Test-Path -LiteralPath $lockPath) {
        Remove-Item -Force -LiteralPath $lockPath
    }
    if (-not $published -and (Test-Path -LiteralPath $stage)) {
        Remove-OwnedDirectory $stage $stageLeaf
    }
}

Write-Output $output
