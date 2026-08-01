[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Attach', 'HappyPath', 'RestartDurability')]
    [string]$Phase,
    [string]$AndroidSerial = '192.168.1.45:43337',
    [string]$AdbPath = 'C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe',
    [string]$MailboxBootstrapRoot = 'C:\Work\DeepSession\secrets\mailbox-bootstrap',
    [string]$MrXPublicKeySha256 = $env:DEEP_MR_X_PUBLIC_KEY_SHA256,
    [switch]$Execute
)

# This is a non-destructive, opt-in physical lane.  It only starts/stops the E2E
# package, preserves its app data and the supplied Windows root, and never queries
# a holder/capability value.  The production package is snapshotted before/after.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$devOpsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot '..\deep-devops'))
$androidPackage = 'network.xpoint.deep.e2e'
$productionPackage = 'network.xpoint.deep'
$policyPath = Join-Path $repoRoot '.secrets\android-lab\approved-policy.json'

function Assert-AbsoluteExisting([string]$Path, [string]$Label, [switch]$Directory) {
    if (-not [IO.Path]::IsPathFullyQualified($Path) -or
        -not (Test-Path -LiteralPath $Path -PathType $(if ($Directory) { 'Container' } else { 'Leaf' }))) {
        throw "$Label must be an existing absolute $($(if ($Directory) { 'directory' } else { 'file' }))."
    }
    return [IO.Path]::GetFullPath($Path)
}

function Invoke-AdbQuiet([string[]]$Arguments) {
    $output = @(& $script:adb @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "ADB command failed: $($Arguments[0..([Math]::Min(3, $Arguments.Count - 1))] -join ' ')" }
    return $output
}

function Get-PackageSnapshot([string]$Package) {
    $value = @(Invoke-AdbQuiet @('-s', $AndroidSerial, 'shell', 'pm', 'path', $Package) |
        ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join "`n"
    if ([string]::IsNullOrWhiteSpace($value)) { throw "Required package '$Package' is absent." }
    return $value
}

function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

function Set-ProtectedRunItem([string]$Path) {
    $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl = if ((Get-Item -Force -LiteralPath $Path).PSIsContainer) {
        [Security.AccessControl.DirectorySecurity]::new()
    } else {
        [Security.AccessControl.FileSecurity]::new()
    }
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner($owner)
    foreach ($sid in @($owner, [Security.Principal.SecurityIdentifier]'S-1-5-18', [Security.Principal.SecurityIdentifier]'S-1-5-32-544')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, [Security.AccessControl.FileSystemRights]::FullControl, [Security.AccessControl.InheritanceFlags]::None, [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Set-ProtectedRunTree([string]$Path) {
    Set-ProtectedRunItem $Path
    foreach ($item in Get-ChildItem -Force -Recurse -LiteralPath $Path) {
        Set-ProtectedRunItem $item.FullName
    }
}

function Assert-SanitizedState([object]$State) {
    $json = $State | ConvertTo-Json -Depth 6 -Compress
    foreach ($forbidden in @('sessionId', 'holder', 'credential', 'capability', 'privateKey', 'seed', 'payload', 'message')) {
        if ($json -match [regex]::Escape($forbidden)) { throw 'Run state attempted to contain secret or message material.' }
    }
}

function Assert-DockerHealthy {
    $script = Join-Path $devOpsRoot 'scripts\survival-dev.ps1'
    Assert-AbsoluteExisting $script 'Supported survival dev status script' | Out-Null
    # Status is the supported read-only operation; no raw compose lifecycle command is used here.
    $status = @(& $script -Action Status 2>&1)
    if ($LASTEXITCODE -ne 0 -or @($status | Where-Object { [string]$_ -match '(?i)unhealthy|exited|dead' }).Count -ne 0) {
        throw 'Survival Docker environment is not healthy.'
    }
}

$adb = Assert-AbsoluteExisting $AdbPath 'ADB'
$bootstrap = Assert-AbsoluteExisting $MailboxBootstrapRoot 'Mailbox bootstrap root' -Directory
$androidRuntime = Assert-AbsoluteExisting (Join-Path $bootstrap 'runtime\android') 'Android runtime root' -Directory
$windowsRuntime = Assert-AbsoluteExisting (Join-Path $bootstrap 'runtime\windows') 'Windows runtime root' -Directory
$windowsAppData = Assert-AbsoluteExisting (Join-Path $bootstrap 'windows') 'Windows app data root' -Directory
$policy = Assert-AbsoluteExisting $policyPath 'Approved Android policy'
if ($MrXPublicKeySha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Mr. X public key hash must be exactly lowercase SHA-256.' }
$runtimeEnvironment = Assert-AbsoluteExisting (Join-Path $repoRoot 'eng\survival.dev.env') 'MAU2 runtime environment'
$runtimeEnvironmentText = Get-Content -Raw -LiteralPath $runtimeEnvironment
if ($runtimeEnvironmentText -cnotmatch '(?m)^DEEP_TRANSPORT_PROTOCOL=authenticated-mau2$' -or
    $runtimeEnvironmentText -cnotmatch '(?m)^DEEP_TRANSPORT_OWNERSHIP=user-managed$') {
    throw 'The physical lane requires the checked-in authenticated MAU2 user-managed runtime profile.'
}
$sourceCommit = @(& git -C $repoRoot rev-parse HEAD)
if ($LASTEXITCODE -ne 0 -or $sourceCommit.Count -ne 1 -or $sourceCommit[0].Trim() -cnotmatch '^[0-9a-f]{40}$') {
    throw 'The MAUI checkout must resolve to one canonical commit.'
}
$sourceCommit = $sourceCommit[0].Trim()
$worktreeState = @(& git -C $repoRoot status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or $worktreeState.Count -ne 0) {
    throw 'Physical commit-bound evidence requires a clean MAUI worktree, including untracked files.'
}

$runId = [Guid]::NewGuid().ToString('N')
$runRoot = Join-Path $bootstrap "e2e-runs\$runId"
[IO.Directory]::CreateDirectory($runRoot) | Out-Null
Set-ProtectedRunTree $runRoot
$runStatePath = Join-Path $runRoot 'run-state.json'
$artifacts = Join-Path $runRoot 'artifacts'
[IO.Directory]::CreateDirectory($artifacts) | Out-Null
Set-ProtectedRunTree $artifacts

$productionBefore = Get-PackageSnapshot $productionPackage
try {
    Invoke-AdbQuiet @('start-server') | Out-Null
    $devices = @(& $adb devices | Select-Object -Skip 1 | Where-Object { $_ -ceq "$AndroidSerial`tdevice" })
    if ($devices.Count -ne 1) { throw 'The exact Wi-Fi Android device is not attached.' }
    Get-PackageSnapshot $androidPackage | Out-Null
    Assert-DockerHealthy

    $policyObject = Get-Content -Raw -LiteralPath $policy | ConvertFrom-Json
    if ($policyObject.sourceCommitSha -notmatch '^[0-9a-f]{40}$' -or
        $policyObject.application.packageId -cne $androidPackage -or
        $policyObject.signature.publicKeySha256 -cne $MrXPublicKeySha256) {
        throw 'Approved lab policy does not bind this exact DEV lane.'
    }

    $state = [ordered]@{
        schema = 'deep.mau2-physical-run-state.v1'
        runId = $runId
        phase = $Phase
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        status = 'prepared'
        sourceCommit = $sourceCommit
        policySha256 = Get-Sha256 $policy
        runtimeEnvironmentSha256 = Get-Sha256 $runtimeEnvironment
        transportProtocol = 'authenticated-mau2'
        transportOwnership = 'user-managed'
        androidRuntimeTreeSha256 = (Get-Sha256 (Join-Path $androidRuntime 'activation.v1.json'))
        windowsRuntimeTreeSha256 = (Get-Sha256 (Join-Path $windowsRuntime 'activation.v1.json'))
        windowsRootPresent = $true
        dockerHealthy = $true
        productionPackageUntouched = $true
        storage = [ordered]@{ replication = 'shared-dev-storage-non-replicated'; before = $null; after = $null }
    }
    Assert-SanitizedState $state
    $state | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $runStatePath -Encoding utf8NoBOM
    Set-ProtectedRunTree $runRoot

    if ($Execute) {
        $env:DEEP_STRICT_CROSS_PLATFORM_UI = '1'
        $env:DEEP_MAU2_E2E_PHASE = $Phase
        $env:DEEP_MAU2_E2E_RUN_STATE = $runStatePath
        $env:DEEP_E2E_ANDROID_SERIAL = $AndroidSerial
        $env:DEEP_E2E_ADB = $adb
        $env:DEEP_E2E_ANDROID_POLICY = $policy
        $env:DEEP_MR_X_PUBLIC_KEY_SHA256 = $MrXPublicKeySha256
        $env:DEEP_E2E_ARTIFACTS = $artifacts
        $env:DEEP_E2E_APPDATA_ROOT = $windowsAppData
        $env:DEEP_E2E_BOOTSTRAP = 'live'
        $env:DEEP_TRANSPORT_PROTOCOL = 'authenticated-mau2'
        $env:DEEP_TRANSPORT_OWNERSHIP = 'user-managed'
        # The test itself rechecks its policy/tool/APK pins. This wrapper never emits
        # their paths, holders, sessions, message markers, or native/container logs.
        & dotnet test (Join-Path $repoRoot 'tests\Deep.Client.Maui.UiTests\Deep.Client.Maui.UiTests.csproj') --no-restore --filter 'FullyQualifiedName~StrictCrossPlatformUiTests'
        if ($LASTEXITCODE -ne 0) { throw 'Physical MAU2 UI phase failed.' }
    }
} finally {
    $productionAfter = Get-PackageSnapshot $productionPackage
    if ($productionBefore -cne $productionAfter) { throw 'Production Android package changed during MAU2 E2E.' }
}

Write-Output "Physical MAU2 phase '$Phase' prepared. Sanitized protected run state: $runId"
