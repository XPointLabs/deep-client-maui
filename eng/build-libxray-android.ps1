param(
    [string]$GoRoot = $env:GOROOT,
    [string]$AndroidSdkRoot = $env:ANDROID_HOME,
    [string]$JavaHome = $env:JAVA_HOME,
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\src\Deep.Client.Maui\Platforms\Android\Jars\libXray.aar')
)

$ErrorActionPreference = 'Stop'
$libXrayTag = 'v26.3.27'
$libXrayCommit = '38ae3cd8914d5bc2a7f81122fc6206efe3c07ad6'
$gomobileVersion = 'v0.0.0-20260611195102-4dd8f1dbf5d2'

if ([string]::IsNullOrWhiteSpace($GoRoot) -or -not (Test-Path (Join-Path $GoRoot 'bin\go.exe'))) {
    throw 'Go 1.26.2 is required. Pass -GoRoot or set GOROOT.'
}
if ([string]::IsNullOrWhiteSpace($AndroidSdkRoot) -or -not (Test-Path $AndroidSdkRoot)) {
    throw 'Android SDK with NDK 28.2.13676358 is required. Pass -AndroidSdkRoot or set ANDROID_HOME.'
}
if ([string]::IsNullOrWhiteSpace($JavaHome)) {
    $javaRoot = Get-ChildItem "${env:ProgramFiles}\Android\openjdk" -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Select-Object -First 1
    if ($null -ne $javaRoot) {
        $JavaHome = $javaRoot.FullName
    }
}
if ([string]::IsNullOrWhiteSpace($JavaHome) -or -not (Test-Path (Join-Path $JavaHome 'bin\javac.exe'))) {
    throw 'JDK with javac is required. Pass -JavaHome or set JAVA_HOME.'
}

$go = Join-Path $GoRoot 'bin\go.exe'
$version = & $go version
if ($version -notmatch 'go1\.26\.2') {
    throw "Expected Go 1.26.2, got: $version"
}

$work = Join-Path ([IO.Path]::GetTempPath()) "xpoint-libxray-$([Guid]::NewGuid().ToString('N'))"
try {
    New-Item -ItemType Directory -Path $work | Out-Null
    $env:GOROOT = $GoRoot
    $env:ANDROID_HOME = $AndroidSdkRoot
    $env:ANDROID_SDK_ROOT = $AndroidSdkRoot
    $env:JAVA_HOME = $JavaHome
    $env:GOBIN = Join-Path $work 'bin'
    $env:PATH = "$env:GOBIN;$(Join-Path $GoRoot 'bin');$(Join-Path $JavaHome 'bin');$env:PATH"

    & $go install "golang.org/x/mobile/cmd/gomobile@$gomobileVersion"
    git clone --depth 1 --branch $libXrayTag https://github.com/XTLS/libXray.git (Join-Path $work 'libxray')
    $actualCommit = git -C (Join-Path $work 'libxray') rev-parse HEAD
    if ($actualCommit -ne $libXrayCommit) {
        throw "libXray tag resolved to unexpected commit $actualCommit."
    }

    $destination = [IO.Path]::GetFullPath($OutputPath)
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($destination)) | Out-Null
    Push-Location (Join-Path $work 'libxray')
    try {
        & $go get -tool "golang.org/x/mobile/cmd/gobind@$gomobileVersion"
        & (Join-Path $env:GOBIN 'gomobile.exe') init
        & (Join-Path $env:GOBIN 'gomobile.exe') bind `
            '-target=android/arm64,android/amd64' `
            '-androidapi=26' `
            -o $destination `
            .
    }
    finally {
        Pop-Location
    }

    & (Join-Path $PSScriptRoot 'validate-libxray-aar.ps1') -AarPath $destination

    $actualSha256 = (Get-FileHash $destination -Algorithm SHA256).Hash
    Write-Host "Built $destination ($actualSha256)"
}
finally {
    if (Test-Path $work) {
        Remove-Item -LiteralPath $work -Recurse -Force
    }
}
