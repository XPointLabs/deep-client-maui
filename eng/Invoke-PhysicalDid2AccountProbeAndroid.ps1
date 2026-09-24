[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9._:-]{4,80}$')]
    [string]$AndroidSerial,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')]
    [string]$ExpectedCommit,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')]
    [string]$ExpectedApkSha256,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')]
    [string]$ExpectedSignerSha256,
    [switch]$AllowProbeUpdate,
    [switch]$Execute
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$apk = Join-Path $repo 'src\Deep.Client.Maui\bin\Debug\net10.0-android\network.xpoint.deep.did2probe-Signed.apk'
$sdk = 'C:\Program Files (x86)\Android\android-sdk'
$adb = Join-Path $sdk 'platform-tools\adb.exe'
$aapt = Join-Path $sdk 'build-tools\36.0.0\aapt.exe'
$apksignerJar = Join-Path $sdk 'build-tools\36.0.0\lib\apksigner.jar'
$java = 'C:\Program Files\Android\openjdk\jdk-21.0.8\bin\java.exe'
$probePackage = 'network.xpoint.deep.did2probe'
$protectedPackages = @('network.xpoint.deep', 'network.xpoint.deep.e2e')

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

foreach ($tool in @($adb, $aapt, $apksignerJar, $java, $apk)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) {
        throw 'A required pinned Android probe tool or APK is unavailable.'
    }
}
$commit = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -cne $ExpectedCommit) {
    throw 'DID2 probe source commit does not match the expected revision.'
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
if ($badging -notmatch "(?m)^package: name='network\.xpoint\.deep\.did2probe'") {
    throw 'DID2 probe APK has an unexpected application ID.'
}
$signer = Invoke-Bounded $java @('-jar', $apksignerJar, 'verify',
    '--print-certs', $apk) 'APK signature'
if ($signer -notmatch 'Signer #1 certificate SHA-256 digest: ([0-9a-f]{64})' -or
    $Matches[1] -cne $ExpectedSignerSha256) {
    throw 'DID2 probe APK signer differs from the approved preflight input.'
}
$devices = Invoke-Bounded $adb @('devices') 'ADB device list'
if ($devices -notmatch "(?m)^$([regex]::Escape($AndroidSerial))`tdevice$") {
    throw 'The exact Android serial is not attached and authorized.'
}

$before = [ordered]@{}
foreach ($package in $protectedPackages) {
    $before[$package] = Get-PackageSnapshot $package
}
$probeBefore = Get-PackageSnapshot $probePackage
if ($probeBefore.installed -and -not $AllowProbeUpdate) {
    throw 'The dedicated DID2 probe package already exists; explicit -AllowProbeUpdate is required.'
}
$result = [ordered]@{
    schema = 'deep.did2-account-android-probe.v1'
    commit = $commit
    apkSha256 = $apkSha256
    signerSha256 = $ExpectedSignerSha256
    androidSerial = $AndroidSerial
    package = $probePackage
    execute = [bool]$Execute
    allowProbeUpdate = [bool]$AllowProbeUpdate
    status = 'preflight'
    protectedBefore = $before
    probeBefore = $probeBefore
}
if ($Execute) {
    try {
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
