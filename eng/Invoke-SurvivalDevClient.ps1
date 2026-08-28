[CmdletBinding()]
param(
    [ValidateSet('Android', 'Windows', 'All')]
    [string]$Target = 'Android',
    [string]$AndroidSerial,
    [string]$AdbPath = $env:DEEP_ADB_PATH,
    [string]$ApkSignerPath = $env:DEEP_APKSIGNER,
    [string]$JavaHome = $env:JAVA_HOME,
    [string]$RuntimeEnvironmentPath,
    [string]$MrXPublicKeySha256 = $env:DEEP_MR_X_PUBLIC_KEY_SHA256,
    [string]$PhysicalUatTrustFloorBundle = $env:DEEP_PHYSICAL_UAT_TRUST_FLOOR_BUNDLE,
    [string]$PhysicalUatPrivacyRoutesJson = $env:DEEP_PHYSICAL_UAT_PRIVACY_ROUTES_JSON,
    [string]$PhysicalUatPrivacyRoutesSignature = $env:DEEP_PHYSICAL_UAT_PRIVACY_ROUTES_SIGNATURE,
    [string]$PhysicalUatPrivacyRoutesPublicKey = $env:DEEP_PHYSICAL_UAT_PRIVACY_ROUTES_PUBLIC_KEY,
    [string]$PhysicalUatAndroidTransparencyManifest =
        $env:DEEP_PHYSICAL_UAT_ANDROID_TRANSPARENCY_MANIFEST,
    [string]$AndroidKeystore = $env:XPOINT_ANDROID_KEYSTORE,
    [string]$AndroidSigningPasswordFile = $env:XPOINT_ANDROID_SIGNING_PASSWORD_FILE,
    [string]$AndroidSigningKeyAlias = $(if ($env:XPOINT_ANDROID_KEY_ALIAS) {
        $env:XPOINT_ANDROID_KEY_ALIAS
    } else { 'xpoint-upload' }),
    [switch]$NoBuild,
    [switch]$NoInstall,
    [switch]$BuildOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'Read-ProductionTrustBundle.ps1')

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

function Resolve-CanonicalPhysicalUatFile {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Label
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "Physical UAT $Label path is required."
    }
    $candidate = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Physical UAT $Label must be an existing file."
    }
    for ($current = $candidate; -not [string]::IsNullOrWhiteSpace($current); $current = [IO.Path]::GetDirectoryName($current)) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Physical UAT $Label path must not traverse a reparse point."
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -ceq $current) { break }
    }
    return (Resolve-Path -LiteralPath $candidate -ErrorAction Stop).ProviderPath
}

if ([string]::IsNullOrWhiteSpace($RuntimeEnvironmentPath)) {
    $RuntimeEnvironmentPath = Join-Path $PSScriptRoot 'survival.dev.env'
}

$project = Join-Path $repoRoot 'src\Deep.Client.Maui\Deep.Client.Maui.csproj'
$runtimeEnvironment = Resolve-CanonicalRuntimeEnvironmentFile -Path $RuntimeEnvironmentPath
$physicalE2eProperty = '-p:DeepPhysicalE2E=true'
$runtimeEnvironmentProperty = "-p:DeepSurvivalRuntimeEnv=$runtimeEnvironment"
if ($MrXPublicKeySha256 -cnotmatch '^[0-9a-f]{64}$') {
    throw 'DEEP_MR_X_PUBLIC_KEY_SHA256 must be exactly 64 lowercase hexadecimal characters.'
}
$mrXTrustRootProperty = "-p:DeepMrXPublicKeySha256=$MrXPublicKeySha256"
$androidPackage = 'network.xpoint.deep.e2e'
$productionPackage = 'network.xpoint.deep'
$reversePorts = @(41545) + @(41801..41806) + @(41810..41823)
$physicalUatTrustFloorBundle = Resolve-CanonicalPhysicalUatFile `
    -Path $PhysicalUatTrustFloorBundle -Label 'trust-floor bundle'
$physicalUatPrivacyRoutesJson = Resolve-CanonicalPhysicalUatFile `
    -Path $PhysicalUatPrivacyRoutesJson -Label 'privacy-route JSON'
$physicalUatPrivacyRoutesSignature = Resolve-CanonicalPhysicalUatFile `
    -Path $PhysicalUatPrivacyRoutesSignature -Label 'privacy-route signature'
$physicalUatPrivacyRoutesPublicKey = Resolve-CanonicalPhysicalUatFile `
    -Path $PhysicalUatPrivacyRoutesPublicKey -Label 'privacy-route public key'
$requireAndroidUatIdentity = $Target -in @('Android', 'All')
$physicalUatTrust = Import-ProductionTrustBundle `
    -Path $physicalUatTrustFloorBundle `
    -RequireAndroid:$requireAndroidUatIdentity `
    -ExpectedAndroidApplicationId $androidPackage
if ([string]$physicalUatTrust.DeepProductionMrXPublicKeySha256 -cne $MrXPublicKeySha256) {
    throw 'Physical UAT trust floor differs from the build-pinned Mr. X key.'
}
$physicalUatBuildProperties = @(
    "-p:DeepPhysicalUatMrXPublicKeySha256=$($physicalUatTrust.DeepProductionMrXPublicKeySha256)",
    "-p:DeepPhysicalUatNetworkId=$($physicalUatTrust.DeepProductionNetworkId)",
    "-p:DeepPhysicalUatAuthorityGeneration=$($physicalUatTrust.DeepProductionAuthorityGeneration)",
    "-p:DeepPhysicalUatAuthorityHash=$($physicalUatTrust.DeepProductionAuthorityHash)",
    "-p:DeepPhysicalUatRevocationGeneration=$($physicalUatTrust.DeepProductionRevocationGeneration)",
    "-p:DeepPhysicalUatRevocationHeadHash=$($physicalUatTrust.DeepProductionRevocationHeadHash)",
    "-p:DeepPhysicalUatRevocationSnapshotHash=$($physicalUatTrust.DeepProductionRevocationSnapshotHash)",
    "-p:DeepPhysicalUatTopologyGeneration=$($physicalUatTrust.DeepProductionTopologyGeneration)",
    "-p:DeepPhysicalUatTopologyHash=$($physicalUatTrust.DeepProductionTopologyHash)",
    "-p:DeepPhysicalUatPrivacyRoutesJson=$physicalUatPrivacyRoutesJson",
    "-p:DeepPhysicalUatPrivacyRoutesSignature=$physicalUatPrivacyRoutesSignature",
    "-p:DeepPhysicalUatPrivacyRoutesPublicKey=$physicalUatPrivacyRoutesPublicKey"
)
if ($requireAndroidUatIdentity) {
    $physicalUatAndroidTransparencyManifest = Resolve-CanonicalPhysicalUatFile `
        -Path $PhysicalUatAndroidTransparencyManifest -Label 'Android ACT1 manifest'
    $actualManifestSha256 = (Get-FileHash `
        -LiteralPath $physicalUatAndroidTransparencyManifest -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualManifestSha256 -cne [string]$physicalUatTrust.DeepProductionAndroidBuildIdSha256) {
        throw 'Physical UAT ACT1 SHA-256 differs from the UAT trust-floor approval.'
    }
    $physicalUatBuildProperties += @(
        "-p:DeepPhysicalUatAndroidApplicationId=$androidPackage",
        "-p:DeepPhysicalUatAndroidVersionCode=$($physicalUatTrust.DeepProductionAndroidVersionCode)",
        "-p:DeepPhysicalUatAndroidSignerLineageSha256=$($physicalUatTrust.DeepProductionAndroidSignerLineageSha256)",
        "-p:DeepPhysicalUatAndroidCodeTransparencyManifest=$physicalUatAndroidTransparencyManifest"
    )
}
$androidSigningProperties = @()
$androidBuildStabilityProperties = @(
    '-m:1',
    '-p:BuildInParallel=false',
    '-p:UseSharedCompilation=false',
    '-p:Aapt2DaemonMaxInstanceCount=1',
    '-nodeReuse:false')
if ($requireAndroidUatIdentity -and
    (-not [string]::IsNullOrWhiteSpace($AndroidKeystore) -or
     -not [string]::IsNullOrWhiteSpace($AndroidSigningPasswordFile))) {
    $androidKeystore = Resolve-CanonicalPhysicalUatFile `
        -Path $AndroidKeystore -Label 'Android keystore'
    $androidSigningPasswordFile = Resolve-CanonicalPhysicalUatFile `
        -Path $AndroidSigningPasswordFile -Label 'Android signing password file'
    if ($AndroidSigningKeyAlias -cnotmatch '^[A-Za-z0-9_.-]{1,128}$') {
        throw 'Android signing key alias is non-canonical.'
    }
    $androidSigningProperties = @(
        '-p:AndroidKeyStore=true',
        "-p:AndroidSigningKeyStore=$androidKeystore",
        "-p:AndroidSigningKeyAlias=$AndroidSigningKeyAlias",
        "-p:AndroidSigningKeyPass=file:$androidSigningPasswordFile",
        "-p:AndroidSigningStorePass=file:$androidSigningPasswordFile")
}

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

    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $Adb @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($exitCode -ne 0) {
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

function Resolve-ApkSigner {
    if (-not [string]::IsNullOrWhiteSpace($ApkSignerPath) -and
        (Test-Path -LiteralPath $ApkSignerPath -PathType Leaf)) {
        return (Resolve-Path -LiteralPath $ApkSignerPath).Path
    }
    $command = Get-Command apksigner -ErrorAction SilentlyContinue
    if ($null -ne $command) { return $command.Source }
    $buildTools = @(
        "$env:LOCALAPPDATA\Android\Sdk\build-tools",
        "$env:ANDROID_HOME\build-tools",
        "$env:ANDROID_SDK_ROOT\build-tools",
        "${env:ProgramFiles(x86)}\Android\android-sdk\build-tools",
        "$env:ProgramFiles\Android\android-sdk\build-tools")
    foreach ($root in $buildTools) {
        if ([string]::IsNullOrWhiteSpace($root) -or
            -not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        $candidate = Get-ChildItem -LiteralPath $root -Directory |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName 'apksigner.bat' } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Select-Object -First 1
        if ($candidate) { return $candidate }
    }
    throw 'apksigner was not found. Set -ApkSignerPath or DEEP_APKSIGNER.'
}

function Get-ApkSignerSha256 {
    param(
        [Parameter(Mandatory)][string]$ApkSigner,
        [Parameter(Mandatory)][string]$Apk
    )
    $resolvedJavaHome = $JavaHome
    if ([string]::IsNullOrWhiteSpace($resolvedJavaHome)) {
        $resolvedJavaHome = @(
            'C:\Program Files\Android\openjdk\jdk-21.0.8',
            'C:\Program Files\Microsoft\jdk-21.0.8.9-hotspot') |
            Where-Object { Test-Path -LiteralPath (Join-Path $_ 'bin\java.exe') -PathType Leaf } |
            Select-Object -First 1
    }
    if ([string]::IsNullOrWhiteSpace($resolvedJavaHome) -or
        -not (Test-Path -LiteralPath (Join-Path $resolvedJavaHome 'bin\java.exe') -PathType Leaf)) {
        throw 'JAVA_HOME does not identify an Android build JDK.'
    }
    $previousJavaHome = $env:JAVA_HOME
    try {
        $env:JAVA_HOME = $resolvedJavaHome
        $output = @(& $ApkSigner verify --print-certs $Apk 2>&1)
        if ($LASTEXITCODE -ne 0) { throw "apksigner verify failed: $($output -join ' ')" }
    } finally {
        $env:JAVA_HOME = $previousJavaHome
    }
    $signers = @($output | ForEach-Object {
        if ([string]$_ -cmatch
            '^Signer #(?<number>[1-9][0-9]*) certificate SHA-256 digest: (?<digest>[0-9a-f]{64})$') {
            [pscustomobject]@{
                number = [int]$Matches.number
                digest = $Matches.digest
            }
        }
    })
    if ($signers.Count -ne 1 -or $signers[0].number -ne 1) {
        throw 'APK must have exactly one canonical signer and no rotation ambiguity.'
    }
    return $signers[0].digest
}

function Invoke-ExplicitApkSigning {
    param(
        [Parameter(Mandatory)][string]$ApkSigner,
        [Parameter(Mandatory)][string]$Apk,
        [Parameter(Mandatory)][string]$Keystore,
        [Parameter(Mandatory)][string]$PasswordFile,
        [Parameter(Mandatory)][string]$KeyAlias
    )
    $temporary = "$Apk.explicit-sign-$([Guid]::NewGuid().ToString('N')).apk"
    $resolvedJavaHome = $JavaHome
    if ([string]::IsNullOrWhiteSpace($resolvedJavaHome)) {
        $resolvedJavaHome = @('C:\Program Files\Android\openjdk\jdk-21.0.8',
            'C:\Program Files\Microsoft\jdk-21.0.8.9-hotspot') |
            Where-Object { Test-Path -LiteralPath (Join-Path $_ 'bin\java.exe') -PathType Leaf } |
            Select-Object -First 1
    }
    $previousJavaHome = $env:JAVA_HOME
    try {
        $env:JAVA_HOME = $resolvedJavaHome
        & $ApkSigner sign --out $temporary --ks $Keystore --ks-type PKCS12 `
            --ks-key-alias $KeyAlias --ks-pass "file:$PasswordFile" `
            --key-pass "file:$PasswordFile" $Apk
        if ($LASTEXITCODE -ne 0) { throw 'Explicit SDK APK signing failed.' }
        Move-Item -LiteralPath $temporary -Destination $Apk -Force
    } finally {
        Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
        $env:JAVA_HOME = $previousJavaHome
    }
}

if ($Target -in @('Windows', 'All')) {
    if (-not $NoBuild) {
        & dotnet build $project -f net10.0-windows10.0.19041.0 -c Debug $physicalE2eProperty $runtimeEnvironmentProperty $mrXTrustRootProperty @physicalUatBuildProperties
        if ($LASTEXITCODE -ne 0) {
            throw 'Survival Windows Debug build failed.'
        }
    }
}

if ($Target -in @('Android', 'All')) {
    if ($BuildOnly -and (-not $NoInstall -or $NoBuild -or $Target -ne 'Android')) {
        throw 'BuildOnly requires Target Android, NoInstall, and an enabled build.'
    }
    $adb = $null
    $wifiDevices = @()
    $productionBefore = ''
    if (-not $BuildOnly) {
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
    }

    if (-not $NoBuild) {
        & dotnet build $project -f net10.0-android -c Debug $physicalE2eProperty $runtimeEnvironmentProperty $mrXTrustRootProperty @physicalUatBuildProperties @androidSigningProperties @androidBuildStabilityProperties
        if ($LASTEXITCODE -ne 0) {
            throw 'Survival Android Debug build failed.'
        }
    }

    $apk = Join-Path $repoRoot "src\Deep.Client.Maui\bin\Debug\net10.0-android\$androidPackage-Signed.apk"
    if (-not (Test-Path -LiteralPath $apk -PathType Leaf)) {
        throw "Expected E2E APK was not produced: $apk"
    }

    $apkSigner = Resolve-ApkSigner
    if ($androidSigningProperties.Count -ne 0) {
        Invoke-ExplicitApkSigning -ApkSigner $apkSigner -Apk $apk `
            -Keystore $androidKeystore -PasswordFile $androidSigningPasswordFile `
            -KeyAlias $AndroidSigningKeyAlias
    }
    $candidateSigner = Get-ApkSignerSha256 -ApkSigner $apkSigner -Apk $apk
    $approvedCurrentSigner = ([string]$physicalUatTrust.DeepProductionAndroidSignerLineageSha256).Split('|')[-1]
    if ($candidateSigner -cne $approvedCurrentSigner) {
        throw 'Candidate APK signer differs from the UAT trust-floor lineage.'
    }

    if (-not $NoInstall) {
        $installedPath = Get-PackagePath -Adb $adb -Serial $AndroidSerial -Package $androidPackage
        if (-not [string]::IsNullOrWhiteSpace($installedPath)) {
            if ($installedPath.Contains("`n") -or
                -not $installedPath.StartsWith('package:/data/app/', [StringComparison]::Ordinal)) {
                throw 'Installed E2E package path is not one exact base APK.'
            }
            $temporaryInstalledApk = Join-Path ([IO.Path]::GetTempPath()) (
                'deep-installed-e2e-' + [Guid]::NewGuid().ToString('N') + '.apk')
            try {
                Invoke-AdbChecked -Adb $adb -Arguments @(
                    '-s', $AndroidSerial, 'pull',
                    $installedPath.Substring('package:'.Length),
                    $temporaryInstalledApk) | Out-Null
                $installedSigner = Get-ApkSignerSha256 `
                    -ApkSigner $apkSigner `
                    -Apk $temporaryInstalledApk
                if ($installedSigner -cne $candidateSigner) {
                    throw 'Candidate APK signer differs from the installed E2E package signer.'
                }
            } finally {
                Remove-Item -LiteralPath $temporaryInstalledApk -Force -ErrorAction SilentlyContinue
            }
        }
        Invoke-AdbChecked -Adb $adb -Arguments @('-s', $AndroidSerial, 'install', '-r', '-t', $apk) | Out-Host
        if ([string]::IsNullOrWhiteSpace((Get-PackagePath -Adb $adb -Serial $AndroidSerial -Package $androidPackage))) {
            throw 'The E2E package was not installed.'
        }
    }

    if (-not $BuildOnly) {
        $productionAfter = Get-PackagePath -Adb $adb -Serial $AndroidSerial -Package $productionPackage
        if ($productionAfter -cne $productionBefore) {
            throw 'The production package state changed during survival E2E setup.'
        }
    }

    [pscustomobject]@{
        serial = $AndroidSerial
        package = $androidPackage
        apk = $apk
        installed = -not $NoInstall
        reversedPorts = $(if ($BuildOnly) { @() } else { $reversePorts })
        productionPackageUntouched = $true
    } | Format-List | Out-Host
}
