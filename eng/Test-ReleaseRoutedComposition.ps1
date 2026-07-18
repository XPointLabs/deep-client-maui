[CmdletBinding()]
param(
    [ValidateSet('win-x64')]
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$ArtifactDirectory,
    [switch]$ContractOnlyVerifierFailure,
    [switch]$ContractOnlyFactoryFailure
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
$failure = [System.Collections.Generic.List[string]]::new()
$compiledDiStatus = 'not-run'
$factoryStatus = 'not-run'

function Write-GuardEvidence {
    [ordered]@{
        schema = 'deep.survival.release-routed-composition.v1'
        sourceCommitSha = $sourceCommitSha
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        status = $status
        assemblySha256 = $assemblySha256
        checks = @(
            [ordered]@{
                name = 'compiled-maui-program-routed-di'
                status = $compiledDiStatus
            },
            [ordered]@{
                name = 'release-production-factory-behavior'
                status = $factoryStatus
            }
        )
        failure = $failure -join '; '
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $resultPath -Encoding utf8
}

$contractModes = @($ContractOnlyVerifierFailure, $ContractOnlyFactoryFailure).Where({ $_ }).Count
if ($contractModes -gt 0) {
    if ([Environment]::GetEnvironmentVariable('DEEP_RELEASE_GUARD_CONTRACT_TEST') -cne '1' -or
        $contractModes -ne 1) {
        throw 'Partial-failure simulation is restricted to one explicit compiled contract test.'
    }
    if ($ContractOnlyVerifierFailure) {
        $compiledDiStatus = 'failed'
        $factoryStatus = 'passed'
        $failure.Add('simulated verifier failure')
    } else {
        $compiledDiStatus = 'passed'
        $factoryStatus = 'failed'
        $failure.Add('simulated factory failure')
    }
    Write-GuardEvidence
    exit 1
}

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
    $assemblyPath = $assemblies[0].FullName
    $assemblySha256 = (Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash.ToLowerInvariant()

    try {
        dotnet build `
            (Join-Path $repoRoot 'eng\Deep.ReleaseCompositionVerifier\Deep.ReleaseCompositionVerifier.csproj') `
            --configuration Release
        if ($LASTEXITCODE -ne 0) {
            throw 'Release DI verifier build failed.'
        }
        $verifier = Join-Path $repoRoot (
            'eng\Deep.ReleaseCompositionVerifier\bin\Release\net10.0\{0}\Deep.ReleaseCompositionVerifier.exe' -f
            $RuntimeIdentifier)
        & $verifier $assemblyPath
        if ($LASTEXITCODE -ne 0) {
            throw 'Compiled MauiProgram routed DI verification failed.'
        }
        $compiledDiStatus = 'passed'
    } catch {
        $compiledDiStatus = 'failed'
        $failure.Add($_.Exception.Message)
    }

    try {
        dotnet test `
            (Join-Path $repoRoot 'tests\Deep.Client.Maui.ViewModels.Tests\Deep.Client.Maui.ViewModels.Tests.csproj') `
            --configuration Release `
            --filter 'FullyQualifiedName~RoutedRuntimeConfigurationTests.ProductionFactory_' `
            --logger 'console;verbosity=minimal'
        if ($LASTEXITCODE -ne 0) {
            throw 'Release routed production factory behavior failed.'
        }
        $factoryStatus = 'passed'
    } catch {
        $factoryStatus = 'failed'
        $failure.Add($_.Exception.Message)
    }

    if ($compiledDiStatus -ceq 'passed' -and $factoryStatus -ceq 'passed') {
        $status = 'passed'
    }
} catch {
    $failure.Add($_.Exception.Message)
} finally {
    Write-GuardEvidence
}

if ($status -cne 'passed') {
    Write-Error ($failure -join '; ')
    exit 1
}
