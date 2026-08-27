$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '..\..\..\eng\StrictLane.Common.ps1')

$sandbox = Join-Path ([IO.Path]::GetTempPath()) ("deep-path-safety-{0}" -f [Guid]::NewGuid().ToString('N'))
$root = Join-Path $sandbox 'artifacts'
$sibling = Join-Path $sandbox 'artifacts-escape'
$outside = Join-Path $sandbox 'outside'
New-Item -ItemType Directory -Force -Path $root, $sibling, $outside | Out-Null
$junction = $null
$payloadJunction = $null
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

    $payload = Join-Path $root 'payload'
    $executableDirectory = Join-Path $payload 'app'
    New-Item -ItemType Directory -Force -Path $executableDirectory | Out-Null
    $executable = Join-Path $executableDirectory 'Deep.Client.Maui.exe'
    $library = Join-Path $payload 'Deep.Client.Maui.dll'
    [IO.File]::WriteAllText($executable, 'executable')
    [IO.File]::WriteAllText($library, 'library')
    $snapshot = Get-StrictPayloadSnapshot `
        -RepositoryRoot $root `
        -PayloadRoot $payload `
        -ExecutablePath $executable

    $duplicateSubstitution = [ordered]@{
        payloadRoot = $snapshot.payloadRoot
        executable = $snapshot.executable
        files = @($snapshot.files[0], $snapshot.files[0])
    }
    $duplicateRejected = $false
    try {
        Assert-StrictPayloadSnapshotsEqual -Expected $duplicateSubstitution -Actual $snapshot
    } catch {
        $duplicateRejected = $true
    }
    if (-not $duplicateRejected) {
        throw 'Duplicate manifest entry substituted for a distinct payload file.'
    }

    $separatorSubstitution = [ordered]@{
        payloadRoot = $snapshot.payloadRoot
        executable = $snapshot.executable.Replace('/', '\')
        files = $snapshot.files
    }
    $separatorRejected = $false
    try {
        Assert-StrictPayloadSnapshotsEqual -Expected $separatorSubstitution -Actual $snapshot
    } catch {
        $separatorRejected = $true
    }
    if (-not $separatorRejected) {
        throw 'Alternate-separator executable substitution was accepted.'
    }

    $replacement = Join-Path $payload 'replacement.tmp'
    [IO.File]::WriteAllText($replacement, 'replacement')
    $lease = Open-StrictPayloadLease `
        -RepositoryRoot $root `
        -PayloadRoot $payload `
        -ExecutablePath $executable
    try {
        foreach ($operation in @('write', 'replace', 'delete')) {
            $job = Start-Job -ArgumentList $operation, $executable, $replacement -ScriptBlock {
                param($Operation, $Executable, $Replacement)
                try {
                    if ($Operation -eq 'write') {
                        [IO.File]::WriteAllText($Executable, 'concurrent mutation')
                    } elseif ($Operation -eq 'replace') {
                        [IO.File]::Replace($Replacement, $Executable, $null)
                    } else {
                        [IO.File]::Delete($Executable)
                    }
                    return $false
                } catch {
                    return $true
                }
            }
            $rejected = Receive-Job -Job $job -Wait
            Remove-Job -Job $job -Force
            if ($rejected -ne $true) {
                throw "Concurrent payload $operation succeeded while strict read leases were held."
            }
        }
        $leasedPostSnapshot = Get-StrictPayloadSnapshot `
            -RepositoryRoot $root `
            -PayloadRoot $payload `
            -ExecutablePath $executable
        Assert-StrictPayloadSnapshotsEqual -Expected $lease.snapshot -Actual $leasedPostSnapshot
    } finally {
        Close-StrictPayloadLease -Lease $lease
    }
    if (Test-Path -LiteralPath $replacement) {
        Remove-Item -LiteralPath $replacement -Force
    }

    $payloadJunction = Join-Path $payload 'linked-outside'
    New-Item -ItemType Junction -Path $payloadJunction -Target $outside | Out-Null
    $payloadJunctionRejected = $false
    try {
        Get-StrictPayloadSnapshot `
            -RepositoryRoot $root `
            -PayloadRoot $payload `
            -ExecutablePath $executable | Out-Null
    } catch {
        $payloadJunctionRejected = $true
    }
    if (-not $payloadJunctionRejected) {
        throw 'Payload snapshot traversed a junction.'
    }
} finally {
    if (-not [string]::IsNullOrWhiteSpace($payloadJunction) -and (Test-Path -LiteralPath $payloadJunction)) {
        Remove-Item -LiteralPath $payloadJunction -Force
    }
    if (-not [string]::IsNullOrWhiteSpace($junction) -and (Test-Path -LiteralPath $junction)) {
        Remove-Item -LiteralPath $junction -Force
    }
    Remove-Item -LiteralPath $sandbox -Recurse -Force
}
