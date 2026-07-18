[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$ArtifactDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $ArtifactDirectory = Join-Path $repoRoot 'artifacts\release-gates'
}
$ArtifactDirectory = [System.IO.Path]::GetFullPath($ArtifactDirectory)
New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null
$resultPath = Join-Path $ArtifactDirectory 'release-routed-composition.json'
$sourceCommitSha = (& git -C $repoRoot rev-parse HEAD).Trim()
$status = 'failed'
$assemblySha256 = ''
$failure = ''

try {
    dotnet build `
        (Join-Path $repoRoot 'src\Deep.Client.Maui\Deep.Client.Maui.csproj') `
        --framework 'net10.0-windows10.0.19041.0' `
        --configuration Release `
        "-p:RuntimeIdentifierOverride=$RuntimeIdentifier"
    if ($LASTEXITCODE -ne 0) {
        throw 'Windows Release composition build failed.'
    }

    $assemblyRoot = Join-Path $repoRoot (
        'src\Deep.Client.Maui\bin\Release\net10.0-windows10.0.19041.0\{0}' -f
        $RuntimeIdentifier)
    $assemblies = @(Get-ChildItem -LiteralPath $assemblyRoot -Filter 'Deep.Client.Maui.dll' -File)
    if ($assemblies.Count -ne 1) {
        throw "Expected exactly one freshly built Release MAUI assembly; found $($assemblies.Count)."
    }
    $AssemblyPath = $assemblies[0].FullName
    $assemblySha256 = (Get-FileHash -LiteralPath $AssemblyPath -Algorithm SHA256).Hash.ToLowerInvariant()

    dotnet run `
        --project (Join-Path $repoRoot 'eng\Deep.ReleaseCompositionVerifier\Deep.ReleaseCompositionVerifier.csproj') `
        --configuration Release `
        -- $AssemblyPath
    if ($LASTEXITCODE -ne 0) {
        throw 'Compiled MauiProgram routed binding verification failed.'
    }

    dotnet test `
        (Join-Path $repoRoot 'tests\Deep.Client.Maui.ViewModels.Tests\Deep.Client.Maui.ViewModels.Tests.csproj') `
        --configuration Release `
        --filter 'FullyQualifiedName~RoutedRuntimeConfigurationTests.ProductionFactory_' `
        --logger 'console;verbosity=minimal'
    if ($LASTEXITCODE -ne 0) {
        throw 'Release routed production factory behavior failed.'
    }

    $status = 'passed'
} catch {
    $failure = $_.Exception.Message
} finally {
    [ordered]@{
        schema = 'deep.survival.release-routed-composition.v1'
        sourceCommitSha = $sourceCommitSha
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        status = $status
        assemblySha256 = $assemblySha256
        checks = @(
            [ordered]@{
                name = 'compiled-maui-program-routed-binding'
                status = $status
            },
            [ordered]@{
                name = 'release-production-factory-behavior'
                status = $status
            }
        )
        failure = $failure
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $resultPath -Encoding utf8
}

if ($status -cne 'passed') {
    Write-Error $failure
    exit 1
}
