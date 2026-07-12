param(
    [ValidateSet('amd64', 'arm64')]
    [string[]]$Architecture = @('amd64', 'arm64'),
    [string]$GoRoot = $env:GOROOT,
    [string]$GoPath = $env:GOPATH,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\src\Deep.Client.Maui\Platforms\Windows\Tools')
)

$ErrorActionPreference = 'Stop'
$xrayVersion = 'v1.260327.0'
$xrayCommit = 'd2758a023cd7f4174a5a5fa4ff66e487d4342ba0'
$xrayModuleSum = 'h1:g4TzxMwyPrxslZh6uD+FiG3lXKTrnNO+b4ky2OhogHE='
$xrayGoModSum = 'h1:OXMlhBloFry8mw0KwWLWLd3RQyXJzEYsCGlgsX36h60='
$expectedHashes = @{
    amd64 = '49FD9EB558FBC2FCE2D1DB7716E031F30C9BC889AA4A33EFA88F85265BDF1F30'
    arm64 = 'D12CB97F501E294D914C75D33EE80EE5A27D0CC835249228F5059A1E9D619811'
}

if ([string]::IsNullOrWhiteSpace($GoRoot) -or -not (Test-Path (Join-Path $GoRoot 'bin\go.exe'))) {
    throw 'Go 1.26.2 is required. Pass -GoRoot or set GOROOT.'
}
if ([string]::IsNullOrWhiteSpace($GoPath)) {
    $GoPath = Join-Path ([IO.Path]::GetTempPath()) 'xpoint-go'
}

$go = Join-Path $GoRoot 'bin\go.exe'
$version = & $go version
if ($version -notmatch 'go1\.26\.2') {
    throw "Expected Go 1.26.2, got: $version"
}

$destinationDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
$env:GOROOT = $GoRoot
$env:GOPATH = [IO.Path]::GetFullPath($GoPath)
$env:CGO_ENABLED = '0'

$moduleJson = & $go mod download -json "github.com/xtls/xray-core@$xrayVersion"
if ($LASTEXITCODE -ne 0) {
    throw "Could not download Xray core $xrayVersion."
}
$module = $moduleJson | ConvertFrom-Json
if ($module.Sum -ne $xrayModuleSum -or $module.GoModSum -ne $xrayGoModSum) {
    throw 'Downloaded Xray module failed checksum validation.'
}
if ($module.Origin.Hash -ne $xrayCommit) {
    throw "Xray tag resolved to unexpected commit $($module.Origin.Hash)."
}

foreach ($targetArchitecture in $Architecture) {
    $destination = Join-Path $destinationDirectory "xray-windows-$targetArchitecture.exe"
    $env:GOOS = 'windows'
    $env:GOARCH = $targetArchitecture
    Push-Location $module.Dir
    try {
        & $go build `
            -trimpath `
            -buildvcs=false `
            -ldflags '-s -w -buildid=' `
            -o $destination `
            ./main
        if ($LASTEXITCODE -ne 0) {
            throw "Xray build failed for windows/$targetArchitecture."
        }
    }
    finally {
        Pop-Location
    }

    $actualHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    if ($actualHash -ne $expectedHashes[$targetArchitecture]) {
        Remove-Item -LiteralPath $destination -Force
        throw "Xray build for windows/$targetArchitecture was not reproducible: $actualHash."
    }

    Write-Host "Built $destination ($actualHash)"
}
