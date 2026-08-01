$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$runner = Join-Path $repoRoot 'eng\Invoke-PhysicalMau2CrossPlatform.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    $runner, [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) {
    throw 'Physical MAU2 runner did not parse.'
}
foreach ($name in @(
    'Set-ProtectedRunItem',
    'Assert-NonReparseDirectory',
    'Assert-ExactProtectedRunDirectory',
    'Initialize-ProtectedRunsRoot')) {
    $definition = $ast.Find({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -ceq $name
    }, $true)
    if ($null -eq $definition) {
        throw "Runner helper '$name' is absent."
    }
    Invoke-Expression $definition.Extent.Text
}

$sandbox = Join-Path ([IO.Path]::GetTempPath()) "deep-mau2-runs-$([Guid]::NewGuid().ToString('N'))"
$target = Join-Path $sandbox 'outside'
$junction = Join-Path $sandbox 'e2e-runs'
try {
    [IO.Directory]::CreateDirectory($target) | Out-Null
    New-Item -ItemType Junction -Path $junction -Target $target | Out-Null
    $rejected = $false
    try {
        Initialize-ProtectedRunsRoot $junction
    } catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw 'A planted e2e-runs junction was accepted.'
    }
    if (@(Get-ChildItem -Force -LiteralPath $target).Count -ne 0) {
        throw 'The runner mutated a junction target before rejecting it.'
    }
    [IO.Directory]::Delete($junction)
    $junction = $null

    $regularRoot = Join-Path $sandbox 'regular-e2e-runs'
    Initialize-ProtectedRunsRoot $regularRoot
    Assert-ExactProtectedRunDirectory $regularRoot 'Test physical E2E runs root'
} finally {
    if (-not [string]::IsNullOrWhiteSpace($junction) -and
        (Test-Path -LiteralPath $junction)) {
        [IO.Directory]::Delete($junction)
    }
    if (Test-Path -LiteralPath $sandbox) {
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}

Write-Output 'Physical MAU2 protected runs root contract: PASS'
