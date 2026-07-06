param(
    [string]$HostAddress = $(if ($env:DEEP_ANDROID_DEBUG_HOST) { $env:DEEP_ANDROID_DEBUG_HOST } else { "192.168.1.45" }),
    [int]$ConnectPort = $(if ($env:DEEP_ANDROID_DEBUG_PORT) { [int]$env:DEEP_ANDROID_DEBUG_PORT } else { 5555 }),
    [int]$FallbackPort = $(if ($env:DEEP_ANDROID_DEBUG_FALLBACK_PORT) { [int]$env:DEEP_ANDROID_DEBUG_FALLBACK_PORT } else { 38129 }),
    [int]$StablePort = $(if ($env:DEEP_ANDROID_DEBUG_STABLE_PORT) { [int]$env:DEEP_ANDROID_DEBUG_STABLE_PORT } else { 5555 }),
    [int]$PairPort = $(if ($env:DEEP_ANDROID_PAIR_PORT) { [int]$env:DEEP_ANDROID_PAIR_PORT } else { 0 }),
    [string]$PairCode = $env:DEEP_ANDROID_PAIR_CODE,
    [string]$AdbPath = $env:DEEP_ADB_PATH
)

$ErrorActionPreference = "Stop"

function Resolve-Adb {
    param([string]$PreferredPath)

    if (-not [string]::IsNullOrWhiteSpace($PreferredPath) -and (Test-Path -LiteralPath $PreferredPath -PathType Leaf)) {
        return (Resolve-Path -LiteralPath $PreferredPath).Path
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

    throw "adb was not found. Set DEEP_ADB_PATH or add Android platform-tools to PATH."
}

$adb = Resolve-Adb $AdbPath
& $adb start-server | Out-Host

if ($PairPort -gt 0 -and -not [string]::IsNullOrWhiteSpace($PairCode)) {
    & $adb pair "${HostAddress}:${PairPort}" $PairCode | Out-Host
}

function Connect-AndroidPort {
    param([int]$Port)

    $output = & $adb connect "${HostAddress}:${Port}" 2>&1
    $output | Out-Host
    return ($LASTEXITCODE -eq 0 -and (($output -join "`n") -match "connected|already connected"))
}

$connected = Connect-AndroidPort $ConnectPort
if (-not $connected -and $FallbackPort -gt 0 -and $FallbackPort -ne $ConnectPort) {
    $connected = Connect-AndroidPort $FallbackPort
    if ($connected -and $StablePort -gt 0 -and $StablePort -ne $FallbackPort) {
        & $adb -s "${HostAddress}:${FallbackPort}" tcpip $StablePort | Out-Host
        Start-Sleep -Seconds 3
        $connected = Connect-AndroidPort $StablePort
    }
}

& $adb devices -l | Out-Host
