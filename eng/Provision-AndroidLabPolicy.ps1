[CmdletBinding(DefaultParameterSetName = 'Provision')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Provision')][string]$SourceRoot,
    [Parameter(Mandatory, ParameterSetName = 'Provision')][string]$ExpectedOwner,
    [Parameter(Mandatory, ParameterSetName = 'Provision')]
    [ValidatePattern('^[0-9a-f]{64}$')][string]$MrXPublicKeySha256,
    [Parameter(ParameterSetName = 'Provision')][string]$DestinationRoot,
    [Parameter(Mandatory, ParameterSetName = 'Clean')][switch]$Clean,
    [Parameter(ParameterSetName = 'Clean')][string]$CleanDestinationRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'StrictLane.Common.ps1')
$defaultDestination = Join-Path $repoRoot '.secrets\android-lab'

function Assert-ExactDestination {
    param([string]$Path)
    $canonical = [IO.Path]::GetFullPath($Path)
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals($canonical, [IO.Path]::GetFullPath($defaultDestination))) {
        throw 'Android lab material may only be provisioned at the fixed repository protected root.'
    }
    return $canonical
}

if ($Clean) {
    $target = Assert-ExactDestination $(if ([string]::IsNullOrWhiteSpace($CleanDestinationRoot)) {
        $defaultDestination
    } else {
        $CleanDestinationRoot
    })
    if (Test-Path -LiteralPath $target) {
        Assert-NoReparsePointInPath -Root $repoRoot -Candidate $target
        Get-ChildItem -LiteralPath $target -File -Recurse -Force |
            ForEach-Object { $_.IsReadOnly = $false }
        Remove-ContainedTree -Root $repoRoot -Candidate $target
    }
    exit 0
}

if ($MrXPublicKeySha256 -eq ('0' * 64)) {
    throw 'A nonzero externally pinned Mr. X public-key fingerprint is required.'
}
$source = [IO.Path]::GetFullPath($SourceRoot)
$destination = Assert-ExactDestination $(if ([string]::IsNullOrWhiteSpace($DestinationRoot)) {
    $defaultDestination
} else {
    $DestinationRoot
})
if (-not (Test-Path -LiteralPath $source -PathType Container)) {
    throw 'Protected Android lab source bundle is missing.'
}
if (((Get-Item -LiteralPath $source -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'Protected Android lab source root may not be a reparse point.'
}
$broadIdentities = @('Everyone', 'BUILTIN\Users', 'NT AUTHORITY\Authenticated Users')
function Assert-ProtectedAcl {
    param([string]$Path)
    $acl = Get-Acl -LiteralPath $Path
    if (-not $acl.AreAccessRulesProtected -or $acl.Owner -cne $ExpectedOwner) {
        throw 'Protected bundle item owner or inheritance does not match pinned lab configuration.'
    }
    foreach ($rule in $acl.Access) {
        $writeMask =
            [Security.AccessControl.FileSystemRights]::WriteData -bor
            [Security.AccessControl.FileSystemRights]::CreateFiles -bor
            [Security.AccessControl.FileSystemRights]::AppendData -bor
            [Security.AccessControl.FileSystemRights]::CreateDirectories -bor
            [Security.AccessControl.FileSystemRights]::Delete -bor
            [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
            [Security.AccessControl.FileSystemRights]::TakeOwnership
        $writes = $rule.FileSystemRights -band $writeMask
        if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            $writes -ne 0 -and [string]$rule.IdentityReference -in $broadIdentities) {
            throw "Protected bundle item grants broad write access: $Path"
        }
    }
    return $acl
}
$sourceAcl = Assert-ProtectedAcl -Path $source

$required = @(
    'approved-policy.json',
    'approval.receipt',
    'mr-x-public-key.bin',
    'policy.signature',
    'signed-payload.json')
foreach ($relative in $required) {
    $path = Join-Path $source $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
        -not (Get-Item -LiteralPath $path -Force).IsReadOnly) {
        throw "Protected source bundle file '$relative' is missing or not read-only."
    }
}
$pending = [Collections.Generic.Stack[string]]::new()
$pending.Push($source)
$sourceFiles = [Collections.Generic.List[object]]::new()
while ($pending.Count -gt 0) {
    $directory = $pending.Pop()
    $null = Assert-ProtectedAcl -Path $directory
    foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Protected bundle may not contain file or directory reparse points.'
        }
        $null = Assert-ProtectedAcl -Path $item.FullName
        if ($item.PSIsContainer) {
            $pending.Push($item.FullName)
        } elseif (-not $item.IsReadOnly) {
            throw 'Every protected source file must be regular and read-only.'
        } else {
            $sourceFiles.Add($item)
        }
    }
}

$policyPath = Join-Path $source 'approved-policy.json'
$payloadPath = Join-Path $source 'signed-payload.json'
$publicKeyPath = Join-Path $source 'mr-x-public-key.bin'
$signaturePath = Join-Path $source 'policy.signature'
$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
$payload = Get-Content -LiteralPath $payloadPath -Raw | ConvertFrom-Json
$allowedFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($relative in $required) { $null = $allowedFiles.Add($relative) }
$policyRelativePrefix = '.secrets/android-lab/'
foreach ($relative in @(
    [string]$policy.approval.receiptRelativePath,
    [string]$policy.tools.runner.relativePath,
    [string]$policy.tools.adb.relativePath,
    [string]$policy.tools.aapt.relativePath,
    [string]$policy.tools.apksigner.relativePath)) {
    if (-not $relative.StartsWith($policyRelativePrefix, [StringComparison]::Ordinal) -or
        $relative.Contains('\') -or $relative.Split('/').Contains('..')) {
        throw 'Protected policy contains a non-canonical bundle path.'
    }
    $null = $allowedFiles.Add($relative.Substring($policyRelativePrefix.Length))
}
foreach ($sourceFile in $sourceFiles) {
    $relative = (Get-RelativePathCompat -Root $source -Candidate $sourceFile.FullName).Replace('\', '/')
    if (-not $allowedFiles.Remove($relative)) {
        throw "Protected bundle contains unexpected or duplicate file '$relative'."
    }
}
if ($allowedFiles.Count -ne 0) {
    throw 'Protected bundle is missing one or more signed/approved files.'
}
if ((Get-Sha256Lower -Path (Join-Path $source (
        [string]$policy.approval.receiptRelativePath).Substring($policyRelativePrefix.Length))) -cne
        [string]$policy.approval.receiptSha256) {
    throw 'Protected approval receipt hash mismatch.'
}
foreach ($role in @('runner', 'adb', 'aapt', 'apksigner')) {
    $definition = $policy.tools.$role
    $toolPath = Join-Path $source ([string]$definition.relativePath).Substring($policyRelativePrefix.Length)
    if ((Get-Sha256Lower -Path $toolPath) -cne [string]$definition.sha256) {
        throw "Protected $role tool hash mismatch."
    }
}
if ((Get-Sha256Lower -Path $publicKeyPath) -cne $MrXPublicKeySha256 -or
    (Get-Sha256Lower -Path $payloadPath) -cne [string]$policy.signature.signedPayloadSha256 -or
    [string]$policy.signature.publicKeySha256 -cne $MrXPublicKeySha256 -or
    [string]$payload.schema -cne [string]$policy.schema -or
    $payload.provisioned -ne $policy.provisioned -or
    $payload.synthetic -ne $policy.synthetic -or
    [string]$payload.sourceCommitSha -cne [string]$policy.sourceCommitSha -or
    [string]$payload.policyId -cne [string]$policy.policyId -or
    ($payload.approval | ConvertTo-Json -Depth 8 -Compress) -cne ($policy.approval | ConvertTo-Json -Depth 8 -Compress) -or
    ($payload.tools | ConvertTo-Json -Depth 8 -Compress) -cne ($policy.tools | ConvertTo-Json -Depth 8 -Compress) -or
    ($payload.device | ConvertTo-Json -Depth 8 -Compress) -cne ($policy.device | ConvertTo-Json -Depth 8 -Compress) -or
    ($payload.application | ConvertTo-Json -Depth 8 -Compress) -cne ($policy.application | ConvertTo-Json -Depth 8 -Compress)) {
    throw 'Signed payload does not exactly bind the protected policy, tools, device, and application.'
}

$verifierProject = Join-Path $PSScriptRoot 'Deep.AndroidLab.PolicyVerifier\Deep.AndroidLab.PolicyVerifier.csproj'
dotnet run --project $verifierProject -c Release --no-build --no-restore -- verify $publicKeyPath $signaturePath $payloadPath
if ($LASTEXITCODE -ne 0) {
    throw 'Mr. X Ed25519 signature verification failed.'
}

if (Test-Path -LiteralPath $destination) {
    throw 'Destination already exists; clean it explicitly before provisioning.'
}
New-Item -ItemType Directory -Path $destination | Out-Null
try {
    Get-ChildItem -LiteralPath $source -Force |
        Copy-Item -Destination $destination -Recurse -Force
    $icacls = Join-Path $env:SystemRoot 'System32\icacls.exe'
    & $icacls $destination '/inheritance:r' '/grant:r' "${ExpectedOwner}:F" '/T' '/C' | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not protect provisioned destination ACL inheritance.'
    }
    foreach ($file in Get-ChildItem -LiteralPath $destination -File -Recurse -Force) {
        $file.IsReadOnly = $true
    }
    $destinationAcl = Get-Acl -LiteralPath $destination
    if (-not $destinationAcl.AreAccessRulesProtected -or $destinationAcl.Owner -cne $ExpectedOwner) {
        throw 'Provisioned destination ACL is not protected and owner-pinned.'
    }
    foreach ($sourceFile in Get-ChildItem -LiteralPath $source -File -Recurse -Force) {
        $relative = Get-RelativePathCompat -Root $source -Candidate $sourceFile.FullName
        $destinationFile = Join-Path $destination $relative
        if ((Get-Sha256Lower -Path $sourceFile.FullName) -cne (Get-Sha256Lower -Path $destinationFile)) {
            throw 'Provisioned Android lab bundle differs from the protected source.'
        }
    }
    $destinationItems = @(Get-Item -LiteralPath $destination)
    $destinationItems += @(Get-ChildItem -LiteralPath $destination -Recurse -Force)
    foreach ($destinationItem in $destinationItems) {
        $null = Assert-ProtectedAcl -Path $destinationItem.FullName
    }
} catch {
    Get-ChildItem -LiteralPath $destination -File -Recurse -Force -ErrorAction SilentlyContinue |
        ForEach-Object { $_.IsReadOnly = $false }
    Remove-ContainedTree -Root $repoRoot -Candidate $destination
    throw
}

Write-Output (Join-Path $destination 'approved-policy.json')
