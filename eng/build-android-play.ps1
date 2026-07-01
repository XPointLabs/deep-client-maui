param(
    [string]$Keystore = $env:XPOINT_ANDROID_KEYSTORE,
    [string]$PasswordFile = $env:XPOINT_ANDROID_SIGNING_PASSWORD_FILE,
    [string]$Alias = $(if ($env:XPOINT_ANDROID_KEY_ALIAS) { $env:XPOINT_ANDROID_KEY_ALIAS } else { "xpoint-upload" }),
    [string]$GoogleServicesJson = $env:XPOINT_GOOGLE_SERVICES_JSON,
    [string]$AndroidSdkRoot = $(if ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } elseif ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { "${env:ProgramFiles(x86)}\Android\android-sdk" }),
    [string]$JavaHome = $env:JAVA_HOME
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Keystore) -or -not (Test-Path -LiteralPath $Keystore -PathType Leaf)) {
    throw "Set XPOINT_ANDROID_KEYSTORE to the Play upload keystore path."
}

if ([string]::IsNullOrWhiteSpace($PasswordFile) -or -not (Test-Path -LiteralPath $PasswordFile -PathType Leaf)) {
    throw "Set XPOINT_ANDROID_SIGNING_PASSWORD_FILE to a password file outside Git."
}

$project = Join-Path $PSScriptRoot "..\src\Deep.Client.Maui\Deep.Client.Maui.csproj"
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
Copy-Item -LiteralPath $bundle.FullName -Destination $finalBundle -Force

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

& $apksigner.FullName verify --verbose $finalApk
if ($LASTEXITCODE -ne 0) {
    throw "APK signature verification failed with exit code $LASTEXITCODE."
}

Write-Output $finalBundle
Write-Output $finalApk
