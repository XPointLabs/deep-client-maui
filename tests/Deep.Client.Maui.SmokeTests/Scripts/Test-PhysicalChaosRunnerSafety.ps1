$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$runner = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\eng\Invoke-PhysicalMau2CrossPlatform.ps1'))
$text = Get-Content -Raw -LiteralPath $runner
$match = [regex]::Match(
    $text,
    "(?s)Add-Type -TypeDefinition @'(?<source>.*?)\r?\n'@")
if (-not $match.Success) { throw 'Bounded process source was not found exactly once.' }
Add-Type -TypeDefinition $match.Groups['source'].Value

$powershell = 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
$pidFile = Join-Path $env:TEMP ('deep-physical-job-' + [guid]::NewGuid().ToString('N') + '.pid')
$spawnImmediately = '$p=Start-Process -FilePath ''' + $powershell + ''' -ArgumentList ''-NoProfile'',''-Command'',''Start-Sleep -Seconds 300'' -PassThru; Set-Content -LiteralPath ''' + $pidFile + ''' -Value $p.Id; Wait-Process -Id $p.Id'
try {
    try {
        [Deep.PhysicalE2E.BoundedProcess]::Run(
            $powershell,
            @('-NoProfile', '-Command', $spawnImmediately),
            (Split-Path -Parent $runner),
            5000) | Out-Null
        throw 'The hung process unexpectedly completed.'
    } catch {
        if ($_.Exception.ToString() -notmatch 'exceeded its deadline') { throw }
    }
    if (-not (Test-Path -LiteralPath $pidFile -PathType Leaf)) {
        throw 'The bounded child did not publish its immediate descendant PID before the deadline.'
    }
    $childPid = [int](Get-Content -LiteralPath $pidFile)
    if (Get-Process -Id $childPid -ErrorAction SilentlyContinue) {
        throw 'An immediate descendant escaped the suspended job assignment.'
    }
} finally {
    Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue
}

try {
    [Deep.PhysicalE2E.BoundedProcess]::Run(
        $powershell,
        @('-NoProfile', '-Command',
            '[Console]::Out.Write((''x'' * 1100000)); Start-Sleep -Seconds 300'),
        (Split-Path -Parent $runner),
        10000) | Out-Null
    throw 'The output flood unexpectedly completed.'
} catch {
    if ($_.Exception.ToString() -notmatch 'aggregate output limit') { throw }
}

$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    $runner, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw 'Physical runner did not parse for snapshot safety test.' }
$requiredFunctions = @(
    'Get-Sha256', 'Get-TextSha256', 'Read-ExactPinnedBytes',
    'Assert-AuthorityPathAncestors',
    'Set-ProtectedRunItem', 'Set-ProtectedRunTree',
    'New-ChaosDependencySnapshot', 'Close-ChaosDependencySnapshot')
foreach ($name in $requiredFunctions) {
    $matches = @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -ceq $name
    }, $true))
    if ($matches.Count -ne 1) { throw "Snapshot helper '$name' was not found exactly once." }
    Invoke-Expression $matches[0].Extent.Text
}

$snapshotWork = Join-Path $env:TEMP ('deep-physical-snapshot-' + [guid]::NewGuid().ToString('N'))
$sourceRoot = Join-Path $snapshotWork 'source'
$sourceScripts = Join-Path $sourceRoot 'scripts'
$sourceConfig = Join-Path $sourceRoot 'config\survival-uat-tls'
$snapshotRoot = Join-Path $snapshotWork 'snapshot'
$manifest = Join-Path $snapshotWork 'manifest.json'
$launcher = Join-Path $sourceScripts 'survival-dev.ps1'
$helper = Join-Path $sourceScripts 'helper.ps1'
$haproxyConfig = Join-Path $sourceConfig 'haproxy.cfg'
$lease = $null
try {
    [void][IO.Directory]::CreateDirectory($sourceScripts)
    [void][IO.Directory]::CreateDirectory($sourceConfig)
    [IO.File]::WriteAllText($launcher, "'reviewed-launcher'`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($helper, "'reviewed-helper'`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($haproxyConfig, "global`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($manifest, '{"reviewed":true}', [Text.UTF8Encoding]::new($false))
    Assert-AuthorityPathAncestors $sourceRoot $launcher 'Regular source file'
    $script:chaosManifestSha256 = Get-Sha256 $manifest
    $authority = [pscustomobject]@{
        DevOpsRoot = $sourceRoot
        Manifest = $manifest
        Files = @(
            [pscustomobject]@{ path = 'config/survival-uat-tls/haproxy.cfg'; sha256 = (Get-Sha256 $haproxyConfig) },
            [pscustomobject]@{ path = 'scripts/helper.ps1'; sha256 = (Get-Sha256 $helper) },
            [pscustomobject]@{ path = 'scripts/survival-dev.ps1'; sha256 = (Get-Sha256 $launcher) })
        Docker = 'docker'; DockerSha256 = '0' * 64
        DockerCompose = 'compose'; DockerComposeSha256 = '1' * 64
        PowerShell = $powershell; DotNet = 'dotnet'
        ManifestSha256 = $script:chaosManifestSha256
        DependencyTreeSha256 = '2' * 64
    }
    $lease = New-ChaosDependencySnapshot $authority $snapshotRoot

    [IO.File]::WriteAllText($launcher, "'malicious-launcher'`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($helper, "'malicious-helper'`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($haproxyConfig, "malicious`n", [Text.UTF8Encoding]::new($false))
    $snapshotLauncher = Join-Path $lease.DevOpsRoot 'scripts\survival-dev.ps1'
    if ((Get-Content -Raw -LiteralPath $snapshotLauncher) -cne "'reviewed-launcher'`n") {
        throw 'Snapshot did not retain the exact reviewed source bytes.'
    }
    if ((Get-Content -Raw -LiteralPath $lease.HAProxyConfig) -cne "global`n") {
        throw 'Snapshot did not retain the exact reviewed HAProxy configuration.'
    }
    [IO.File]::SetAttributes(
        $snapshotLauncher,
        [IO.File]::GetAttributes($snapshotLauncher) -band (-bnot [IO.FileAttributes]::ReadOnly))
    $mutationRejected = $false
    try {
        [IO.File]::WriteAllText($snapshotLauncher, 'replacement')
    } catch [IO.IOException] { $mutationRejected = $true }
    if (-not $mutationRejected) { throw 'Snapshot lease allowed write/replace during execution.' }
    $haproxyMutationRejected = $false
    try {
        [IO.File]::SetAttributes(
            $lease.HAProxyConfig,
            [IO.File]::GetAttributes($lease.HAProxyConfig) -band (-bnot [IO.FileAttributes]::ReadOnly))
        [IO.File]::WriteAllText($lease.HAProxyConfig, 'replacement')
    } catch [IO.IOException] { $haproxyMutationRejected = $true }
    if (-not $haproxyMutationRejected) {
        throw 'Snapshot lease allowed HAProxy configuration replacement during execution.'
    }

    Close-ChaosDependencySnapshot $lease
    $lease = $null
    [IO.File]::WriteAllText($snapshotLauncher, 'released')
    if ((Get-Content -Raw -LiteralPath $snapshotLauncher) -cne 'released') {
        throw 'Snapshot lease did not release after bounded cleanup.'
    }
} finally {
    if ($null -ne $lease) { Close-ChaosDependencySnapshot $lease }
    if (Test-Path -LiteralPath $snapshotWork) {
        Get-ChildItem -Force -Recurse -LiteralPath $snapshotWork -File |
            ForEach-Object { $_.Attributes = $_.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly) }
        Remove-Item -LiteralPath $snapshotWork -Recurse -Force
    }
}

'physical-chaos-bounded-runner-green'
