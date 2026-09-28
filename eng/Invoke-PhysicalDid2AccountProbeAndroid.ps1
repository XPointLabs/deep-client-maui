[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9._:-]{4,80}$')]
    [string]$AndroidSerial,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')]
    [string]$ExpectedCommit,
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string]$ExpectedApkSourceCommit,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')]
    [string]$ExpectedApkSha256,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')]
    [string]$ExpectedSignerSha256,
    [ValidateSet('default', 'android-arm64')]
    [string]$ApkVariant = 'default',
    [ValidateSet('Did2Account', 'Did2Https')]
    [string]$Lane = 'Did2Account',
    [string]$ApkPath,
    [ValidateSet('Install', 'Inspect', 'SetName', 'DismissKeyboard', 'CreateAccount', 'Settings', 'ScrollSettings', 'VerifyNetwork', 'Restart')]
    [string]$Phase = 'Install',
    [switch]$AllowProbeUpdate,
    [switch]$Execute
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$apkRoot = Join-Path $repo 'src\Deep.Client.Maui\bin\Debug\net10.0-android'
$apk = if ($Lane -eq 'Did2Https') {
    if ([string]::IsNullOrWhiteSpace($ApkPath) -or
        -not [IO.Path]::IsPathFullyQualified($ApkPath)) {
        throw 'DID2 HTTPS install requires its explicit built APK path.'
    }
    $full = [IO.Path]::GetFullPath($ApkPath)
    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts\did2-https-android')) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'DID2 HTTPS APK must belong to the supported build artifact lane.'
    }
    for ($cursor = $full; -not [string]::IsNullOrWhiteSpace($cursor);
        $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if ((Test-Path -LiteralPath $cursor) -and
            (((Get-Item -LiteralPath $cursor -Force).Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw 'DID2 HTTPS APK must not traverse a reparse point.'
        }
        if ([IO.Path]::GetDirectoryName($cursor) -ceq $cursor) { break }
    }
    $full
} elseif (-not [string]::IsNullOrWhiteSpace($ApkPath)) {
    throw 'An arbitrary APK path is not accepted by the account-only lane.'
} elseif ($ApkVariant -eq 'android-arm64') {
    Join-Path $apkRoot 'android-arm64\network.xpoint.deep.did2probe-Signed.apk'
} else {
    Join-Path $apkRoot 'network.xpoint.deep.did2probe-Signed.apk'
}
$sdk = 'C:\Program Files (x86)\Android\android-sdk'
$adb = Join-Path $sdk 'platform-tools\adb.exe'
$aapt = Join-Path $sdk 'build-tools\36.0.0\aapt.exe'
$apksignerJar = Join-Path $sdk 'build-tools\36.0.0\lib\apksigner.jar'
$java = 'C:\Program Files\Android\openjdk\jdk-21.0.8\bin\java.exe'
$probePackage = if ($Lane -eq 'Did2Https') { 'network.xpoint.deep.did2https' } else { 'network.xpoint.deep.did2probe' }
$protectedPackages = @('network.xpoint.deep', 'network.xpoint.deep.e2e',
    $(if ($Lane -eq 'Did2Https') { 'network.xpoint.deep.did2probe' } else { 'network.xpoint.deep.did2https' }))

function Invoke-Bounded([string]$File, [string[]]$Arguments,
    [string]$Label, [int]$DeadlineMs = 30000) {
    $start = [Diagnostics.ProcessStartInfo]::new($File)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { [void]$start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw "$Label did not start." }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($DeadlineMs)) {
            $process.Kill($true)
            throw "$Label exceeded its deadline."
        }
        $output = $stdout.GetAwaiter().GetResult()
        $errorText = $stderr.GetAwaiter().GetResult()
        if ($output.Length + $errorText.Length -gt 1048576) {
            throw "$Label returned excessive output."
        }
        if ($process.ExitCode -ne 0) {
            throw "$Label failed with exit code $($process.ExitCode)."
        }
        return $output.Trim()
    } finally { $process.Dispose() }
}

function Invoke-Adb([string[]]$Arguments, [string]$Label,
    [int]$DeadlineMs = 30000) {
    return Invoke-Bounded $adb (@('-s', $AndroidSerial) + $Arguments) `
        $Label $DeadlineMs
}

function Get-TextHash([string]$Value) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
    return [Convert]::ToHexStringLower(
        [Security.Cryptography.SHA256]::HashData($bytes))
}

function Get-PackageSnapshot([string]$Package) {
    $packages = Invoke-Adb @('shell', 'pm', 'list', 'packages', $Package) `
        "Package list $Package"
    if ($packages -notmatch "(?m)^package:$([regex]::Escape($Package))`r?$") {
        return [ordered]@{
            installed = $false
            pathSha256 = $null
            metadataSha256 = $null
        }
    }
    $path = Invoke-Adb @('shell', 'pm', 'path', $Package) "Package path $Package"
    if ([string]::IsNullOrWhiteSpace($path)) {
        throw "Installed package $Package has no readable path."
    }
    $details = Invoke-Adb @('shell', 'dumpsys', 'package', $Package) `
        "Package metadata $Package"
    $marker = [regex]::Escape($Package)
    $section = [regex]::Match($details,
        "(?ms)^  Package \[$marker\] \([^)]+\):`r?`n(?<body>.*?)(?=^  Package \[|\z)")
    if (-not $section.Success) {
        throw "Installed package $Package has no exact metadata section."
    }
    $stable = [ordered]@{}
    foreach ($name in @('userId', 'codePath', 'resourcePath', 'versionCode',
        'versionName', 'dataDir', 'timeStamp', 'firstInstallTime',
        'lastUpdateTime')) {
        $field = [regex]::Match($section.Groups['body'].Value,
            "(?m)^    $name=(.+)`r?$")
        if (-not $field.Success) {
            throw "Installed package $Package lacks required $name metadata."
        }
        $stable[$name] = $field.Groups[1].Value.Trim()
    }
    $dataInode = [regex]::Match($section.Groups['body'].Value,
        '(?m)^    User 0: ceDataInode=([0-9]+)')
    if (-not $dataInode.Success) {
        throw "Installed package $Package lacks a user-0 data inode."
    }
    $stable['ceDataInode'] = $dataInode.Groups[1].Value
    return [ordered]@{
        installed = $true
        pathSha256 = Get-TextHash $path
        metadataSha256 = Get-TextHash ($stable | ConvertTo-Json -Compress)
    }
}

function Assert-SameSnapshot($Before, $After, [string]$Package) {
    if ($Before.installed -ne $After.installed -or
        $Before.pathSha256 -cne $After.pathSha256 -or
        $Before.metadataSha256 -cne $After.metadataSha256) {
        throw "Protected package $Package changed during DID2 probe."
    }
}

function Read-ProbeUi {
    $focus = Invoke-Adb @('shell', 'dumpsys', 'window') 'DID2 foreground owner'
    if ($focus -notmatch "mCurrentFocus=.*$([regex]::Escape($probePackage))/") {
        throw 'The exact DID2 package does not own the foreground window.'
    }
    $raw = Invoke-Adb @('exec-out', 'uiautomator', 'dump', '/dev/tty') 'DID2 UI inspection'
    $start = $raw.IndexOf('<hierarchy', [StringComparison]::Ordinal)
    $end = $raw.LastIndexOf('</hierarchy>', [StringComparison]::Ordinal)
    if ($start -lt 0 -or $end -lt $start) { throw 'DID2 UI hierarchy is unavailable.' }
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 1048576
    $reader = [Xml.XmlReader]::Create([IO.StringReader]::new($raw.Substring($start, $end + 12 - $start)), $settings)
    try { $document = [Xml.XmlDocument]::new(); $document.XmlResolver = $null; $document.Load($reader) }
    finally { $reader.Dispose() }
    $nodes = @($document.SelectNodes('//node') | Where-Object { $_.GetAttribute('package') -ceq $probePackage })
    if ($nodes.Count -eq 0 -or $nodes.Count -gt 10000) { throw 'DID2 UI node bounds rejected.' }
    $controls = @()
    foreach ($id in @('Welcome.DisplayName', 'Welcome.CreateAccount', 'Did2Workspace.Settings', 'Did2Workspace.MobileSettings',
        'Did2Probe.VerifyNetwork', 'Settings.Identity', 'Settings.RecoveryPhraseStatus', 'Did2Probe.NetworkStatus')) {
        $matches = @($nodes | Where-Object {
            $_.GetAttribute('resource-id') -ceq "${probePackage}:id/$id" -or
            $_.GetAttribute('content-desc') -ceq $id
        })
        if ($matches.Count -eq 1) {
            $controls += [ordered]@{id=$id; enabled=($matches[0].GetAttribute('enabled') -ceq 'true'); bounds=$matches[0].GetAttribute('bounds')}
        }
    }
    $text = ($nodes | ForEach-Object { $_.GetAttribute('text') }) -join "`n"
    $failure = [regex]::Match($text, 'DID2 (AccountProof|NetworkVerification|PreKeyStaging|PreKeyPublication) failed \(([A-Za-z0-9; ]+)\)')
    return [pscustomobject]@{
        Nodes=$nodes
        ImeShowing=($focus -match 'mImeShowing=true')
        Summary=[ordered]@{
            controls=$controls
            keyboardShown=($focus -match 'mImeShowing=true')
            accountVisible=($text -match '(?m)^deep1[a-z0-9]+$')
            recoveryRetained=($text.Contains('Зашифрованная копия хранится на этом устройстве.'))
            verifying=($text.Contains('Проверяем подписанный каталог и регистрацию'))
            stageFailure=$(if ($failure.Success) { $failure.Value } else { $null })
        }
    }
}

function Click-ProbeControl($Ui, [string]$Id) {
    if ($Ui.ImeShowing -and $Id -cne 'Welcome.DisplayName') {
        throw 'Dismiss the observed probe keyboard before tapping another control.'
    }
    $nodes = @($Ui.Nodes | Where-Object {
        ($_.GetAttribute('resource-id') -ceq "${probePackage}:id/$Id" -or
            $_.GetAttribute('content-desc') -ceq $Id) -and $_.GetAttribute('enabled') -ceq 'true'
    })
    if ($nodes.Count -ne 1) { throw 'An exact enabled DID2 control is required.' }
    $bounds = [regex]::Match($nodes[0].GetAttribute('bounds'), '^\[(\d{1,5}),(\d{1,5})\]\[(\d{1,5}),(\d{1,5})\]$')
    if (-not $bounds.Success) { throw 'DID2 control bounds are invalid.' }
    $left = [int]$bounds.Groups[1].Value; $top = [int]$bounds.Groups[2].Value
    $right = [int]$bounds.Groups[3].Value; $bottom = [int]$bounds.Groups[4].Value
    if ($right -le $left -or $bottom -le $top -or $right -gt 20000 -or $bottom -gt 20000) {
        throw 'DID2 control bounds are outside the screen limit.'
    }
    [void](Invoke-Adb @('shell', 'input', 'tap', [string][int](($left+$right)/2), [string][int](($top+$bottom)/2)) 'DID2 control tap')
}

foreach ($tool in @($adb, $aapt, $apksignerJar, $java, $apk)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) {
        throw 'A required pinned Android probe tool or APK is unavailable.'
    }
}
$commit = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -cne $ExpectedCommit) {
    throw 'DID2 probe source commit does not match the expected revision.'
}
$apkSourceCommit = if ([string]::IsNullOrWhiteSpace($ExpectedApkSourceCommit)) { $commit } else { $ExpectedApkSourceCommit }
if ($apkSourceCommit -cne $commit) {
    & git -C $repo merge-base --is-ancestor $apkSourceCommit $commit
    if ($LASTEXITCODE -ne 0) { throw 'APK source must be a committed ancestor of this harness.' }
    $changed = @(& git -C $repo diff --name-only $apkSourceCommit $commit)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to check APK/harness source separation.' }
    $harnessOnly = @('eng/Invoke-PhysicalDid2AccountProbeAndroid.ps1',
        'tests/Deep.Client.Maui.Clean.Tests/CleanStartupCompositionTests.cs',
        'docs/ARCHITECTURE.md', 'docs/DID2-HTTPS-DEVICE-2026-09-28.md')
    if ($Lane -ne 'Did2Https' -or $Phase -eq 'Install' -or
        @($changed | Where-Object { $_ -cnotin $harnessOnly }).Count -ne 0) {
        throw 'Older APKs are allowed only for UI phases with strictly harness/documentation-only changes.'
    }
}
$dirty = @(& git -C $repo status --porcelain)
if ($LASTEXITCODE -ne 0 -or $dirty.Count -ne 0) {
    throw 'DID2 probe requires a clean committed MAUI worktree.'
}
$apkSha256 = (Get-FileHash -LiteralPath $apk -Algorithm SHA256).Hash.ToLowerInvariant()
if ($apkSha256 -cne $ExpectedApkSha256) {
    throw 'DID2 probe APK hash differs from the approved preflight input.'
}
$badging = Invoke-Bounded $aapt @('dump', 'badging', $apk) 'APK manifest'
if ($badging -notmatch "(?m)^package: name='$([regex]::Escape($probePackage))'") {
    throw 'DID2 probe APK has an unexpected application ID.'
}
$signer = Invoke-Bounded $java @('-jar', $apksignerJar, 'verify',
    '--print-certs', $apk) 'APK signature'
if ($signer -notmatch 'Signer #1 certificate SHA-256 digest: ([0-9a-f]{64})' -or
    $Matches[1] -cne $ExpectedSignerSha256) {
    throw 'DID2 probe APK signer differs from the approved preflight input.'
}
$devices = Invoke-Bounded $adb @('devices') 'ADB device list'
if ($devices -notmatch "(?m)^$([regex]::Escape($AndroidSerial))`tdevice\r?$") {
    throw 'The exact Android serial is not attached and authorized.'
}

$before = [ordered]@{}
foreach ($package in $protectedPackages) {
    $before[$package] = Get-PackageSnapshot $package
}
$probeBefore = Get-PackageSnapshot $probePackage
if ($Phase -ne 'Install' -and ($Lane -ne 'Did2Https' -or -not $probeBefore.installed)) {
    throw 'UI phases require the installed dedicated DID2 HTTPS package.'
}
if ($Phase -eq 'Install' -and $probeBefore.installed -and -not $AllowProbeUpdate) {
    throw 'The dedicated DID2 probe package already exists; explicit -AllowProbeUpdate is required.'
}
$result = [ordered]@{
    schema = $(if ($Lane -eq 'Did2Https') { 'deep.did2-https-android-diagnostic.v1' } else { 'deep.did2-account-android-probe.v1' })
    commit = $apkSourceCommit
    harnessCommit = $commit
    apkSha256 = $apkSha256
    signerSha256 = $ExpectedSignerSha256
    androidSerial = $AndroidSerial
    package = $probePackage
    lane = $Lane
    phase = $Phase
    apkVariant = $ApkVariant
    execute = [bool]$Execute
    allowProbeUpdate = [bool]$AllowProbeUpdate
    status = 'preflight'
    deviceDeliveryVerified = $false
    protectedBefore = $before
    probeBefore = $probeBefore
}
if ($Execute) {
    try {
        if ($Phase -eq 'Install') {
        $installArguments = if ($probeBefore.installed) {
            @('install', '-r', $apk)
        } else {
            @('install', $apk)
        }
        $install = Invoke-Adb $installArguments 'Dedicated DID2 probe install' 120000
        if ($install -notmatch 'Success') {
            throw 'The dedicated DID2 probe install was not acknowledged.'
        }
        $launch = Invoke-Adb @('shell', 'monkey', '-p', $probePackage,
            '-c', 'android.intent.category.LAUNCHER', '1') `
            'Dedicated DID2 probe launch' 30000
        if ($launch -notmatch 'Events injected: 1') {
            throw 'The dedicated DID2 probe launch was not acknowledged.'
        }
        $result.status = 'installed-and-launched'
        } elseif ($Phase -eq 'Restart') {
            [void](Invoke-Adb @('shell', 'am', 'force-stop', $probePackage) 'Stop dedicated DID2 HTTPS process')
            [void](Invoke-Adb @('shell', 'monkey', '-p', $probePackage, '-c', 'android.intent.category.LAUNCHER', '1') 'Restart dedicated DID2 HTTPS process')
            $result.status = 'restarted-without-reset'
        } else {
            $ui = Read-ProbeUi
            $result.uiBefore = $ui.Summary
            switch ($Phase) {
                'Inspect' { }
                'SetName' {
                    Click-ProbeControl $ui 'Welcome.DisplayName'
                    $focused = Read-ProbeUi
                    $field = @($focused.Nodes | Where-Object {
                        $_.GetAttribute('focused') -ceq 'true' -and
                        ($_.GetAttribute('resource-id') -ceq "${probePackage}:id/Welcome.DisplayName" -or
                            $_.GetAttribute('content-desc') -ceq 'Welcome.DisplayName')
                    })
                    if ($field.Count -ne 1) { throw 'DID2 name field did not receive focus.' }
                    [void](Invoke-Adb @('shell', 'input', 'text', 'Android%sHTTPS%sQA') 'Fill disposable DID2 test name')
                }
                'DismissKeyboard' {
                    if (-not $ui.ImeShowing) { throw 'The exact probe must have a shown keyboard.' }
                    [void](Invoke-Adb @('shell', 'input', 'keyevent', '4') 'Dismiss observed probe keyboard')
                }
                'CreateAccount' { Click-ProbeControl $ui 'Welcome.CreateAccount' }
                'Settings' {
                    $settingsId = if (@($ui.Summary.controls | Where-Object { $_.id -ceq 'Did2Workspace.MobileSettings' }).Count -eq 1) {
                        'Did2Workspace.MobileSettings'
                    } else { 'Did2Workspace.Settings' }
                    Click-ProbeControl $ui $settingsId
                }
                'ScrollSettings' {
                    if ($ui.ImeShowing) { throw 'Dismiss the probe keyboard before scrolling settings.' }
                    $pane = @($ui.Nodes | Where-Object {
                        $_.GetAttribute('resource-id') -ceq "${probePackage}:id/Page.Settings" -and
                        $_.GetAttribute('scrollable') -ceq 'true' -and
                        $_.GetAttribute('class') -ceq 'android.widget.ScrollView'
                    })
                    if ($pane.Count -ne 1) { throw 'One exact owned vertical settings pane is required.' }
                    $bounds = [regex]::Match($pane[0].GetAttribute('bounds'), '^\[(\d{1,5}),(\d{1,5})\]\[(\d{1,5}),(\d{1,5})\]$')
                    if (-not $bounds.Success) { throw 'Settings pane bounds are invalid.' }
                    $left=[int]$bounds.Groups[1].Value; $top=[int]$bounds.Groups[2].Value
                    $right=[int]$bounds.Groups[3].Value; $bottom=[int]$bounds.Groups[4].Value
                    if ($right -le $left -or $bottom - $top -lt 200 -or $right -gt 20000 -or $bottom -gt 20000) {
                        throw 'Settings pane bounds are outside the screen limit.'
                    }
                    $x=[string][int](($left+$right)/2)
                    [void](Invoke-Adb @('shell', 'input', 'swipe', $x, [string]($bottom-100),
                        $x, [string]($top+100), '450') 'Scroll exact owned settings pane')
                }
                'VerifyNetwork' { Click-ProbeControl $ui 'Did2Probe.VerifyNetwork' }
            }
            $result.uiAfter = (Read-ProbeUi).Summary
            $result.status = 'ui-phase-observed'
        }
    } finally {
        $after = [ordered]@{}
        foreach ($package in $protectedPackages) {
            $after[$package] = Get-PackageSnapshot $package
            Assert-SameSnapshot $before[$package] $after[$package] $package
        }
        $result.protectedAfter = $after
        $result.probeInstalled = (Get-PackageSnapshot $probePackage).installed
    }
}
$result | ConvertTo-Json -Depth 6
