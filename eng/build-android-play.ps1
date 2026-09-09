param(
    [Parameter(Mandatory = $true)]
    [string]$TrustFloorBundle,
    [Parameter(Mandatory = $true)]
    [string]$AndroidCodeTransparencyManifest,
    [Parameter(Mandatory = $true)]
    [string]$BundletoolJar,
    [string]$Keystore = $env:XPOINT_ANDROID_KEYSTORE,
    [string]$PasswordFile = $env:XPOINT_ANDROID_SIGNING_PASSWORD_FILE,
    [string]$Alias = $(if ($env:XPOINT_ANDROID_KEY_ALIAS) { $env:XPOINT_ANDROID_KEY_ALIAS } else { "xpoint-upload" }),
    [string]$GoogleServicesJson = $env:XPOINT_GOOGLE_SERVICES_JSON,
    [string]$AndroidSdkRoot = $(if ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } elseif ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { "${env:ProgramFiles(x86)}\Android\android-sdk" }),
    [string]$JavaHome = $env:JAVA_HOME
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'Read-ProductionTrustBundle.ps1')
. (Join-Path $PSScriptRoot 'Assert-AndroidBundletool.ps1')
$trustProperties = Import-ProductionTrustBundle `
    -Path $TrustFloorBundle -RequireAndroid
$bundletoolLease = Open-PinnedAndroidBundletool -Path $BundletoolJar
$BundletoolJar = $bundletoolLease.Path
try {
$trustMsBuildArguments = @($trustProperties.GetEnumerator() | ForEach-Object {
    "-p:$($_.Key)=$($_.Value)"
})
$expectedUploadCertificateSha256 = "BF8ABED56E852D0902796F9E0131789F188688784A07AD516760F127684204C2"

function Assert-UploadCertificateSha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ArtifactName,
        [Parameter(Mandatory = $true)]
        [string]$ActualSha256
    )

    $normalizedSha256 = $ActualSha256.Replace(":", "").Trim().ToUpperInvariant()
    if ($normalizedSha256 -ne $expectedUploadCertificateSha256) {
        throw "$ArtifactName upload certificate SHA-256 mismatch. Expected $expectedUploadCertificateSha256, got $normalizedSha256."
    }
}

function Get-ExclusiveBoundedSha256 {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][int]$MaximumBytes
    )
    $resolved = [IO.Path]::GetFullPath($Path)
    $cursor = Get-Item -LiteralPath $resolved -Force
    while ($null -ne $cursor) {
        if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Android transparency input path contains a reparse point."
        }
        $cursor = if ($cursor -is [IO.FileInfo]) { $cursor.Directory } else { $cursor.Parent }
    }
    $stream = [IO.File]::Open($resolved, [IO.FileMode]::Open,
        [IO.FileAccess]::Read, [IO.FileShare]::None)
    try {
        if ($stream.Length -le 0 -or $stream.Length -gt $MaximumBytes) {
            throw "Android transparency input is empty or oversized."
        }
        $sha256 = [Security.Cryptography.SHA256]::Create()
        try {
            $result = [BitConverter]::ToString($sha256.ComputeHash($stream)).Replace("-", "").ToLowerInvariant()
            $cursor = Get-Item -LiteralPath $resolved -Force
            while ($null -ne $cursor) {
                if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Android transparency input path changed to a reparse point."
                }
                $cursor = if ($cursor -is [IO.FileInfo]) { $cursor.Directory } else { $cursor.Parent }
            }
            return $result
        }
        finally { $sha256.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Read-ExclusiveBoundedUtf8 {
    param([Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][int]$MaximumBytes)
    $resolved = [IO.Path]::GetFullPath($Path)
    $cursor = Get-Item -LiteralPath $resolved -Force
    while ($null -ne $cursor) {
        if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'External Android build input path contains a reparse point.'
        }
        $cursor = if ($cursor -is [IO.FileInfo]) { $cursor.Directory } else { $cursor.Parent }
    }
    $stream = [IO.File]::Open($resolved, [IO.FileMode]::Open,
        [IO.FileAccess]::Read, [IO.FileShare]::None)
    $bytes = $null
    try {
        if ($stream.Length -le 0 -or $stream.Length -gt $MaximumBytes) {
            throw 'External Android build input is empty or oversized.'
        }
        $bytes = [byte[]]::new([int]$stream.Length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            if ($read -le 0) { throw 'External Android build input ended unexpectedly.' }
            $offset += $read
        }
        $cursor = Get-Item -LiteralPath $resolved -Force
        while ($null -ne $cursor) {
            if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'External Android build input path changed to a reparse point.'
            }
            $cursor = if ($cursor -is [IO.FileInfo]) { $cursor.Directory } else { $cursor.Parent }
        }
        return [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
    }
    finally {
        if ($null -ne $bytes) { [Array]::Clear($bytes, 0, $bytes.Length) }
        $stream.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $AndroidCodeTransparencyManifest -PathType Leaf)) {
    throw "Mr. X-signed Android code-transparency manifest was not found."
}
$AndroidCodeTransparencyManifest = [IO.Path]::GetFullPath($AndroidCodeTransparencyManifest)
$manifestSha256 = Get-ExclusiveBoundedSha256 `
    -Path $AndroidCodeTransparencyManifest -MaximumBytes (512 * 1024)
if ($manifestSha256 -cne [string]$trustProperties.DeepProductionAndroidBuildIdSha256) {
    throw "Android code-transparency manifest SHA-256 does not match the approved buildIdSha256."
}
$trustMsBuildArguments +=
    "-p:DeepProductionAndroidCodeTransparencyManifest=$AndroidCodeTransparencyManifest"

if ([string]::IsNullOrWhiteSpace($Keystore) -or -not (Test-Path -LiteralPath $Keystore -PathType Leaf)) {
    throw "Set XPOINT_ANDROID_KEYSTORE to the Play upload keystore path."
}

if ([string]::IsNullOrWhiteSpace($PasswordFile) -or -not (Test-Path -LiteralPath $PasswordFile -PathType Leaf)) {
    throw "Set XPOINT_ANDROID_SIGNING_PASSWORD_FILE to a password file outside Git."
}

$project = Join-Path $PSScriptRoot "..\src\Deep.Client.Maui\Deep.Client.Maui.csproj"
$projectXml = [xml](Get-Content -LiteralPath $project -Raw)
$applicationIdNode = $projectXml.SelectSingleNode("/Project/PropertyGroup/ApplicationId")
$displayVersionNode = $projectXml.SelectSingleNode("/Project/PropertyGroup/ApplicationDisplayVersion")
$versionCodeNode = $projectXml.SelectSingleNode("/Project/PropertyGroup/ApplicationVersion")
if ($null -eq $applicationIdNode -or $null -eq $displayVersionNode -or $null -eq $versionCodeNode) {
    throw "Android release version metadata is missing from the project."
}
$applicationId = $applicationIdNode.InnerText.Trim()
$displayVersion = $displayVersionNode.InnerText.Trim()
$versionCode = $versionCodeNode.InnerText.Trim()
$parsedVersionCode = 0
if ($applicationId -ne "network.xpoint.deep") {
    throw "Android release project must use package network.xpoint.deep."
}
if ([string]::IsNullOrWhiteSpace($displayVersion) -or -not [int]::TryParse($versionCode, [ref]$parsedVersionCode) -or $parsedVersionCode -le 0) {
    throw "Android release version metadata is invalid."
}
if ([string]$trustProperties.DeepProductionAndroidVersionCode -cne $versionCode) {
    throw "Trust-floor Android versionCode does not match project versionCode $versionCode."
}

if ([string]::IsNullOrWhiteSpace($GoogleServicesJson) -or
    -not (Test-Path -LiteralPath $GoogleServicesJson -PathType Leaf)) {
    throw "Set XPOINT_GOOGLE_SERVICES_JSON to the production Firebase google-services.json path."
}
$GoogleServicesJson = [IO.Path]::GetFullPath($GoogleServicesJson)
$firebase = Read-ExclusiveBoundedUtf8 -Path $GoogleServicesJson -MaximumBytes (1024 * 1024) |
    ConvertFrom-Json
$packages = @($firebase.client | ForEach-Object { $_.client_info.android_client_info.package_name })
if ($packages -notcontains "network.xpoint.deep") {
    throw "Firebase config must contain the Android package network.xpoint.deep."
}

$libXrayAar = Join-Path $PSScriptRoot "..\src\Deep.Client.Maui\Platforms\Android\Jars\libXray.aar"
& (Join-Path $PSScriptRoot "validate-libxray-aar.ps1") -AarPath $libXrayAar

$trustMsBuildArguments += "-p:DeepGoogleServicesJson=$GoogleServicesJson"

dotnet publish $project `
    -f net10.0-android `
    -c Release `
    -p:AndroidPackageFormats=aab `
    -p:AndroidKeyStore=true `
    -p:AndroidSigningKeyStore="$Keystore" `
    -p:AndroidSigningKeyAlias="$Alias" `
    -p:AndroidSigningStorePass="file:$PasswordFile" `
    @trustMsBuildArguments `
    -nodeReuse:false

if ($LASTEXITCODE -ne 0) {
    throw "Android App Bundle build failed with exit code $LASTEXITCODE."
}

$androidOutput = Join-Path $PSScriptRoot "..\src\Deep.Client.Maui\bin\Release\net10.0-android"
$bundle = Get-ChildItem $androidOutput -Recurse -Filter "network.xpoint.deep-Signed.aab" |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

if ($null -eq $bundle) {
    throw "Build succeeded but no Android App Bundle was produced."
}

$releaseDirectory = Join-Path $PSScriptRoot "..\artifacts\android-release"
$finalBundle = Join-Path $releaseDirectory "network.xpoint.deep.aab"
$versionedBundle = Join-Path $releaseDirectory "network.xpoint.deep-$displayVersion-v$versionCode.aab"

dotnet publish $project `
    -f net10.0-android `
    -c Release `
    -p:AndroidPackageFormats=apk `
    -p:AndroidKeyStore=false `
    @trustMsBuildArguments `
    -nodeReuse:false `
    --no-restore

if ($LASTEXITCODE -ne 0) {
    throw "Android APK build failed with exit code $LASTEXITCODE."
}

$apk = Get-ChildItem $androidOutput -Recurse -Filter "network.xpoint.deep.apk" |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($null -eq $apk) {
    throw "Build succeeded but no Android APK was produced."
}

$candidateDirectory = Join-Path ([IO.Path]::GetTempPath()) ("deep-android-release-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $candidateDirectory | Out-Null
try {
$candidateApk = Join-Path $candidateDirectory "network.xpoint.deep.apk"
$finalApk = Join-Path $releaseDirectory "network.xpoint.deep.apk"
$versionedApk = Join-Path $releaseDirectory "network.xpoint.deep-$displayVersion-v$versionCode.apk"
$apksigner = Get-ChildItem (Join-Path $AndroidSdkRoot "build-tools") -Recurse -Filter "apksigner.bat" |
    Sort-Object FullName -Descending | Select-Object -First 1
if ($null -eq $apksigner) {
    throw "Android SDK apksigner.bat was not found under $AndroidSdkRoot."
}

if ([string]::IsNullOrWhiteSpace($JavaHome)) {
    $javaRoot = Get-ChildItem "$env:ProgramFiles\Android\openjdk" -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Select-Object -First 1
    if ($null -ne $javaRoot) {
        $JavaHome = $javaRoot.FullName
    }
}

if ([string]::IsNullOrWhiteSpace($JavaHome) -or -not (Test-Path (Join-Path $JavaHome "bin\java.exe"))) {
    throw "Set JAVA_HOME to the JDK used by the Android workload."
}

$env:JAVA_HOME = $JavaHome
& $apksigner.FullName sign `
    --ks $Keystore `
    --ks-key-alias $Alias `
    --ks-pass "file:$PasswordFile" `
    --out $candidateApk `
    $apk.FullName
if ($LASTEXITCODE -ne 0) {
    throw "APK signing failed with exit code $LASTEXITCODE."
}

$apkVerificationOutput = & $apksigner.FullName verify --verbose --print-certs $candidateApk 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "APK signature verification failed with exit code $LASTEXITCODE."
}
$apkCertificateMatch = [regex]::Match(
    ($apkVerificationOutput -join "`n"),
    "(?im)^Signer #1 certificate SHA-256 digest:\s*([0-9a-f:]{64,95})\s*$")
if (-not $apkCertificateMatch.Success) {
    throw "APK signer certificate SHA-256 was not reported by apksigner."
}
Assert-UploadCertificateSha256 -ArtifactName "APK" -ActualSha256 $apkCertificateMatch.Groups[1].Value

$aapt = Get-ChildItem (Join-Path $AndroidSdkRoot "build-tools") -Recurse -Filter "aapt.exe" |
    Sort-Object FullName -Descending | Select-Object -First 1
if ($null -eq $aapt) {
    throw "Android SDK aapt.exe was not found under $AndroidSdkRoot."
}

$badgingOutput = & $aapt.FullName dump badging $candidateApk
$aaptExitCode = $LASTEXITCODE
$badging = $badgingOutput | Select-Object -First 1
if ($aaptExitCode -ne 0 -or
    $badging -notmatch "name='network\.xpoint\.deep'" -or
    $badging -notmatch "versionCode='$([regex]::Escape($versionCode))'" -or
    $badging -notmatch "versionName='$([regex]::Escape($displayVersion))'") {
    throw "APK package/version verification failed: $badging"
}

$jarsigner = Join-Path $JavaHome "bin\jarsigner.exe"
& $jarsigner -verify $bundle.FullName | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "AAB signature verification failed with exit code $LASTEXITCODE."
}

$keytool = Join-Path $JavaHome "bin\keytool.exe"
if (-not (Test-Path -LiteralPath $keytool -PathType Leaf)) {
    throw "keytool.exe was not found under JAVA_HOME."
}
$aabCertificateOutput = & $keytool -printcert -jarfile $bundle.FullName -rfc 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "AAB signer certificate extraction failed with exit code $LASTEXITCODE."
}
$aabCertificateMatch = [regex]::Match(
    ($aabCertificateOutput -join "`n"),
    "(?s)-----BEGIN CERTIFICATE-----\s*(.*?)\s*-----END CERTIFICATE-----")
if (-not $aabCertificateMatch.Success) {
    throw "AAB signer certificate was not reported by keytool."
}
$aabCertificateBytes = [Convert]::FromBase64String(
    [regex]::Replace($aabCertificateMatch.Groups[1].Value, "\s", ""))
$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $aabCertificateSha256 = [BitConverter]::ToString(
        $sha256.ComputeHash($aabCertificateBytes)).Replace("-", "")
}
finally {
    $sha256.Dispose()
}
Assert-UploadCertificateSha256 -ArtifactName "AAB" -ActualSha256 $aabCertificateSha256

$generatedApks = Join-Path $candidateDirectory "network.xpoint.deep.apks"
& (Join-Path $JavaHome "bin\java.exe") -jar $BundletoolJar build-apks `
    "--bundle=$($bundle.FullName)" `
    "--output=$generatedApks" `
    --mode=default `
    "--ks=$Keystore" `
    "--ks-key-alias=$Alias" `
    "--ks-pass=file:$PasswordFile" `
    --overwrite
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $generatedApks -PathType Leaf)) {
    throw "bundletool failed to generate the complete default APK set."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($generatedApks)
$inventory = [Collections.Generic.Dictionary[string,string]]::new(
    [StringComparer]::Ordinal)
try {
    if ($archive.Entries.Count -gt 16384) {
        throw "Generated APKS archive has too many entries."
    }
    $apkEntries = @($archive.Entries | Where-Object {
        -not [string]::IsNullOrEmpty($_.Name) -and $_.Name.EndsWith('.apk', [StringComparison]::Ordinal)
    })
    if ($apkEntries.Count -lt 1 -or $apkEntries.Count -gt 4096) {
        throw "Generated APKS artifact count is outside strict bounds."
    }
    $entryNumber = 0
    foreach ($entry in $apkEntries) {
        if ($entry.FullName.Contains('\') -or $entry.FullName.StartsWith('/') -or
            @($entry.FullName.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0 -or
            $entry.Length -le 0 -or $entry.Length -gt (512MB) -or
            ($entry.Length -gt (4MB) -and
                ($entry.CompressedLength -le 0 -or ($entry.Length / $entry.CompressedLength) -gt 1000))) {
            throw "Generated APKS contains an unsafe APK entry."
        }
        $entryNumber++
        $extracted = Join-Path $candidateDirectory ("split-{0:D4}.apk" -f $entryNumber)
        $source = $entry.Open()
        $target = [IO.File]::Open($extracted, [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $buffer = [byte[]]::new(131072)
            [long]$written = 0
            while (($read = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $written += $read
                if ($written -gt $entry.Length) { throw "Generated APK expanded past its declaration." }
                $target.Write($buffer, 0, $read)
            }
            if ($written -ne $entry.Length) { throw "Generated APK is truncated." }
        }
        finally { $source.Dispose(); $target.Dispose() }

        $splitBadgingOutput = & $aapt.FullName dump badging $extracted
        $splitAaptExitCode = $LASTEXITCODE
        $splitBadging = $splitBadgingOutput | Select-Object -First 1
        if ($splitAaptExitCode -ne 0 -or
            $splitBadging -notmatch "name='$([regex]::Escape($applicationId))'" -or
            $splitBadging -notmatch "versionCode='$([regex]::Escape($versionCode))'") {
            throw "Generated APK package/version verification failed: $splitBadging"
        }
        $splitMatch = [regex]::Match($splitBadging, "(?:^|\s)split='([^']+)'(?:\s|$)")
        $identity = if ($splitMatch.Success) { $splitMatch.Groups[1].Value } else { 'base' }
        if ($identity -notmatch '^[A-Za-z0-9_.-]{1,256}$' -or
            -not $inventory.TryAdd($identity, $extracted)) {
            throw "Generated APKS contains an invalid or duplicate split identity '$identity'."
        }
    }
}
finally { $archive.Dispose() }

$inventoryPath = Join-Path $candidateDirectory 'android-transparency-inventory.tsv'
$inventoryText = (($inventory.GetEnumerator() | Sort-Object Key | ForEach-Object {
    "$($_.Key)`t$($_.Value)"
}) -join "`n") + "`n"
[IO.File]::WriteAllText($inventoryPath, $inventoryText, [Text.UTF8Encoding]::new($false, $true))
$transparencyTool = Join-Path $PSScriptRoot 'tools\Deep.AndroidTransparency.Tool\Deep.AndroidTransparency.Tool.csproj'
& dotnet run --project $transparencyTool -c Release -- `
    verify `
    --manifest $AndroidCodeTransparencyManifest `
    --expected-manifest-sha256 ([string]$trustProperties.DeepProductionAndroidBuildIdSha256) `
    --expected-mrx-sha256 ([string]$trustProperties.DeepProductionMrXPublicKeySha256) `
    --application-id $applicationId `
    --version-code $versionCode `
    --signer-lineage ([string]$trustProperties.DeepProductionAndroidSignerLineageSha256) `
    --inventory $inventoryPath
if ($LASTEXITCODE -ne 0) {
    throw "Generated default APK set does not match the Mr. X-signed code-transparency manifest."
}

New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
Copy-Item -LiteralPath $bundle.FullName -Destination $finalBundle -Force
Copy-Item -LiteralPath $bundle.FullName -Destination $versionedBundle -Force
Copy-Item -LiteralPath $candidateApk -Destination $finalApk -Force
Copy-Item -LiteralPath $candidateApk -Destination $versionedApk -Force

Write-Output $versionedBundle
Write-Output $versionedApk
}
finally {
    if (Test-Path -LiteralPath $candidateDirectory) {
        Remove-Item -LiteralPath $candidateDirectory -Recurse -Force
    }
}
}
finally { Close-PinnedAndroidBundletool -Lease $bundletoolLease }
