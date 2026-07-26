$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

[Console]::Error.Write(('e' * (1024 * 1024)))
[Console]::Out.WriteLine('pipe-drain-complete')
