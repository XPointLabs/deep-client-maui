[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('ExportHolder', 'PublishRuntime')]
    [string]$Action,
    [Parameter(Mandatory)]
    [string]$WindowsAppDataRoot,
    [string]$OutputPath,
    [string]$RuntimeRoot,
    [ValidatePattern('^[0-9a-f]{64}$')]
    [string]$MrXPublicKeySha256 = $env:DEEP_MR_X_PUBLIC_KEY_SHA256
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Test-AbsoluteWindowsPath([string]$Path) {
    return -not [string]::IsNullOrWhiteSpace($Path) -and
        ($Path -cmatch '^[A-Za-z]:[\\/]' -or $Path -cmatch '^\\\\[^\\/]+[\\/][^\\/]+')
}

function Get-ExactAbsoluteDirectory([string]$Path, [string]$Label) {
    if (-not (Test-AbsoluteWindowsPath $Path)) {
        throw "$Label must be an absolute path."
    }
    $full = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $full -PathType Container)) {
        throw "$Label must be an existing directory."
    }
    Assert-NoReparseTree $full
    return $full.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
}

function Assert-NoReparseTree([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if (Test-Path -LiteralPath $current) {
            if (((Get-Item -Force -LiteralPath $current).Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Mailbox bootstrap path must not traverse a reparse point.'
            }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -ceq $current) { break }
        $current = $parent
    }
    foreach ($item in Get-ChildItem -Force -Recurse -LiteralPath $Path) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Mailbox bootstrap tree must not contain a reparse point.'
        }
    }
}

function Get-RelativeChildPath([string]$Root, [string]$Child) {
    $canonicalRoot = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $canonicalChild = [IO.Path]::GetFullPath($Child)
    $prefix = $canonicalRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $canonicalChild.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Mailbox bootstrap enumeration escaped its canonical root.'
    }
    return $canonicalChild.Substring($prefix.Length).Replace('\', '/')
}

function Get-CurrentUserSid { return [Security.Principal.WindowsIdentity]::GetCurrent().User.Value }

function Set-CanonicalMailboxAcl([string]$Path) {
    if (((Get-Item -Force -LiteralPath $Path).Attributes -band
        [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Mailbox ACL path cannot be a reparse point.'
    }
    $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl = if ((Get-Item -Force -LiteralPath $Path).PSIsContainer) {
        [Security.AccessControl.DirectorySecurity]::new()
    } else {
        [Security.AccessControl.FileSecurity]::new()
    }
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner($owner)
    foreach ($sid in @($owner,
            [Security.Principal.SecurityIdentifier]'S-1-5-18',
            [Security.Principal.SecurityIdentifier]'S-1-5-32-544')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $sid, [Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.InheritanceFlags]::None,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
    Assert-CanonicalMailboxAcl $Path
}

function Assert-CanonicalMailboxAcl([string]$Path) {
    $item = Get-Item -Force -LiteralPath $Path
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Mailbox ACL path cannot be a reparse point.'
    }
    $acl = Get-Acl -LiteralPath $Path
    $owner = Get-CurrentUserSid
    if (-not $acl.AreAccessRulesProtected -or
        $acl.Owner -cne ([Security.Principal.WindowsIdentity]::GetCurrent().Name)) {
        throw 'Mailbox ACL owner or inheritance is not canonical.'
    }
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($sid in @($owner, 'S-1-5-18', 'S-1-5-32-544')) { $null = $expected.Add($sid) }
    $rules = @($acl.Access)
    if ($rules.Count -ne 3) { throw 'Mailbox ACL rule count is not canonical.' }
    foreach ($rule in $rules) {
        $sid = $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
            $rule.IsInherited -or
            $rule.FileSystemRights -ne [Security.AccessControl.FileSystemRights]::FullControl -or
            $rule.InheritanceFlags -ne [Security.AccessControl.InheritanceFlags]::None -or
            $rule.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None -or
            -not $expected.Remove($sid)) {
            throw 'Mailbox ACL is not the exact current-user/System/Administrators policy.'
        }
    }
    if ($expected.Count -ne 0) { throw 'Mailbox ACL is missing a required principal.' }
}

function Assert-CanonicalMailboxTreeAcl([string]$Root) {
    Assert-NoReparseTree $Root
    Assert-CanonicalMailboxAcl $Root
    foreach ($item in Get-ChildItem -Force -Recurse -LiteralPath $Root) {
        Assert-CanonicalMailboxAcl $item.FullName
    }
}

function Get-Sha256Lower([string]$Path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

function Read-ExactHolder([string]$AppRoot) {
    $holderPath = Join-Path $AppRoot 'mailbox-holder-bootstrap-v1\windows.holder.v1.json'
    if (-not (Test-Path -LiteralPath $holderPath -PathType Leaf)) {
        throw 'Windows app-private holder bootstrap record is missing.'
    }
    Assert-NoReparseTree (Split-Path -Parent $holderPath)
    Assert-CanonicalMailboxAcl (Split-Path -Parent $holderPath)
    Assert-CanonicalMailboxAcl $holderPath
    $raw = Get-Content -Raw -LiteralPath $holderPath
    $holder = $raw | ConvertFrom-Json
    $names = @($holder.PSObject.Properties.Name | Sort-Object)
    if (($names -join ',') -cne
        'developmentOnly,ed25519PublicKey,platform,schemaVersion,sessionId') {
        throw 'Windows holder bootstrap record has an invalid schema.'
    }
    if ($holder.schemaVersion -ne 1 -or -not $holder.developmentOnly -or
        [string]$holder.platform -cne 'windows' -or
        [string]$holder.sessionId -cnotmatch '^05[0-9a-f]{64}$' -or
        [string]$holder.ed25519PublicKey -cnotmatch '^[0-9a-f]{64}$') {
        throw 'Windows holder bootstrap record is not canonical public-only material.'
    }
    return ($holder | ConvertTo-Json -Compress)
}

function Write-ProtectedHolder([string]$Destination, [string]$CanonicalJson) {
    if (-not (Test-AbsoluteWindowsPath $Destination)) {
        throw 'Holder output path must be absolute.'
    }
    $holderOutput = [IO.Path]::GetFullPath($Destination)
    $parent = Split-Path -Parent $holderOutput
    if ([string]::IsNullOrWhiteSpace($parent) -or -not (Test-Path -LiteralPath $parent -PathType Container)) {
        throw 'Holder output parent must be an existing directory.'
    }
    Assert-NoReparseTree $parent
    if (Test-Path -LiteralPath $holderOutput) { throw 'Holder output already exists; replace it explicitly outside this command.' }
    [IO.File]::WriteAllText($holderOutput, $CanonicalJson, [Text.UTF8Encoding]::new($false))
    try { Set-CanonicalMailboxAcl $holderOutput }
    catch { Remove-Item -LiteralPath $holderOutput -Force -ErrorAction SilentlyContinue; throw }
}

function Assert-SourceRuntime([string]$Root, [string]$Pin) {
    $activationPath = Join-Path $Root 'activation.v1.json'
    $pointerPath = Join-Path $Root 'pair\current-generation.json'
    foreach ($relative in @('activation.v1.json', 'authority.public.json', 'revocations.v1.json',
            'mr-x-mailbox-policy.payload.json', 'mr-x-mailbox-policy.signature',
            'mr-x-mailbox-policy.public-key', 'pair\current-generation.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Root $relative) -PathType Leaf)) {
            throw "Mailbox runtime is missing required file: $relative"
        }
    }
    $activation = Get-Content -Raw -LiteralPath $activationPath | ConvertFrom-Json
    $pointer = Get-Content -Raw -LiteralPath $pointerPath | ConvertFrom-Json
    $generation = [string]$pointer.generation
    if ($activation.schemaVersion -ne 1 -or -not $activation.developmentOnly -or
        [string]$activation.platform -cne 'windows' -or
        $generation -cnotmatch '^[0-9a-f]{64}$' -or
        [string]$activation.pairGeneration -cne $generation -or
        [string]$activation.pairManifestSha256 -cne [string]$pointer.pairManifestSha256) {
        throw 'Mailbox runtime activation and pair pointer do not match Windows DEV schema v1.'
    }
    $files = @('activation.v1.json', 'authority.public.json', 'revocations.v1.json',
        'mr-x-mailbox-policy.payload.json', 'mr-x-mailbox-policy.signature',
        'mr-x-mailbox-policy.public-key', 'pair/current-generation.json',
        "pair/generations/$generation/android.mailbox-credentials.v1.json",
        "pair/generations/$generation/windows.mailbox-credentials.v1.json",
        "pair/generations/$generation/pair-manifest.v1.json")
    foreach ($relative in $files) {
        if (-not (Test-Path -LiteralPath (Join-Path $Root $relative) -PathType Leaf)) {
            throw "Mailbox runtime generation is incomplete: $relative"
        }
    }
    $publicKey = Join-Path $Root 'mr-x-mailbox-policy.public-key'
    $signature = Join-Path $Root 'mr-x-mailbox-policy.signature'
    $payload = Join-Path $Root 'mr-x-mailbox-policy.payload.json'
    if ((Get-Item -LiteralPath $publicKey).Length -ne 32 -or
        (Get-Sha256Lower $publicKey) -cne $Pin -or
        (Get-Item -LiteralPath $signature).Length -ne 64) {
        throw 'Mailbox runtime Mr. X approval does not match the independent build pin.'
    }
    $verifier = Join-Path $PSScriptRoot 'Deep.AndroidLab.PolicyVerifier\Deep.AndroidLab.PolicyVerifier.csproj'
    & dotnet run --project $verifier -c Release --no-build --no-restore -- verify $publicKey $signature $payload | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Mailbox runtime Mr. X Ed25519 approval signature is invalid.' }
    $authority = Get-Content -Raw -LiteralPath (Join-Path $Root 'authority.public.json') | ConvertFrom-Json
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    if ([long]$authority.issuerValidFromUnixSeconds -gt $now -or
        [long]$authority.issuerValidUntilUnixSeconds -lt ($now + 1800) -or
        @($authority.selections).Count -ne 0 -or
        @($authority.epochs | Where-Object { @($_.replicas).Count -ne 0 }).Count -ne 0) {
        throw 'Mailbox authority must be live for 30 minutes and use the minimized client shape.'
    }
    $manifest = Join-Path $Root "pair\generations\$generation\pair-manifest.v1.json"
    if ((Get-Sha256Lower (Join-Path $Root 'authority.public.json')) -cne [string]$activation.authoritySha256 -or
        (Get-Sha256Lower (Join-Path $Root 'revocations.v1.json')) -cne [string]$activation.revocationSnapshotSha256 -or
        (Get-Sha256Lower $manifest) -cne [string]$activation.pairManifestSha256) {
        throw 'Mailbox runtime independent activation hashes do not match staged bytes.'
    }
    $actualFiles = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force |
        ForEach-Object { Get-RelativeChildPath $Root $_.FullName } | Sort-Object)
    if (($actualFiles -join "`n") -cne (($files | Sort-Object) -join "`n")) {
        throw 'Mailbox runtime source root contains missing or unexpected files.'
    }
    $actualDirectories = @(Get-ChildItem -LiteralPath $Root -Recurse -Directory -Force |
        ForEach-Object { Get-RelativeChildPath $Root $_.FullName } | Sort-Object)
    $directories = @('pair', 'pair/generations', "pair/generations/$generation" | Sort-Object)
    if (($actualDirectories -join "`n") -cne ($directories -join "`n")) {
        throw 'Mailbox runtime source root contains missing or unexpected directories.'
    }
    return [pscustomobject]@{
        Files = $files
        Directories = $directories
        Generation = $generation
    }
}

function Test-RuntimeMatchesSource(
    [Parameter(Mandatory)][string]$Candidate,
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string[]]$Files,
    [Parameter(Mandatory)][string[]]$Directories) {
    if (-not (Test-Path -LiteralPath $Candidate -PathType Container)) { return $false }
    Assert-NoReparseTree $Candidate
    $actualFiles = @(Get-ChildItem -LiteralPath $Candidate -Recurse -File -Force |
        ForEach-Object { Get-RelativeChildPath $Candidate $_.FullName } | Sort-Object)
    if (($actualFiles -join "`n") -cne (($Files | Sort-Object) -join "`n")) {
        return $false
    }
    $actualDirectories = @(Get-ChildItem -LiteralPath $Candidate -Recurse -Directory -Force |
        ForEach-Object { Get-RelativeChildPath $Candidate $_.FullName } | Sort-Object)
    if (($actualDirectories -join "`n") -cne (($Directories | Sort-Object) -join "`n")) {
        return $false
    }
    foreach ($relative in $Files) {
        if ((Get-Sha256Lower (Join-Path $Candidate $relative)) -cne
            (Get-Sha256Lower (Join-Path $Source $relative))) {
            return $false
        }
    }
    return $true
}

function Remove-OwnedMailboxTree([string]$Path, [string]$ExpectedName) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    if ((Split-Path -Leaf $Path) -cne $ExpectedName) {
        throw 'Refusing to remove a mailbox tree with an unexpected name.'
    }
    Assert-NoReparseTree $Path
    Get-ChildItem -LiteralPath $Path -File -Recurse -Force -ErrorAction SilentlyContinue |
        ForEach-Object { $_.IsReadOnly = $false }
    Remove-Item -LiteralPath $Path -Recurse -Force
}

$appRoot = Get-ExactAbsoluteDirectory $WindowsAppDataRoot 'WindowsAppDataRoot'
if ($Action -eq 'ExportHolder') {
    $holder = Read-ExactHolder $appRoot
    if ([string]::IsNullOrWhiteSpace($OutputPath)) { $holder } else { Write-ProtectedHolder $OutputPath $holder }
    exit 0
}
if (-not (Test-AbsoluteWindowsPath $RuntimeRoot)) {
    throw 'PublishRuntime requires an absolute -RuntimeRoot directory.'
}
if ($MrXPublicKeySha256 -cnotmatch '^[0-9a-f]{64}$') {
    throw 'PublishRuntime requires the exact lowercase Mr. X public-key SHA-256 pin.'
}
$source = Get-ExactAbsoluteDirectory $RuntimeRoot 'RuntimeRoot'
$runtime = Assert-SourceRuntime $source $MrXPublicKeySha256
$holder = (Read-ExactHolder $appRoot) | ConvertFrom-Json
$manifest = Get-Content -Raw -LiteralPath (
    Join-Path $source "pair\generations\$($runtime.Generation)\pair-manifest.v1.json") |
    ConvertFrom-Json
$credentials = Get-Content -Raw -LiteralPath (
    Join-Path $source "pair\generations\$($runtime.Generation)\windows.mailbox-credentials.v1.json") |
    ConvertFrom-Json
if ([string]$credentials.holderPublicKey -cne [string]$holder.ed25519PublicKey -or
    [string]$manifest.windowsHolderPublicKey -cne [string]$holder.ed25519PublicKey) {
    throw 'The new mailbox runtime does not belong to the installed Windows holder.'
}
$runningClients = @(Get-Process -Name 'Deep.Client.Maui' -ErrorAction SilentlyContinue)
if ($runningClients.Count -ne 0) {
    throw 'The Windows Deep client must be stopped before mailbox runtime publication.'
}
$destination = Join-Path $appRoot 'mailbox-runtime-v1'
$stage = Join-Path $appRoot '.mailbox-runtime-v1.stage'
$backup = Join-Path $appRoot '.mailbox-runtime-v1.backup'
$published = $false
try {
    if ((Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $destination)) {
        [IO.Directory]::Move($backup, $destination)
    } elseif (Test-Path -LiteralPath $backup) {
        if (-not (Test-RuntimeMatchesSource $destination $source $runtime.Files $runtime.Directories)) {
            throw 'An ambiguous interrupted Windows mailbox runtime rotation requires operator review.'
        }
        Remove-OwnedMailboxTree $backup '.mailbox-runtime-v1.backup'
    }
    if (Test-Path -LiteralPath $stage) {
        Remove-OwnedMailboxTree $stage '.mailbox-runtime-v1.stage'
    }
    if (Test-RuntimeMatchesSource $destination $source $runtime.Files $runtime.Directories) {
        Assert-CanonicalMailboxTreeAcl $destination
        Write-Output 'Windows mailbox runtime already matches the verified publication.'
        exit 0
    }
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    foreach ($relative in $runtime.Files) {
        $sourceFile = Join-Path $source $relative
        $stageFile = Join-Path $stage $relative
        [IO.Directory]::CreateDirectory((Split-Path -Parent $stageFile)) | Out-Null
        [IO.File]::Copy($sourceFile, $stageFile, $false)
        if ((Get-Sha256Lower $sourceFile) -cne (Get-Sha256Lower $stageFile)) {
            throw "Staged Windows mailbox runtime hash mismatch: $relative"
        }
    }
    $stagedFiles = @(Get-ChildItem -LiteralPath $stage -Recurse -File -Force |
        ForEach-Object { Get-RelativeChildPath $stage $_.FullName } | Sort-Object)
    if (($stagedFiles -join "`n") -cne (($runtime.Files | Sort-Object) -join "`n")) {
        throw 'Windows mailbox staging root contains missing or unexpected files.'
    }
    foreach ($entry in @((Get-Item -LiteralPath $stage)) + @(Get-ChildItem -LiteralPath $stage -Force -Recurse)) {
        Set-CanonicalMailboxAcl $entry.FullName
    }
    Assert-CanonicalMailboxTreeAcl $stage
    if (Test-Path -LiteralPath $destination) {
        if (Test-Path -LiteralPath $backup) {
            throw 'Windows mailbox runtime backup appeared during publication.'
        }
        [IO.Directory]::Move($destination, $backup)
    }
    try {
        if (Test-Path -LiteralPath $destination) {
            throw 'Live Windows mailbox runtime appeared during publication.'
        }
        [IO.Directory]::Move($stage, $destination)
        Assert-CanonicalMailboxTreeAcl $destination
        if (-not (Test-RuntimeMatchesSource $destination $source $runtime.Files $runtime.Directories)) {
            throw 'Published Windows mailbox runtime failed its final byte-for-byte reread.'
        }
    } catch {
        if (Test-Path -LiteralPath $backup) {
            if (Test-Path -LiteralPath $destination) {
                if (Test-Path -LiteralPath $stage) {
                    throw 'Windows mailbox runtime rollback found an ambiguous stage.'
                }
                [IO.Directory]::Move($destination, $stage)
            }
            [IO.Directory]::Move($backup, $destination)
        }
        throw
    }
    if (Test-Path -LiteralPath $backup) {
        Remove-OwnedMailboxTree $backup '.mailbox-runtime-v1.backup'
    }
    $published = $true
} finally {
    if (-not $published -and (Test-Path -LiteralPath $stage)) {
        Remove-OwnedMailboxTree $stage '.mailbox-runtime-v1.stage'
    }
}
Write-Output 'Windows mailbox runtime published.'
