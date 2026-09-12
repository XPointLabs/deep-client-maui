[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('ProvisionIdentity', 'Attach', 'GroupText', 'PayloadMatrix')]
    [string]$Phase,
    [string]$AndroidSerial = 'RF8M2082TFF',
    [switch]$Execute
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot '..'))
$devOpsRoot = Join-Path $workspaceRoot 'deep-devops'
$devRoot = Join-Path $devOpsRoot 'artifacts\survival-dev'
$androidEnvironment = Join-Path $devRoot 'client.android.env'
$windowsEnvironment = Join-Path $devRoot 'client.windows.env'
$apk = Join-Path $repoRoot 'src\Deep.Client.Maui\bin\Debug\net10.0-android\network.xpoint.deep.e2e-Signed.apk'
$windowsArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture
$windowsRid = switch ($windowsArchitecture) {
    ([Runtime.InteropServices.Architecture]::X64) { 'win-x64' }
    ([Runtime.InteropServices.Architecture]::Arm64) { 'win-arm64' }
    default { throw "Unsupported local Windows architecture '$windowsArchitecture'." }
}
$windowsExe = Join-Path $repoRoot "src\Deep.Client.Maui\bin\Debug\net10.0-windows10.0.19041.0\$windowsRid\Deep.Client.Maui.exe"
$adb = Join-Path $repoRoot '.secrets\android-lab\tools\adb.exe'
$aapt = Join-Path $repoRoot '.secrets\android-lab\tools\aapt.exe'
$apksigner = Join-Path $repoRoot '.secrets\android-lab\tools\apksigner.bat'
$androidPackage = 'network.xpoint.deep.e2e'
$productionPackage = 'network.xpoint.deep'

function Assert-File([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label is missing. Build the local dev client first."
    }
}

function Read-Environment([string]$Path) {
    Assert-File $Path 'Local dev client handoff'
    $values = [ordered]@{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^(?<key>[A-Z0-9_]+)=(?<value>.*)$') {
            $values[$Matches.key] = $Matches.value
        }
    }
    return $values
}

function Assert-LocalDevHandoff([Collections.IDictionary]$Values, [string]$Role) {
    if ($Values['SURVIVAL_ENV'] -cne 'Development' -or
        $Values['DEEP_TRANSPORT_PROTOCOL'] -cne 'authenticated-mau2' -or
        $Values['DEEP_TRANSPORT_OWNERSHIP'] -cne 'user-managed') {
        throw "$Role local dev handoff is not authenticated-mau2/user-managed."
    }
    foreach ($key in @('XNODE_URLS', 'DEEP_REGISTRY_URL', 'DEEP_FILE_URL', 'DEEP_PUSH_URL')) {
        if ([string]::IsNullOrWhiteSpace([string]$Values[$key])) {
            throw "$Role local dev handoff is incomplete."
        }
    }
}

function Invoke-Adb([string[]]$Arguments) {
    $result = @(& $adb -s $AndroidSerial @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw 'ADB command failed without publishing device output.' }
    return ($result -join "`n").Trim()
}

function Get-PackageSnapshot([string]$Package) {
    $path = @(& $adb -s $AndroidSerial shell pm path $Package 2>&1 |
        ForEach-Object { [string]$_ } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join "`n"
    if ([string]::IsNullOrWhiteSpace($path)) { throw "Required package '$Package' is absent." }
    return $path
}

function Get-InstalledApkSha256 {
    $packagePath = Invoke-Adb @('shell', 'pm', 'path', $androidPackage)
    $matches = @($packagePath -split "`r?`n" | Where-Object { $_ -cmatch '^package:/.+$' })
    if ($matches.Count -ne 1) { return $null }
    $remotePath = $matches[0].Substring('package:'.Length)
    $digest = Invoke-Adb @('shell', 'sha256sum', $remotePath)
    $match = [regex]::Match($digest, '^(?<hash>[a-f0-9]{64})\s')
    return $(if ($match.Success) { $match.Groups['hash'].Value } else { $null })
}

foreach ($path in @($apk, $windowsExe, $adb, $aapt, $apksigner)) {
    Assert-File $path 'Required local dev E2E input'
}
$androidValues = Read-Environment $androidEnvironment
$windowsValues = Read-Environment $windowsEnvironment
Assert-LocalDevHandoff $androidValues 'Android'
Assert-LocalDevHandoff $windowsValues 'Windows'

$status = @(& docker ps --filter 'name=deep-survival-dev-' --format '{{.Names}}|{{.Status}}' 2>&1)
if ($LASTEXITCODE -ne 0 -or $status.Count -ne 12 -or
    @($status | Where-Object { [string]$_ -cnotmatch '^deep-survival-dev-.+\|Up .+ \(healthy\)$' }).Count -ne 0) {
    throw 'The local dev Docker contour is not healthy.'
}

& $adb start-server | Out-Null
$deviceRows = @(& $adb devices | Select-Object -Skip 1 | Where-Object { $_ -ceq "$AndroidSerial`tdevice" })
if ($deviceRows.Count -ne 1) { throw 'The requested physical Android device is not attached.' }
if ((Invoke-Adb @('shell', 'getprop', 'ro.kernel.qemu')) -cne '0') {
    throw 'The local dev lane requires a physical Android device.'
}

$productionBefore = Get-PackageSnapshot $productionPackage
$localApkSha256 = (Get-FileHash -LiteralPath $apk -Algorithm SHA256).Hash.ToLowerInvariant()
$installedSha256 = Get-InstalledApkSha256
if ($installedSha256 -cne $localApkSha256) {
    & $adb -s $AndroidSerial install --no-incremental -r -t $apk | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The isolated E2E APK could not be installed.' }
}
if ((Get-InstalledApkSha256) -cne $localApkSha256) {
    throw 'The installed isolated E2E package differs from the local dev APK.'
}

foreach ($port in @(41545, 41801, 41802, 41803, 41804, 41805, 41806, 41810, 41811, 41820, 41821, 41822, 41823)) {
    & $adb -s $AndroidSerial reverse "tcp:$port" "tcp:$port" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'ADB reverse setup failed.' }
}

$runRoot = Join-Path $devRoot 'e2e-current'
$artifacts = Join-Path $runRoot $Phase.ToLowerInvariant()
$windowsAppData = Join-Path $runRoot 'windows-state'
[IO.Directory]::CreateDirectory($artifacts) | Out-Null
[IO.Directory]::CreateDirectory($windowsAppData) | Out-Null
$genericFixture = Join-Path $runRoot 'local-dev-generic.txt'
$documentFixture = Join-Path $runRoot 'local-dev-document.pdf'
$imageFixture = Join-Path $runRoot 'local-dev-image.png'
[IO.File]::WriteAllText($genericFixture, "Deep local dev deterministic attachment.`n", [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllBytes($documentFixture, [Text.ASCIIEncoding]::new().GetBytes("%PDF-1.4`n% Deep local dev deterministic document`n%%EOF`n"))
[IO.File]::WriteAllBytes($imageFixture, [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII='))

$roles = @(
    'Startup.Status', 'Startup.RuntimeFailureCode', 'StartupResetLocalStateButton',
    'Welcome.DisplayName', 'Welcome.Create', 'AccountHome.Settings', 'Conversations.Root', 'PhysicalE2E.RuntimeReadyMarker',
    'Conversations.ProfileSettings', 'Conversations.NewConversationTop', 'Conversations.ConversationRow',
    'Settings.DeepId', 'Settings.Back', 'Settings.RecoveryPhraseReveal', 'Settings.RecoveryPhrase',
    'Settings.RecoveryPhraseHide', 'StartConversation.NewMessage', 'StartConversation.CreateGroup',
    'StartConversation.AccountId', 'StartConversation.Close', 'NewConversation.SessionId',
    'NewConversation.DisplayName', 'NewConversation.Start', 'NewConversation.Error', 'NewConversation.Back',
    'Chat.Back', 'Chat.Draft', 'Chat.Send', 'Chat.MessageBody', 'Chat.MessageBubble',
    'Chat.Attach', 'Chat.PickFile', 'Chat.PickPhoto', 'Chat.StagedAttachmentFilename',
    'Chat.StagedAttachmentMetadata', 'Chat.AttachmentFilename', 'Chat.AttachmentMetadata',
    'Chat.AttachmentOpen', 'Chat.AttachmentSave', 'Chat.MessageAttachmentOpen',
    'Chat.MessageAttachmentSave', 'Chat.ImagePreview', 'Chat.ImageMetadata', 'Chat.DeliveryStatus',
    'Chat.Retry', 'Chat.Voice', 'Chat.VoicePlayButton', 'Groups.GroupName', 'Groups.MemberAddress',
    'Groups.AddMember', 'Groups.DraftMembers', 'Groups.Create', 'GroupChat.Title', 'GroupChat.Draft',
    'GroupChat.Send', 'GroupChat.MessageBubble', 'GroupChat.MessageBody', 'GroupChat.DeliveryStatus',
    'GroupChat.Error', 'PhysicalE2E.VoicePlaybackState', 'PhysicalE2E.AckCorrelation',
    'Call.Root', 'Call.Status', 'Call.MediaState', 'Call.Microphone', 'Call.MicrophoneState', 'Call.Hangup'
)
$selectors = [ordered]@{}
foreach ($role in $roles) { $selectors[$role] = "$androidPackage`:id/$role" }

$env:DEEP_LOCAL_DEV_CROSS_PLATFORM_UI = '1'
$env:DEEP_STRICT_WINDOWS_UI = '1'
$env:DEEP_MAU2_E2E_PHASE = $Phase
$env:DEEP_E2E_ANDROID_SERIAL = $AndroidSerial
$env:DEEP_E2E_ANDROID_FINGERPRINT = Invoke-Adb @('shell', 'getprop', 'ro.build.fingerprint')
$env:DEEP_E2E_ANDROID_MODEL = Invoke-Adb @('shell', 'getprop', 'ro.product.model')
$env:DEEP_E2E_ADB = $adb
$env:DEEP_E2E_ANDROID_APK = $apk
$env:DEEP_E2E_AAPT = $aapt
$env:DEEP_E2E_APKSIGNER = $apksigner
$env:DEEP_E2E_GENERIC_FIXTURE = $genericFixture
$env:DEEP_E2E_DOCUMENT_FIXTURE = $documentFixture
$env:DEEP_E2E_IMAGE_FIXTURE = $imageFixture
$env:DEEP_E2E_ARTIFACTS = $artifacts
$env:DEEP_E2E_ANDROID_SELECTORS_JSON = $selectors | ConvertTo-Json -Compress
$env:DEEP_E2E_ANDROID_PICKER_FILE_ID = 'android:id/title'
$env:DEEP_E2E_ANDROID_PICKER_CONFIRM_ID = $null
$env:DEEP_MAUI_EXE = $windowsExe
$env:DEEP_E2E_APPDATA_ROOT = $windowsAppData
$env:DEEP_E2E_BOOTSTRAP = 'live'
$env:DEEP_E2E_REPOSITORY_ROOT = $repoRoot
$env:DEEP_TRANSPORT_PROTOCOL = 'authenticated-mau2'
$env:DEEP_TRANSPORT_OWNERSHIP = 'user-managed'

if ($Execute) {
    dotnet test (Join-Path $repoRoot 'tests\Deep.Client.Maui.UiTests\Deep.Client.Maui.UiTests.csproj') `
        --no-restore --configuration Release -m:1 `
        --results-directory $artifacts `
        --logger "trx;LogFileName=local-dev-$($Phase.ToLowerInvariant()).trx" `
        --filter 'FullyQualifiedName=Deep.Client.Maui.UiTests.StrictCrossPlatformUiTests.Local_dev_android_and_windows_exchange_persist_and_decrypt_an_attachment'
    if ($LASTEXITCODE -ne 0) { throw "Local dev physical phase '$Phase' failed." }
}

$productionAfter = Get-PackageSnapshot $productionPackage
if ($productionBefore -cne $productionAfter) {
    throw 'The production Android package changed during local dev E2E.'
}

if ($Execute) {
    Write-Output "Local dev physical phase '$Phase' passed; production Android package was untouched."
} else {
    Write-Output "Local dev physical phase '$Phase' preflight passed; no UI actions were executed."
}
