[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('ProvisionIdentity', 'Attach', 'PayloadMatrix', 'Call', 'RestartDurability', 'ManualResendAfterRestart', 'AutomaticRetryAfterRestart', 'AckCrashWindow', 'NegativeRuntime')]
    [string]$Phase,
    [string]$AndroidSerial = '192.168.1.45:43337',
    [string]$MailboxBootstrapRoot = 'C:\Work\DeepSession\secrets\mailbox-bootstrap',
    [string]$MrXPublicKeySha256 = $env:DEEP_MR_X_PUBLIC_KEY_SHA256,
    [string]$AndroidPickerFileId = 'android:id/title',
    [string]$AndroidPickerConfirmId,
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

function Test-AbsoluteWindowsPath([string]$Path) {
    return -not [string]::IsNullOrWhiteSpace($Path) -and
        ($Path -cmatch '^[A-Za-z]:[\\/]' -or
         $Path -cmatch '^\\\\[^\\/]+[\\/][^\\/]+')
}

function Assert-AbsoluteExisting([string]$Path, [string]$Label, [switch]$Directory) {
    if (-not (Test-AbsoluteWindowsPath $Path) -or
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

function Resolve-PolicyPinnedFile(
    [string]$RelativePath,
    [string]$ExpectedSha256,
    [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or
        $RelativePath -cnotmatch '^[A-Za-z0-9._/-]+$' -or
        $RelativePath.StartsWith('/') -or
        $RelativePath.Contains('\') -or
        @($RelativePath.Split('/') | Where-Object { $_ -ceq '.' -or $_ -ceq '..' }).Count -ne 0 -or
        $ExpectedSha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw "$Label has an invalid signed repository-relative path or SHA-256."
    }
    $root = $repoRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $path = [IO.Path]::GetFullPath((Join-Path $repoRoot $RelativePath))
    if (-not $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $path -PathType Leaf) -or
        ((Get-Item -Force -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        (Get-Sha256 $path) -cne $ExpectedSha256) {
        throw "$Label does not resolve to the exact signed regular file."
    }
    return $path
}

function New-CanonicalAndroidSelectorsJson {
    $roles = @(
        'Startup.Status', 'Welcome.DisplayName', 'Welcome.Create', 'Conversations.Root',
        'PhysicalE2E.RuntimeReadyMarker',
        'Conversations.ProfileSettings', 'Conversations.NewConversationTop',
        'Conversations.ConversationRow', 'Settings.SessionId', 'Settings.Back',
        'StartConversation.NewMessage', 'NewConversation.SessionId',
        'NewConversation.DisplayName', 'NewConversation.Start',
        'NewConversation.Error', 'NewConversation.Back', 'Chat.Back', 'Chat.Draft',
        'Chat.Send', 'Chat.MessageBody', 'Chat.MessageBubble', 'Chat.DeliveryStatus',
        'Chat.Attach', 'Chat.PickFile', 'Chat.PickPhoto', 'Chat.StagedAttachmentFilename',
        'Chat.AttachmentFilename', 'Chat.AttachmentMetadata', 'Chat.AttachmentOpen',
        'Chat.AttachmentSave', 'Chat.MessageAttachmentOpen', 'Chat.MessageAttachmentSave',
        'Chat.ImagePreview', 'Chat.ImageMetadata',
        'Chat.Voice', 'Chat.VoicePlayButton', 'PhysicalE2E.VoicePlaybackState',
        'Call.Root', 'Call.Status', 'Call.MediaState', 'Call.Microphone',
        'Call.MicrophoneState', 'Call.Hangup')
    $selectors = [ordered]@{}
    foreach ($role in $roles) {
        $selectors[$role] = "$androidPackage`:id/$role"
    }
    return ($selectors | ConvertTo-Json -Compress)
}

function Assert-ExactPhysicalTestResult([string]$TrxPath) {
    if (-not (Test-Path -LiteralPath $TrxPath -PathType Leaf)) {
        throw 'The physical test did not produce its runner-owned TRX result.'
    }
    [xml]$trx = Get-Content -Raw -LiteralPath $TrxPath
    $counters = $trx.TestRun.ResultSummary.Counters
    if ($null -eq $counters) { throw 'The physical TRX result has no counters.' }
    $total = [int]$counters.total
    $executed = [int]$counters.executed
    $passed = [int]$counters.passed
    $failed = [int]$counters.failed
    $notExecuted = [int]$counters.notExecuted
    if ($total -ne 1 -or $executed -ne 1 -or $passed -ne 1 -or
        $failed -ne 0 -or $notExecuted -ne 0) {
        throw "Physical MAU2 requires exactly one executed pass (total=$total executed=$executed passed=$passed failed=$failed notExecuted=$notExecuted)."
    }
}

function Get-TreeSha256([string]$Root) {
    $canonical = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
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
    $paths = [string[]]@(Get-ChildItem -LiteralPath $canonical -File -Recurse -Force |
        ForEach-Object FullName)
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    $lines = foreach ($path in $paths) {
        $file = Get-Item -Force -LiteralPath $path
        $relative = $path.Substring($canonical.Length + 1).Replace('\', '/')
        "$relative`t$($file.Length)`t$((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant())"
    }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($lines -join "`n") + "`n")
    $hasher = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($hasher.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    } finally {
        $hasher.Dispose()
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
}

function Resolve-PolicyPinnedDirectory(
    [string]$RelativePath,
    [string]$ExpectedTreeSha256,
    [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or
        $RelativePath -cnotmatch '^[A-Za-z0-9._/-]+$' -or
        $RelativePath.StartsWith('/') -or
        $RelativePath.Contains('\') -or
        @($RelativePath.Split('/') | Where-Object { $_ -ceq '.' -or $_ -ceq '..' }).Count -ne 0 -or
        $ExpectedTreeSha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw "$Label has an invalid signed repository-relative path or tree SHA-256."
    }
    $root = $repoRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $path = [IO.Path]::GetFullPath((Join-Path $repoRoot $RelativePath))
    if (-not $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $path -PathType Container) -or
        (Get-TreeSha256 $path) -cne $ExpectedTreeSha256) {
        throw "$Label does not resolve to the exact signed regular directory tree."
    }
    return $path
}

function Set-ProtectedRunItem([string]$Path) {
    $item = Get-Item -Force -LiteralPath $Path
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Refusing to apply a run ACL through a reparse point.'
    }
    $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl = if ($item.PSIsContainer) {
        [Security.AccessControl.DirectorySecurity]::new()
    } else {
        [Security.AccessControl.FileSecurity]::new()
    }
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($owner, [Security.Principal.SecurityIdentifier]'S-1-5-18', [Security.Principal.SecurityIdentifier]'S-1-5-32-544')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, [Security.AccessControl.FileSystemRights]::FullControl, [Security.AccessControl.InheritanceFlags]::None, [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow))
    }
    if ($item.PSIsContainer) {
        [IO.DirectoryInfo]::new($Path).SetAccessControl(
            [Security.AccessControl.DirectorySecurity]$acl)
    } else {
        [IO.FileInfo]::new($Path).SetAccessControl(
            [Security.AccessControl.FileSecurity]$acl)
    }
}

function Assert-NonReparseDirectory([string]$Path, [string]$Label) {
    $item = Get-Item -Force -LiteralPath $Path
    if (-not $item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label must be a regular directory, never a reparse point."
    }
}

function Assert-ExactProtectedRunDirectory([string]$Path, [string]$Label) {
    Assert-NonReparseDirectory $Path $Label
    $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl = Get-Acl -LiteralPath $Path
    $actualOwner = $acl.GetOwner([Security.Principal.SecurityIdentifier])
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($sid in @($owner.Value, 'S-1-5-18', 'S-1-5-32-544')) {
        [void]$expected.Add($sid)
    }
    $rules = @($acl.GetAccessRules($true, $true,
        [Security.Principal.SecurityIdentifier]))
    if (-not $acl.AreAccessRulesProtected -or
        -not $actualOwner.Equals($owner) -or
        $rules.Count -ne 3) {
        throw "$Label does not have the exact protected owner/DACL."
    }
    foreach ($rule in $rules) {
        if ($rule.IsInherited -or
            $rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
            $rule.FileSystemRights -ne [Security.AccessControl.FileSystemRights]::FullControl -or
            $rule.InheritanceFlags -ne [Security.AccessControl.InheritanceFlags]::None -or
            $rule.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None -or
            -not $expected.Remove($rule.IdentityReference.Value)) {
            throw "$Label does not have the exact protected owner/DACL."
        }
    }
    if ($expected.Count -ne 0) {
        throw "$Label does not have the exact protected owner/DACL."
    }
}

function Initialize-ProtectedRunsRoot([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        Assert-NonReparseDirectory $Path 'Physical E2E runs root'
    } else {
        [IO.Directory]::CreateDirectory($Path) | Out-Null
        Assert-NonReparseDirectory $Path 'Physical E2E runs root'
    }
    Set-ProtectedRunItem $Path
    Assert-ExactProtectedRunDirectory $Path 'Physical E2E runs root'
}

function Set-ProtectedRunTree([string]$Path) {
    Set-ProtectedRunItem $Path
    foreach ($item in Get-ChildItem -Force -Recurse -LiteralPath $Path) {
        Set-ProtectedRunItem $item.FullName
    }
}

function Assert-SanitizedState([object]$State) {
    $json = $State | ConvertTo-Json -Depth 6 -Compress
    # PayloadMatrix is a closed phase identifier, not user material. Remove only that
    # exact phase property from the broad content-leak heuristic; every other occurrence
    # of "payload" remains forbidden.
    $inspectionJson = $json -creplace `
        [regex]::Escape('"phase":"PayloadMatrix"'), `
        '"phase":"MatrixPhase"'
    foreach ($forbidden in @('sessionId', 'holder', 'credential', 'capability', 'privateKey', 'seed', 'payload', 'message')) {
        if ($inspectionJson -match [regex]::Escape($forbidden)) { throw 'Run state attempted to contain secret or message material.' }
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

$bootstrap = Assert-AbsoluteExisting $MailboxBootstrapRoot 'Mailbox bootstrap root' -Directory
if ($Phase -ceq 'NegativeRuntime' -and
    $bootstrap -cne [IO.Path]::GetFullPath(
        'C:\Work\DeepSession\secrets\mailbox-bootstrap')) {
    throw 'NegativeRuntime requires the canonical protected mailbox bootstrap root.'
}
$androidRuntime = Assert-AbsoluteExisting (Join-Path $bootstrap 'runtime\android') 'Android runtime root' -Directory
$windowsRuntime = Assert-AbsoluteExisting (Join-Path $bootstrap 'runtime\windows') 'Windows runtime root' -Directory
$windowsAppData = Assert-AbsoluteExisting (Join-Path $bootstrap 'windows') 'Windows app data root' -Directory
$windowsLiveRuntime = Assert-AbsoluteExisting (Join-Path $windowsAppData 'mailbox-runtime-v1') 'Live Windows runtime' -Directory
$windowsLiveRuntimeHashBefore = Get-TreeSha256 $windowsLiveRuntime
$windowsIssuedRuntimeHash = Get-TreeSha256 $windowsRuntime
if ($windowsLiveRuntimeHashBefore -cne $windowsIssuedRuntimeHash) {
    throw 'Live Windows mailbox runtime does not match the currently issued runtime. Publish it before physical E2E.'
}
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
$policyObject = Get-Content -Raw -LiteralPath $policy | ConvertFrom-Json
if ($policyObject.sourceCommitSha -notmatch '^[0-9a-f]{40}$' -or
    $policyObject.sourceCommitSha -cne $sourceCommit -or
    $policyObject.application.packageId -cne $androidPackage -or
    $policyObject.signature.publicKeySha256 -cne $MrXPublicKeySha256) {
    throw 'Approved lab policy does not bind this exact DEV lane.'
}
$approvedApk = Resolve-PolicyPinnedFile $policyObject.application.apkRelativePath `
    $policyObject.application.apkSha256 'Approved Android APK'
$approvedAdb = Resolve-PolicyPinnedFile $policyObject.tools.adb.relativePath `
    $policyObject.tools.adb.sha256 'Approved ADB'
$approvedAapt = Resolve-PolicyPinnedFile $policyObject.tools.aapt.relativePath `
    $policyObject.tools.aapt.sha256 'Approved AAPT'
$approvedApksigner = Resolve-PolicyPinnedFile $policyObject.tools.apksigner.relativePath `
    $policyObject.tools.apksigner.sha256 'Approved APK signer'
$approvedWindowsExe = Resolve-PolicyPinnedFile `
    $policyObject.crossPlatform.windowsExecutableRelativePath `
    $policyObject.crossPlatform.windowsExecutableSha256 `
    'Approved Windows executable'
$approvedWindowsOutputDirectory = Resolve-PolicyPinnedDirectory `
    $policyObject.crossPlatform.windowsOutputDirectoryRelativePath `
    $policyObject.crossPlatform.windowsOutputTreeSha256 `
    'Approved Windows output directory'
if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
        (Split-Path -Parent $approvedWindowsExe),
        $approvedWindowsOutputDirectory)) {
    throw 'Approved Windows executable must be an immediate child of the signed output tree.'
}
$adb = $approvedAdb

$runId = [Guid]::NewGuid().ToString('N')
$e2eRunsRoot = Join-Path $bootstrap 'e2e-runs'
Initialize-ProtectedRunsRoot $e2eRunsRoot
$runRoot = Join-Path $e2eRunsRoot $runId
[IO.Directory]::CreateDirectory($runRoot) | Out-Null
Set-ProtectedRunTree $runRoot
$runStatePath = Join-Path $runRoot 'run-state.json'
$artifacts = Join-Path $runRoot 'artifacts'
[IO.Directory]::CreateDirectory($artifacts) | Out-Null
$genericFixture = Join-Path $runRoot "payload-$runId-generic.bin"
$documentFixture = Join-Path $runRoot "payload-$runId-document.pdf"
$imageFixture = Join-Path $runRoot "payload-$runId-image.png"
[IO.File]::WriteAllBytes(
    $genericFixture,
    [Text.UTF8Encoding]::new($false).GetBytes("deep-payload-generic-v1`n0123456789abcdef`n"))
[IO.File]::WriteAllBytes(
    $documentFixture,
    [Text.ASCIIEncoding]::new().GetBytes("%PDF-1.4`n% Deep deterministic physical UAT document v1`n%%EOF`n"))
[IO.File]::WriteAllBytes(
    $imageFixture,
    [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII='))
Set-ProtectedRunTree $runRoot

$productionBefore = Get-PackageSnapshot $productionPackage
try {
    Invoke-AdbQuiet @('start-server') | Out-Null
    $devices = @(& $adb devices | Select-Object -Skip 1 | Where-Object { $_ -ceq "$AndroidSerial`tdevice" })
    if ($devices.Count -ne 1) { throw 'The exact Wi-Fi Android device is not attached.' }
    Get-PackageSnapshot $androidPackage | Out-Null
    Assert-DockerHealthy

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
    [IO.File]::WriteAllText(
        $runStatePath,
        ($state | ConvertTo-Json -Depth 6),
        [Text.UTF8Encoding]::new($false))
    Set-ProtectedRunTree $runRoot

    if ($Execute) {
        $env:DEEP_STRICT_CROSS_PLATFORM_UI = '1'
        $env:DEEP_STRICT_WINDOWS_UI = '1'
        $env:DEEP_MAU2_E2E_PHASE = $Phase
        $env:DEEP_MAU2_E2E_RUN_STATE = $runStatePath
        $env:DEEP_MAU2_E2E_RUNS_ROOT = (Join-Path $bootstrap 'e2e-runs')
        $env:DEEP_E2E_ANDROID_SERIAL = $AndroidSerial
        $env:DEEP_E2E_ADB = $approvedAdb
        $env:DEEP_E2E_ANDROID_APK = $approvedApk
        $env:DEEP_E2E_AAPT = $approvedAapt
        $env:DEEP_E2E_APKSIGNER = $approvedApksigner
        $env:DEEP_E2E_GENERIC_FIXTURE = $genericFixture
        $env:DEEP_E2E_DOCUMENT_FIXTURE = $documentFixture
        $env:DEEP_E2E_IMAGE_FIXTURE = $imageFixture
        $env:DEEP_E2E_ANDROID_POLICY = $policy
        $env:DEEP_E2E_REPOSITORY_ROOT = $repoRoot
        $env:DEEP_MR_X_PUBLIC_KEY_SHA256 = $MrXPublicKeySha256
        $env:DEEP_E2E_ARTIFACTS = $artifacts
        $env:DEEP_E2E_ANDROID_SELECTORS_JSON = New-CanonicalAndroidSelectorsJson
        $env:DEEP_E2E_ANDROID_PICKER_FILE_ID = $AndroidPickerFileId
        $env:DEEP_E2E_ANDROID_PICKER_CONFIRM_ID = $AndroidPickerConfirmId
        $env:DEEP_MAUI_EXE = $approvedWindowsExe
        $env:DEEP_E2E_APPDATA_ROOT = $windowsAppData
        $env:DEEP_E2E_BOOTSTRAP = 'live'
        $env:DEEP_RELEASE_INVOCATION_ID = [Guid]::NewGuid().ToString('N')
        $env:DEEP_TRANSPORT_PROTOCOL = 'authenticated-mau2'
        $env:DEEP_TRANSPORT_OWNERSHIP = 'user-managed'
        $env:DEEP_STORAGE_URL = $null
        $negativeGenerator = Join-Path $devOpsRoot 'scripts\survival-dev-mailbox-negative-runtime.ps1'
        $negativePrepared = $false
        if ($Phase -ceq 'NegativeRuntime') {
            Assert-AbsoluteExisting $negativeGenerator 'Negative runtime generator' | Out-Null
            & powershell -NoProfile -ExecutionPolicy Bypass -File $negativeGenerator `
                -Action Generate -RunRoot $runRoot
            if ($LASTEXITCODE -ne 0) { throw 'Negative runtime fixture generation failed.' }
            $negativePrepared = $true
        }
        # The test itself rechecks its policy/tool/APK pins. This wrapper never emits
        # their paths, holders, sessions, message markers, or native/container logs.
        try {
            $trxPath = Join-Path $artifacts 'physical-phase.trx'
            & dotnet test (Join-Path $repoRoot 'tests\Deep.Client.Maui.UiTests\Deep.Client.Maui.UiTests.csproj') `
                --no-restore `
                --results-directory $artifacts `
                --logger 'trx;LogFileName=physical-phase.trx' `
                --filter 'FullyQualifiedName=Deep.Client.Maui.UiTests.StrictCrossPlatformUiTests.Physical_android_and_windows_exchange_persist_and_decrypt_an_attachment'
            if ($LASTEXITCODE -ne 0) { throw 'Physical MAU2 UI phase failed.' }
            Assert-ExactPhysicalTestResult $trxPath
        } finally {
            if ($negativePrepared) {
                & powershell -NoProfile -ExecutionPolicy Bypass -File $negativeGenerator `
                    -Action Cleanup -RunRoot $runRoot | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Negative runtime fixture cleanup failed.' }
            }
        }
    }
} finally {
    $productionAfter = Get-PackageSnapshot $productionPackage
    if ($productionBefore -cne $productionAfter) { throw 'Production Android package changed during MAU2 E2E.' }
    if ((Get-TreeSha256 $windowsLiveRuntime) -cne $windowsLiveRuntimeHashBefore) {
        throw 'Canonical live Windows runtime changed during MAU2 E2E.'
    }
}

Write-Output "Physical MAU2 phase '$Phase' completed without skipped tests. Sanitized protected run state: $runId"
