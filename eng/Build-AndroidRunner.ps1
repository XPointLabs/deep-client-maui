[CmdletBinding()]
param(
    [ValidateSet('win-arm64', 'win-x64')]
    [string]$RuntimeIdentifier = 'win-arm64',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $PSScriptRoot 'Deep.AndroidRunner\Deep.AndroidRunner.csproj'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts\android-runner\$RuntimeIdentifier"
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
$requiredRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\android-runner'))
if (-not $output.StartsWith(
        $requiredRoot.TrimEnd('\') + '\',
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Android runner output must stay below artifacts/android-runner.'
}

dotnet publish $project `
    --configuration Release `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    --output $output `
    -p:PublishSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:EnableCompressionInSingleFile=true `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw 'Android runner single-file publish failed.'
}

$files = @(Get-ChildItem -LiteralPath $output -File)
if ($files.Count -ne 1 -or
    $files[0].Name -cne 'deep-android-runner.exe') {
    throw 'Android runner publish must contain exactly deep-android-runner.exe.'
}

$version = @(& $files[0].FullName --version 2>&1)
if ($LASTEXITCODE -ne 0 -or
    $version.Count -ne 1 -or
    [string]$version[0] -cne 'Deep Android Runner 3.0.0') {
    throw 'Published Android runner version probe failed.'
}

[pscustomobject]@{
    path = $files[0].FullName
    runtimeIdentifier = $RuntimeIdentifier
    version = [string]$version[0]
    sha256 = (Get-FileHash -LiteralPath $files[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    sizeBytes = $files[0].Length
} | ConvertTo-Json -Compress
