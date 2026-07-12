param(
    [string]$Keystore = $env:XPOINT_ANDROID_KEYSTORE,
    [string]$PasswordFile = $env:XPOINT_ANDROID_SIGNING_PASSWORD_FILE,
    [string]$Alias = $(if ($env:XPOINT_ANDROID_KEY_ALIAS) { $env:XPOINT_ANDROID_KEY_ALIAS } else { "xpoint-upload" }),
    [string]$GoogleServicesJson = $env:XPOINT_GOOGLE_SERVICES_JSON,
    [string]$AndroidSdkRoot = $(if ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } elseif ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { "${env:ProgramFiles(x86)}\Android\android-sdk" }),
    [string]$JavaHome = $env:JAVA_HOME
)

$ErrorActionPreference = "Stop"
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

$firebaseTarget = Join-Path $PSScriptRoot "..\src\Deep.Client.Maui\Platforms\Android\google-services.json"
if ([string]::IsNullOrWhiteSpace($GoogleServicesJson)) {
    $GoogleServicesJson = $firebaseTarget
}

if (-not (Test-Path -LiteralPath $GoogleServicesJson -PathType Leaf)) {
    throw "Set XPOINT_GOOGLE_SERVICES_JSON to the production Firebase google-services.json path."
}

$firebase = Get-Content -LiteralPath $GoogleServicesJson -Raw | ConvertFrom-Json
$packages = @($firebase.client | ForEach-Object { $_.client_info.android_client_info.package_name })
if ($packages -notcontains "network.xpoint.deep") {
    throw "Firebase config must contain the Android package network.xpoint.deep."
}

$libXrayAar = Join-Path $PSScriptRoot "..\src\Deep.Client.Maui\Platforms\Android\Jars\libXray.aar"
& (Join-Path $PSScriptRoot "validate-libxray-aar.ps1") -AarPath $libXrayAar

if ((Resolve-Path -LiteralPath $GoogleServicesJson).Path -ne (Resolve-Path -LiteralPath (Split-Path $firebaseTarget -Parent)).Path + "\google-services.json") {
    Copy-Item -LiteralPath $GoogleServicesJson -Destination $firebaseTarget -Force
}

dotnet publish $project `
    -f net10.0-android `
    -c Release `
    -p:AndroidPackageFormats=aab `
    -p:AndroidKeyStore=true `
    -p:AndroidSigningKeyStore="$Keystore" `
    -p:AndroidSigningKeyAlias="$Alias" `
    -p:AndroidSigningKeyPass="file:$PasswordFile" `
    -p:AndroidSigningStorePass="file:$PasswordFile" `
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
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
$finalBundle = Join-Path $releaseDirectory "network.xpoint.deep.aab"
$versionedBundle = Join-Path $releaseDirectory "network.xpoint.deep-$displayVersion-v$versionCode.aab"
Copy-Item -LiteralPath $bundle.FullName -Destination $finalBundle -Force
Copy-Item -LiteralPath $bundle.FullName -Destination $versionedBundle -Force

dotnet publish $project `
    -f net10.0-android `
    -c Release `
    -p:AndroidPackageFormats=apk `
    -p:AndroidKeyStore=false `
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
    --out $finalApk `
    $apk.FullName
if ($LASTEXITCODE -ne 0) {
    throw "APK signing failed with exit code $LASTEXITCODE."
}

$apkVerificationOutput = & $apksigner.FullName verify --verbose --print-certs $finalApk 2>&1
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

$badgingOutput = & $aapt.FullName dump badging $finalApk
$aaptExitCode = $LASTEXITCODE
$badging = $badgingOutput | Select-Object -First 1
if ($aaptExitCode -ne 0 -or
    $badging -notmatch "name='network\.xpoint\.deep'" -or
    $badging -notmatch "versionCode='$([regex]::Escape($versionCode))'" -or
    $badging -notmatch "versionName='$([regex]::Escape($displayVersion))'") {
    throw "APK package/version verification failed: $badging"
}

$jarsigner = Join-Path $JavaHome "bin\jarsigner.exe"
& $jarsigner -verify $finalBundle | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "AAB signature verification failed with exit code $LASTEXITCODE."
}

$keytool = Join-Path $JavaHome "bin\keytool.exe"
if (-not (Test-Path -LiteralPath $keytool -PathType Leaf)) {
    throw "keytool.exe was not found under JAVA_HOME."
}
$aabCertificateOutput = & $keytool -printcert -jarfile $finalBundle -rfc 2>&1
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

Copy-Item -LiteralPath $finalApk -Destination $versionedApk -Force

Write-Output $versionedBundle
Write-Output $versionedApk
