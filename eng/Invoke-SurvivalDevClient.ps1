[CmdletBinding()]
param(
    [ValidateSet('Android', 'Windows', 'All')]
    [string]$Target = 'Android',
    [string]$AndroidSerial,
    [string]$AdbPath = $env:DEEP_ADB_PATH,
    [string]$RuntimeEnvironmentPath,
    [switch]$NoInstall
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Resolve-CanonicalRuntimeEnvironmentFile {
    param([Parameter(Mandatory)] [string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'Runtime environment path is required.'
    }

    $candidate = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw 'Runtime environment path must be an existing file.'
    }

    # Resolve every lexical ancestor before resolving the file itself. This keeps a
    # caller-supplied override supported while refusing junction/symlink traversal.
    for ($current = $candidate; -not [string]::IsNullOrWhiteSpace($current); $current = [IO.Path]::GetDirectoryName($current)) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Runtime environment path must not traverse a reparse point.'
        }

        $parent = [IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -ceq $current) {
            break
        }
    }

    $resolved = (Resolve-Path -LiteralPath $candidate -ErrorAction Stop).ProviderPath
    if (-not [IO.Path]::IsPathRooted($resolved) -or -not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw 'Runtime environment path did not resolve to a canonical file.'
    }

    return $resolved
}

if ([string]::IsNullOrWhiteSpace($RuntimeEnvironmentPath)) {
    $RuntimeEnvironmentPath = Join-Path $PSScriptRoot 'survival.dev.env'
}

$project = Join-Path $repoRoot 'src\Deep.Client.Maui\Deep.Client.Maui.csproj'
$runtimeEnvironment = Resolve-CanonicalRuntimeEnvironmentFile -Path $RuntimeEnvironmentPath
$physicalE2eProperty = '-p:DeepPhysicalE2E=true'
$runtimeEnvironmentProperty = "-p:DeepSurvivalRuntimeEnv=$runtimeEnvironment"
$androidPackage = 'network.xpoint.deep.e2e'
$productionPackage = 'network.xpoint.deep'
$reversePorts = @(41545) + @(41801..41806) + @(41810..41823)

function Resolve-Adb {
    if (-not [string]::IsNullOrWhiteSpace($AdbPath) -and (Test-Path -LiteralPath $AdbPath -PathType Leaf)) {
        return (Resolve-Path -LiteralPath $AdbPath).Path
    }

    $command = Get-Command adb -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $candidates = @(
        "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe",
        "$env:ANDROID_HOME\platform-tools\adb.exe",
        "$env:ANDROID_SDK_ROOT\platform-tools\adb.exe",
        "${env:ProgramFiles(x86)}\Android\android-sdk\platform-tools\adb.exe",
        "$env:ProgramFiles\Android\android-sdk\platform-tools\adb.exe"
    )
    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw 'adb was not found. Set -AdbPath or DEEP_ADB_PATH.'
}

function Invoke-AdbChecked {
    param(
        [Parameter(Mandatory)] [string]$Adb,
        [Parameter(Mandatory)] [string[]]$Arguments
    )

    $output = @(& $Adb @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "adb failed: $($output -join [Environment]::NewLine)"
    }
    return $output
}

function Get-PackagePath {
    param(
        [Parameter(Mandatory)] [string]$Adb,
        [Parameter(Mandatory)] [string]$Serial,
        [Parameter(Mandatory)] [string]$Package
    )

    return (@(Invoke-AdbChecked -Adb $Adb -Arguments @('-s', $Serial, 'shell', 'pm', 'path', $Package)) |
        ForEach-Object { [string]$_ } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Sort-Object) -join "`n"
}

if ($Target -in @('Windows', 'All')) {
    & dotnet build $project -f net10.0-windows10.0.19041.0 -c Debug $physicalE2eProperty $runtimeEnvironmentProperty
    if ($LASTEXITCODE -ne 0) {
        throw 'Survival Windows Debug build failed.'
    }
}

if ($Target -in @('Android', 'All')) {
    $adb = Resolve-Adb
    Invoke-AdbChecked -Adb $adb -Arguments @('start-server') | Out-Host

    $wifiDevices = @(& $adb devices | Select-Object -Skip 1 | ForEach-Object {
        if ($_ -match '^(?<serial>\S+:\d+)\s+device(?:\s|$)') { $Matches.serial }
    })
    if ([string]::IsNullOrWhiteSpace($AndroidSerial)) {
        if ($wifiDevices.Count -ne 1) {
            throw "Expected exactly one connected Wi-Fi ADB device; found $($wifiDevices.Count). Pass -AndroidSerial explicitly."
        }
        $AndroidSerial = $wifiDevices[0]
    } elseif ($AndroidSerial -notin $wifiDevices) {
        throw 'The selected Android serial is not a connected Wi-Fi ADB device.'
    }

    $productionBefore = Get-PackagePath -Adb $adb -Serial $AndroidSerial -Package $productionPackage
    foreach ($port in $reversePorts) {
        Invoke-AdbChecked -Adb $adb -Arguments @('-s', $AndroidSerial, 'reverse', "tcp:$port", "tcp:$port") | Out-Null
    }

    & dotnet build $project -f net10.0-android -c Debug $physicalE2eProperty $runtimeEnvironmentProperty
    if ($LASTEXITCODE -ne 0) {
        throw 'Survival Android Debug build failed.'
    }

    $apk = Join-Path $repoRoot "src\Deep.Client.Maui\bin\Debug\net10.0-android\$androidPackage-Signed.apk"
    if (-not (Test-Path -LiteralPath $apk -PathType Leaf)) {
        throw "Expected E2E APK was not produced: $apk"
    }

    if (-not $NoInstall) {
        Invoke-AdbChecked -Adb $adb -Arguments @('-s', $AndroidSerial, 'install', '-r', '-t', $apk) | Out-Host
        if ([string]::IsNullOrWhiteSpace((Get-PackagePath -Adb $adb -Serial $AndroidSerial -Package $androidPackage))) {
            throw 'The E2E package was not installed.'
        }
    }

    $productionAfter = Get-PackagePath -Adb $adb -Serial $AndroidSerial -Package $productionPackage
    if ($productionAfter -cne $productionBefore) {
        throw 'The production package state changed during survival E2E setup.'
    }

    [pscustomobject]@{
        serial = $AndroidSerial
        package = $androidPackage
        apk = $apk
        installed = -not $NoInstall
        reversedPorts = $reversePorts
        productionPackageUntouched = $true
    } | Format-List | Out-Host
}
