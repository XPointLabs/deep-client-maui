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
            1500) | Out-Null
        throw 'The hung process unexpectedly completed.'
    } catch {
        if ($_.Exception.ToString() -notmatch 'exceeded its deadline') { throw }
    }
    Start-Sleep -Milliseconds 500
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

'physical-chaos-bounded-runner-green'
