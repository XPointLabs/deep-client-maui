$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '..\..\..\eng\StrictLane.Common.ps1')

$sandbox = Join-Path ([IO.Path]::GetTempPath()) ("deep-path-safety-{0}" -f [Guid]::NewGuid().ToString('N'))
$root = Join-Path $sandbox 'artifacts'
$sibling = Join-Path $sandbox 'artifacts-escape'
$outside = Join-Path $sandbox 'outside'
New-Item -ItemType Directory -Force -Path $root, $sibling, $outside | Out-Null
try {
    $siblingRejected = $false
    try {
        Get-CanonicalContainedPath -Root $root -Candidate (Join-Path $sibling 'payload') | Out-Null
    } catch {
        $siblingRejected = $true
    }
    if (-not $siblingRejected) {
        throw 'Sibling-prefix containment escape was accepted.'
    }

    $junction = Join-Path $root 'junction'
    New-Item -ItemType Junction -Path $junction -Target $outside | Out-Null
    $junctionRejected = $false
    try {
        Remove-ContainedTree -Root $root -Candidate $junction
    } catch {
        $junctionRejected = $true
    }
    if (-not $junctionRejected) {
        throw 'Recursive cleanup traversed a junction.'
    }
    if (-not (Test-Path -LiteralPath $outside -PathType Container)) {
        throw 'The junction target was modified.'
    }
} finally {
    if (Test-Path -LiteralPath $junction) {
        Remove-Item -LiteralPath $junction -Force
    }
    Remove-Item -LiteralPath $sandbox -Recurse -Force
}
