$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$engine = (Get-Process -Id $PID).Path
$child = Start-Process -FilePath $engine -ArgumentList @(
    '-NoLogo',
    '-NoProfile',
    '-Command',
    'Start-Sleep -Seconds 60'
) -PassThru

[Console]::Out.WriteLine("TIMEOUT_CHILD_PID=$($child.Id)")
[Console]::Out.Flush()
Wait-Process -Id $child.Id
