[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('ExportHolder', 'StageRuntime')]
    [string]$Action,
    [string]$AndroidSerial,
    [string]$AdbPath = $env:DEEP_ADB_PATH,
    [string]$OutputPath,
    [string]$RuntimeRoot,
    [ValidatePattern('^[0-9a-f]{64}$')]
    [string]$MrXPublicKeySha256 = $env:DEEP_MR_X_PUBLIC_KEY_SHA256
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$package = 'network.xpoint.deep.e2e'

function Resolve-Adb {
    if (-not [string]::IsNullOrWhiteSpace($AdbPath) -and
        (Test-Path -LiteralPath $AdbPath -PathType Leaf)) {
        return (Resolve-Path -LiteralPath $AdbPath).Path
    }
    $command = Get-Command adb -ErrorAction SilentlyContinue
    if ($null -ne $command) { return $command.Source }
    foreach ($candidate in @(
        "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe",
        "$env:ANDROID_HOME\platform-tools\adb.exe",
        "$env:ANDROID_SDK_ROOT\platform-tools\adb.exe",
        "${env:ProgramFiles(x86)}\Android\android-sdk\platform-tools\adb.exe",
        "$env:ProgramFiles\Android\android-sdk\platform-tools\adb.exe")) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and
            (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    throw 'adb was not found. Set -AdbPath or DEEP_ADB_PATH.'
}

function ConvertTo-NativeArguments([string[]]$Arguments) {
    return (@($Arguments | ForEach-Object {
        if ($null -eq $_ -or $_ -match '[\x00\r\n"]') {
            throw 'Native process argument is not canonical.'
        }
        if ($_ -match '\s') { '"' + $_ + '"' } else { $_ }
    }) -join ' ')
}

function ConvertTo-RemoteShellArgument([string]$Command) {
    if ([string]::IsNullOrWhiteSpace($Command) -or $Command.Contains("'")) {
        throw 'Remote shell command is not canonical.'
    }
    return "'$Command'"
}

function Invoke-AdbCapture([string[]]$Arguments) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $script:adb
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Arguments = ConvertTo-NativeArguments $Arguments
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) { throw 'Could not start adb.' }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        return [pscustomobject]@{
            exitCode = $process.ExitCode
            stdout = $stdoutTask.GetAwaiter().GetResult()
            stderr = $stderrTask.GetAwaiter().GetResult()
        }
    } finally {
        $process.Dispose()
    }
}

function Invoke-AdbChecked([string[]]$Arguments) {
    $result = Invoke-AdbCapture $Arguments
    if ($result.exitCode -ne 0) {
        throw "adb failed (exit $($result.exitCode), command=$($Arguments -join ' ')): $($result.stderr.Trim())"
    }
    return @($result.stdout -split '\r?\n' | Where-Object { $_.Length -ne 0 })
}

function Get-PackagePathSnapshot([string]$PackageName) {
    $result = Invoke-AdbCapture @('-s', $AndroidSerial, 'shell', 'pm', 'path', $PackageName)
    $value = $result.stdout.Trim()
    if ($result.exitCode -ne 0) {
        throw "adb package snapshot failed (exit $($result.exitCode)): $($result.stderr.Trim())"
    }
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw 'adb package snapshot returned no canonical path.'
    }
    return $value
}

function Copy-ToAppPrivate(
    [Parameter(Mandatory)][string]$LocalPath,
    [Parameter(Mandatory)][string]$RemotePath) {
    Invoke-AdbChecked @(
        '-s', $AndroidSerial, 'shell', 'run-as', $package,
        'rm', '-f', $RemotePath) | Out-Null
    Invoke-AdbChecked @(
        '-s', $AndroidSerial, 'shell', 'run-as', $package, 'touch', $RemotePath) | Out-Null
    Invoke-AdbChecked @(
        '-s', $AndroidSerial, 'shell', 'run-as', $package,
        'chmod', '600', $RemotePath) | Out-Null
    $source = [IO.File]::OpenRead($LocalPath)
    $rawBuffer = [byte[]]::new(384)
    $encodedCharacters = [char[]]::new(512)
    $encodedBytes = [byte[]]::new(512)
    try {
        $endOfSource = $false
        while (-not $endOfSource) {
            $count = 0
            while ($count -lt $rawBuffer.Length) {
                $read = $source.Read(
                    $rawBuffer, $count, $rawBuffer.Length - $count)
                if ($read -eq 0) {
                    $endOfSource = $true
                    break
                }
                $count += $read
            }
            if ($count -eq 0) { break }
            for ($index = 0; $index -lt $encodedBytes.Length; $index++) {
                $encodedBytes[$index] = 0x20
            }
            $encodedCount = [Convert]::ToBase64CharArray(
                $rawBuffer, 0, $count, $encodedCharacters, 0)
            for ($index = 0; $index -lt $encodedCount; $index++) {
                $encodedBytes[$index] = [byte]$encodedCharacters[$index]
            }
            $startInfo = [Diagnostics.ProcessStartInfo]::new()
            $startInfo.FileName = $script:adb
            $startInfo.UseShellExecute = $false
            $startInfo.RedirectStandardInput = $true
            $startInfo.RedirectStandardOutput = $true
            $startInfo.RedirectStandardError = $true
            $appendCommand = ConvertTo-RemoteShellArgument (
                "base64 -di >> $RemotePath")
            $startInfo.Arguments = ConvertTo-NativeArguments @(
                '-s', $AndroidSerial, 'shell', 'run-as', $package,
                'sh', '-c', $appendCommand)
            $process = [Diagnostics.Process]::new()
            $process.StartInfo = $startInfo
            try {
                if (-not $process.Start()) {
                    throw 'Could not start the app-private ADB chunk stream.'
                }
                $process.StandardInput.BaseStream.Write(
                    $encodedBytes, 0, $encodedBytes.Length)
                $process.StandardInput.BaseStream.Flush()
                $process.StandardInput.Close()
                $stdoutTask = $process.StandardOutput.ReadToEndAsync()
                $stderrTask = $process.StandardError.ReadToEndAsync()
                $process.WaitForExit()
                $stdout = $stdoutTask.GetAwaiter().GetResult()
                $stderr = $stderrTask.GetAwaiter().GetResult()
                if ($process.ExitCode -ne 0) {
                    throw "App-private ADB chunk stream failed: $stdout$stderr"
                }
            } finally {
                $process.Dispose()
            }
            [Array]::Clear($rawBuffer, 0, $rawBuffer.Length)
            [Array]::Clear($encodedCharacters, 0, $encodedCharacters.Length)
            [Array]::Clear($encodedBytes, 0, $encodedBytes.Length)
        }
        Invoke-AdbChecked @(
            '-s', $AndroidSerial, 'shell', 'run-as', $package,
            'chmod', '600', $RemotePath) | Out-Null
    } finally {
        [Array]::Clear($rawBuffer, 0, $rawBuffer.Length)
        [Array]::Clear($encodedCharacters, 0, $encodedCharacters.Length)
        [Array]::Clear($encodedBytes, 0, $encodedBytes.Length)
        $source.Dispose()
    }
}

function Assert-NoReparseTree([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if (Test-Path -LiteralPath $current) {
            if (((Get-Item -Force -LiteralPath $current).Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Mailbox runtime path must not traverse a reparse point.'
            }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -ceq $current) { break }
        $current = $parent
    }
    foreach ($item in Get-ChildItem -Force -Recurse -LiteralPath $Path) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Mailbox runtime tree must not contain a reparse point.'
        }
    }
}

function Get-RelativeChildPath(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][string]$Child) {
    $canonicalRoot = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $canonicalChild = [IO.Path]::GetFullPath($Child)
    $prefix = $canonicalRoot + [IO.Path]::DirectorySeparatorChar
    $comparison = if ($env:OS -ceq 'Windows_NT') {
        [StringComparison]::OrdinalIgnoreCase
    } else {
        [StringComparison]::Ordinal
    }
    if (-not $canonicalChild.StartsWith($prefix, $comparison)) {
        throw 'Mailbox runtime enumeration escaped its canonical root.'
    }
    return $canonicalChild.Substring($prefix.Length).Replace('\', '/')
}

$adb = Resolve-Adb
Invoke-AdbChecked @('start-server') | Out-Null
$wifiDevices = @(& $adb devices | Select-Object -Skip 1 | ForEach-Object {
    if ($_ -match '^(?<serial>\S+:\d+)\s+device(?:\s|$)') { $Matches.serial }
})
if ([string]::IsNullOrWhiteSpace($AndroidSerial)) {
    if ($wifiDevices.Count -ne 1) {
        throw "Expected exactly one connected Wi-Fi ADB device; found $($wifiDevices.Count)."
    }
    $AndroidSerial = $wifiDevices[0]
} elseif ($AndroidSerial -notin $wifiDevices) {
    throw 'The selected Android serial is not a connected Wi-Fi ADB device.'
}

if ($Action -eq 'ExportHolder') {
    $raw = @(Invoke-AdbChecked @(
        '-s', $AndroidSerial, 'shell', 'run-as', $package,
        'cat', 'files/mailbox-holder-bootstrap-v1/android.holder.v1.json')) -join "`n"
    $holder = $raw | ConvertFrom-Json
    $properties = @($holder.PSObject.Properties.Name | Sort-Object)
    if (($properties -join ',') -cne
        'developmentOnly,ed25519PublicKey,platform,schemaVersion,sessionId' -or
        $holder.schemaVersion -ne 1 -or -not $holder.developmentOnly -or
        [string]$holder.platform -cne 'android' -or
        [string]$holder.sessionId -cnotmatch '^05[0-9a-f]{64}$' -or
        [string]$holder.ed25519PublicKey -cnotmatch '^[0-9a-f]{64}$') {
        throw 'The app-private Android holder bootstrap record is invalid.'
    }
    $canonical = $holder | ConvertTo-Json -Compress
    if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        $canonical
    } else {
        $destination = [IO.Path]::GetFullPath($OutputPath)
        $parent = [IO.Path]::GetDirectoryName($destination)
        if ([string]::IsNullOrWhiteSpace($parent) -or
            -not (Test-Path -LiteralPath $parent -PathType Container) -or
            ((Get-Item -Force -LiteralPath $parent).Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Holder output parent must be an existing non-reparse directory.'
        }
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($canonical)
        try {
            $stream = [IO.FileStream]::new(
                $destination,
                [IO.FileMode]::Create,
                [IO.FileAccess]::Write,
                [IO.FileShare]::None)
            try {
                $stream.Write($bytes, 0, $bytes.Length)
                $stream.Flush($true)
            } finally {
                $stream.Dispose()
            }
        } finally {
            [Array]::Clear($bytes, 0, $bytes.Length)
        }
    }
    exit 0
}

if ([string]::IsNullOrWhiteSpace($RuntimeRoot) -or
    -not (Test-Path -LiteralPath $RuntimeRoot -PathType Container)) {
    throw 'StageRuntime requires an existing -RuntimeRoot directory.'
}
if ($MrXPublicKeySha256 -cnotmatch '^[0-9a-f]{64}$') {
    throw 'StageRuntime requires the exact lowercase Mr. X public-key SHA-256 pin.'
}
$root = [IO.Path]::GetFullPath($RuntimeRoot)
Assert-NoReparseTree $root
$activationPath = Join-Path $root 'activation.v1.json'
$pointerPath = Join-Path $root 'pair\current-generation.json'
foreach ($required in @(
    $activationPath,
    (Join-Path $root 'authority.public.json'),
    (Join-Path $root 'revocations.v1.json'),
    (Join-Path $root 'mr-x-mailbox-policy.payload.json'),
    (Join-Path $root 'mr-x-mailbox-policy.signature'),
    (Join-Path $root 'mr-x-mailbox-policy.public-key'),
    $pointerPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Mailbox runtime is missing required file: $required"
    }
}
$activation = Get-Content -Raw -LiteralPath $activationPath | ConvertFrom-Json
$pointer = Get-Content -Raw -LiteralPath $pointerPath | ConvertFrom-Json
$generation = [string]$pointer.generation
if ($activation.schemaVersion -ne 1 -or -not $activation.developmentOnly -or
    [string]$activation.platform -cne 'android' -or
    $generation -cnotmatch '^[0-9a-f]{64}$' -or
    [string]$activation.pairGeneration -cne $generation -or
    [string]$activation.pairManifestSha256 -cne [string]$pointer.pairManifestSha256) {
    throw 'Mailbox runtime activation and pair pointer do not match Android DEV schema v1.'
}
$generationRoot = Join-Path $root "pair\generations\$generation"
$relativeFiles = @(
    'activation.v1.json',
    'authority.public.json',
    'revocations.v1.json',
    'mr-x-mailbox-policy.payload.json',
    'mr-x-mailbox-policy.signature',
    'mr-x-mailbox-policy.public-key',
    'pair/current-generation.json',
    "pair/generations/$generation/android.mailbox-credentials.v1.json",
    "pair/generations/$generation/windows.mailbox-credentials.v1.json",
    "pair/generations/$generation/pair-manifest.v1.json")
foreach ($relative in $relativeFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $relative) -PathType Leaf)) {
        throw "Mailbox runtime generation is incomplete: $relative"
    }
}
$publicKeyPath = Join-Path $root 'mr-x-mailbox-policy.public-key'
$signaturePath = Join-Path $root 'mr-x-mailbox-policy.signature'
$signedPayloadPath = Join-Path $root 'mr-x-mailbox-policy.payload.json'
$authorityPath = Join-Path $root 'authority.public.json'
$revocationsPath = Join-Path $root 'revocations.v1.json'
$manifestPath = Join-Path $generationRoot 'pair-manifest.v1.json'
if ((Get-Item -LiteralPath $publicKeyPath).Length -ne 32 -or
    (Get-FileHash -Algorithm SHA256 -LiteralPath $publicKeyPath).Hash.ToLowerInvariant() -cne
        $MrXPublicKeySha256 -or
    (Get-Item -LiteralPath $signaturePath).Length -ne 64) {
    throw 'Mailbox runtime Mr. X approval does not match the independent build pin.'
}
$verifierProject = Join-Path $PSScriptRoot `
    'Deep.AndroidLab.PolicyVerifier\Deep.AndroidLab.PolicyVerifier.csproj'
$previousPreference = $ErrorActionPreference
try {
    $ErrorActionPreference = 'Continue'
    & dotnet run --project $verifierProject -c Release --no-build --no-restore -- `
        verify $publicKeyPath $signaturePath $signedPayloadPath | Out-Null
    $verifierExitCode = $LASTEXITCODE
} finally {
    $ErrorActionPreference = $previousPreference
}
if ($verifierExitCode -ne 0) {
    throw 'Mailbox runtime Mr. X Ed25519 approval signature is invalid.'
}
$authority = Get-Content -Raw -LiteralPath $authorityPath | ConvertFrom-Json
$now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
if ([long]$authority.issuerValidFromUnixSeconds -gt $now -or
    [long]$authority.issuerValidUntilUnixSeconds -lt ($now + 1800) -or
    @($authority.selections).Count -ne 0 -or
    @($authority.epochs | Where-Object { @($_.replicas).Count -ne 0 }).Count -ne 0) {
    throw 'Mailbox authority must be live for 30 minutes and use the minimized client shape.'
}
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $authorityPath).Hash.ToLowerInvariant() -cne
        [string]$activation.authoritySha256 -or
    (Get-FileHash -Algorithm SHA256 -LiteralPath $revocationsPath).Hash.ToLowerInvariant() -cne
        [string]$activation.revocationSnapshotSha256 -or
    (Get-FileHash -Algorithm SHA256 -LiteralPath $manifestPath).Hash.ToLowerInvariant() -cne
        [string]$activation.pairManifestSha256) {
    throw 'Mailbox runtime independent activation hashes do not match staged bytes.'
}

$hostFiles = @(Get-ChildItem -LiteralPath $root -Recurse -File -Force |
    ForEach-Object { Get-RelativeChildPath -Root $root -Child $_.FullName } |
    Sort-Object)
$expectedFiles = @($relativeFiles | Sort-Object)
if (($hostFiles -join "`n") -cne ($expectedFiles -join "`n")) {
    throw 'Mailbox runtime host root contains missing or unexpected files.'
}
$hostDirectories = @(Get-ChildItem -LiteralPath $root -Recurse -Directory -Force |
    ForEach-Object { Get-RelativeChildPath -Root $root -Child $_.FullName } |
    Sort-Object)
$expectedDirectories = @(
    'pair',
    'pair/generations',
    "pair/generations/$generation") | Sort-Object
if (($hostDirectories -join "`n") -cne ($expectedDirectories -join "`n")) {
    throw 'Mailbox runtime host root contains missing or unexpected directories.'
}
$productionBefore = Get-PackagePathSnapshot 'network.xpoint.deep'
$appUid = (@(Invoke-AdbChecked @(
    '-s', $AndroidSerial, 'shell', 'run-as', $package, 'id', '-u')) -join '').Trim()
$appHome = (@(Invoke-AdbChecked @(
    '-s', $AndroidSerial, 'shell', 'run-as', $package, 'pwd')) -join '').Trim()
if ($appUid -notmatch '^\d+$' -or
    $appHome -notmatch '^/data/(user/0|data)/network\.xpoint\.deep\.e2e$') {
    throw 'The installed E2E package is not an exact debuggable run-as target.'
}
Invoke-AdbChecked @('-s', $AndroidSerial, 'shell', 'am', 'force-stop', $package) | Out-Null
$stage = 'files/.mailbox-runtime-v1.stage'
$published = $false
try {
    $createCommand = ConvertTo-RemoteShellArgument (
        "test ! -e files/mailbox-runtime-v1 && test ! -e $stage && " +
        "umask 077 && mkdir -p $stage/pair/generations/$generation")
    Invoke-AdbChecked @('-s', $AndroidSerial, 'shell', 'run-as', $package, 'sh', '-c',
        $createCommand) | Out-Null
    foreach ($relative in $relativeFiles) {
        $localPath = Join-Path $root $relative
        Copy-ToAppPrivate -LocalPath $localPath -RemotePath "$stage/$relative"
        $remoteHashLine = @(Invoke-AdbChecked @(
            '-s', $AndroidSerial, 'shell', 'run-as', $package,
            'sha256sum', "$stage/$relative")) -join ' '
        if ($remoteHashLine -cnotmatch '^(?<hash>[0-9a-f]{64})\s+') {
            throw "Staged mailbox runtime returned no canonical SHA-256: $relative"
        }
        $remoteHash = $Matches.hash
        $localHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $localPath).Hash.ToLowerInvariant()
        if ($remoteHash -cne $localHash) {
            $remoteLength = (@(Invoke-AdbChecked @(
                '-s', $AndroidSerial, 'shell', 'run-as', $package,
                'stat', '-c', '%s', "$stage/$relative")) -join '').Trim()
            $localLength = (Get-Item -LiteralPath $localPath).Length
            throw "Staged mailbox runtime hash mismatch: $relative " +
                "(localBytes=$localLength, remoteBytes=$remoteLength)"
        }
    }
    $remoteFiles = @(Invoke-AdbChecked @(
        '-s', $AndroidSerial, 'shell', 'run-as', $package, 'find', $stage,
        '-type', 'f')) | ForEach-Object {
            ([string]$_).Trim().Substring(($stage + '/').Length)
        } | Sort-Object
    if (($remoteFiles -join "`n") -cne ($expectedFiles -join "`n")) {
        throw 'App-private mailbox staging root contains missing or unexpected files.'
    }
    $publishCommand = ConvertTo-RemoteShellArgument (
        "find $stage -type d -exec chmod 700 {} +; " +
        "find $stage -type f -exec chmod 600 {} +; " +
        "test ! -e files/mailbox-runtime-v1 && mv $stage files/mailbox-runtime-v1")
    Invoke-AdbChecked @('-s', $AndroidSerial, 'shell', 'run-as', $package, 'sh', '-c',
        $publishCommand) | Out-Null
    $published = $true
} finally {
    if (-not $published) {
        & $adb -s $AndroidSerial shell run-as $package rm -rf $stage 2>$null | Out-Null
    }
}
$productionAfter = Get-PackagePathSnapshot 'network.xpoint.deep'
if ($productionAfter -cne $productionBefore) {
    throw 'The production package changed during mailbox runtime staging.'
}

[pscustomobject]@{
    serial = $AndroidSerial
    package = $package
    action = 'StageRuntime'
    generation = $generation
    stagedFiles = $relativeFiles.Count
} | Format-List
