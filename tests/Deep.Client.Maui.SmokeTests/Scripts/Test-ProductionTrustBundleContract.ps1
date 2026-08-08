Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
. (Join-Path $root 'eng\Read-ProductionTrustBundle.ps1')
. (Join-Path $root 'eng\Assert-AndroidBundletool.ps1')

$temp = Join-Path ([IO.Path]::GetTempPath()) ("deep-trust-bundle-" + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temp) | Out-Null
try {
    $hex32 = -join (1..32 | ForEach-Object { '11' })
    $hex16 = -join (1..16 | ForEach-Object { '22' })
    $bundle = [ordered]@{
        schemaVersion = 1
        trustFloor = [ordered]@{
            mrXPublicKeySha256 = $hex32
            networkId = $hex16
            authorityGeneration = '1'
            authorityHash = -join (1..32 | ForEach-Object { '33' })
            revocationGeneration = '2'
            revocationHeadHash = -join (1..32 | ForEach-Object { '44' })
            revocationSnapshotHash = -join (1..32 | ForEach-Object { '55' })
            topologyGeneration = '3'
            topologyHash = -join (1..32 | ForEach-Object { '66' })
        }
        android = [ordered]@{
            buildIdSha256 = -join (1..32 | ForEach-Object { '77' })
            applicationId = 'network.xpoint.deep'
            versionCode = '4'
            playAppSigningLineageSha256 = @(
                (-join (1..32 | ForEach-Object { '88' })),
                (-join (1..32 | ForEach-Object { '99' })))
        }
    }
    $validPath = Join-Path $temp 'valid.json'
    [IO.File]::WriteAllText(
        $validPath,
        ($bundle | ConvertTo-Json -Depth 5 -Compress),
        [Text.UTF8Encoding]::new($false))

    $android = Import-ProductionTrustBundle -Path $validPath -RequireAndroid
    if ($android.Count -ne 13) { throw "Android trust bundle returned $($android.Count), expected 13 properties." }
    $windows = Import-ProductionTrustBundle -Path $validPath
    if ($windows.Count -ne 9) { throw "Windows trust bundle returned $($windows.Count), expected 9 properties." }

    $writer = [IO.File]::Open(
        $validPath,
        [IO.FileMode]::Open,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::ReadWrite)
    try {
        try {
            Import-ProductionTrustBundle -Path $validPath | Out-Null
            throw 'Trust bundle with an active same-length mutation handle was accepted.'
        }
        catch {
            if ($_.Exception.Message -like 'Trust bundle with an active same-length*') { throw }
        }
    }
    finally {
        $writer.Dispose()
    }

    function Assert-Rejected([string]$name, [string]$json, [switch]$RequireAndroid) {
        $path = Join-Path $temp "$name.json"
        [IO.File]::WriteAllText($path, $json, [Text.UTF8Encoding]::new($false))
        try {
            Import-ProductionTrustBundle -Path $path -RequireAndroid:$RequireAndroid | Out-Null
            throw "Malformed trust bundle '$name' was accepted."
        }
        catch {
            if ($_.Exception.Message -like "Malformed trust bundle '$name' was accepted.*") { throw }
        }
    }

    Assert-Rejected 'missing' '{"schemaVersion":1,"trustFloor":{},"android":null}'
    $partial = $bundle | ConvertTo-Json -Depth 5 -Compress
    $partial = $partial.Replace(',"topologyHash":"' +
        (-join (1..32 | ForEach-Object { '66' })) + '"', '')
    Assert-Rejected 'partial' $partial
    $extra = ($bundle | ConvertTo-Json -Depth 5 -Compress).Replace(
        '"schemaVersion":1', '"schemaVersion":1,"unexpected":true')
    Assert-Rejected 'extra' $extra
    $duplicate = ($bundle | ConvertTo-Json -Depth 5 -Compress).Replace(
        '"schemaVersion":1', '"schemaVersion":1,"schemaVersion":1')
    Assert-Rejected 'duplicate' $duplicate
    $escapedDuplicate = ($bundle | ConvertTo-Json -Depth 5 -Compress).Replace(
        '"networkId":"', '"\u006eetworkId":"' + $hex16 + '","networkId":"')
    Assert-Rejected 'escaped-duplicate' $escapedDuplicate
    $missingAndroid = [regex]::Replace(
        ($bundle | ConvertTo-Json -Depth 5 -Compress),
        ',"android":\{.*\}$',
        ',"android":null}',
        [Text.RegularExpressions.RegexOptions]::Singleline)
    Assert-Rejected 'missing-android' $missingAndroid -RequireAndroid

    $wrongBundletool = Join-Path $temp 'bundletool-all-1.18.3.jar'
    [IO.File]::WriteAllBytes($wrongBundletool, [byte[]](1, 2, 3, 4))
    $beforeFailureDirectories = @([IO.Directory]::GetDirectories(
        [IO.Path]::GetTempPath(), 'deep-bundletool-lease-*'))
    try {
        Open-PinnedAndroidBundletool -Path $wrongBundletool | Out-Null
        throw 'Bundletool with an unapproved SHA-256 was accepted.'
    }
    catch {
        if ($_.Exception.Message -like 'Bundletool with an unapproved*') { throw }
        if ($_.Exception.Message -notlike '*bundletool 1.18.3*') { throw }
    }
    $afterFailureDirectories = @([IO.Directory]::GetDirectories(
        [IO.Path]::GetTempPath(), 'deep-bundletool-lease-*'))
    if (@($afterFailureDirectories | Where-Object {
            $_ -notin $beforeFailureDirectories }).Count -ne 0) {
        throw 'Failed bundletool verification leaked a temporary lease directory.'
    }

    $leaseSource = Join-Path $temp 'lease-source.jar'
    [IO.File]::WriteAllBytes($leaseSource, [byte[]](5, 6, 7, 8))
    $leaseSha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $expectedLeaseHash = [BitConverter]::ToString(
            $leaseSha256.ComputeHash([byte[]](5, 6, 7, 8))).Replace('-', '').ToLowerInvariant()
    }
    finally { $leaseSha256.Dispose() }
    $attack = [pscustomobject]@{
        RenameBlocked = $false
        WriteBlocked = $false
        DeleteBlocked = $false
    }
    $lease = Open-VerifiedBundletoolLease -Path $leaseSource `
        -ExpectedSha256 $expectedLeaseHash -AfterFlush {
            param($leasedPath)
            try { Move-Item -LiteralPath $leasedPath -Destination ($leasedPath + '.swap') -Force }
            catch { $attack.RenameBlocked = $true }
            try {
                $writer = [IO.File]::Open($leasedPath, [IO.FileMode]::Open,
                    [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
                $writer.Dispose()
            }
            catch { $attack.WriteBlocked = $true }
            try { Remove-Item -LiteralPath $leasedPath -Force }
            catch { $attack.DeleteBlocked = $true }
            if (-not $attack.RenameBlocked -or -not $attack.WriteBlocked -or
                -not $attack.DeleteBlocked) {
                throw 'Bundletool destination was mutable between flush and lease return.'
            }
        }
    $leaseDirectory = $lease.Directory
    try {
        [IO.File]::WriteAllBytes($leaseSource, [byte[]](9, 9, 9, 9))
        try {
            Move-Item -LiteralPath $lease.Path -Destination ($lease.Path + '.replaced') -Force
            throw 'Leased bundletool path was replaceable after verification.'
        }
        catch {
            if ($_.Exception.Message -like 'Leased bundletool path was replaceable*') { throw }
        }
        $lease.Lease.Position = 0
        $leasedSha256 = [Security.Cryptography.SHA256]::Create()
        try {
            $leasedHash = [BitConverter]::ToString(
                $leasedSha256.ComputeHash($lease.Lease)).Replace('-', '').ToLowerInvariant()
        }
        finally { $leasedSha256.Dispose() }
        $lease.Lease.Position = 0
        if ($leasedHash -cne $expectedLeaseHash) {
            throw 'Leased bundletool bytes changed after the source path was replaced.'
        }
    }
    finally { Close-PinnedAndroidBundletool -Lease $lease }
    if ([IO.Directory]::Exists($leaseDirectory)) {
        throw 'Bundletool lease cleanup left its private directory behind.'
    }

    $androidBuild = [IO.File]::ReadAllText((Join-Path $root 'eng\build-android-play.ps1'))
    if ([regex]::Matches($androidBuild, '(?m)^\s*@trustMsBuildArguments(?:\s+`)?\s*$').Count -ne 2) {
        throw 'Every Android publish invocation must receive the complete trust bundle arguments.'
    }
    foreach ($required in @(
        "[Parameter(Mandatory = `$true)]`n    [string]`$AndroidCodeTransparencyManifest",
        'DeepProductionAndroidCodeTransparencyManifest',
        'build-apks',
        '--mode=default',
        'Deep.AndroidTransparency.Tool',
        'Generated default APK set does not match')) {
        if ($androidBuild.Replace("`r`n", "`n").IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
            throw "Android build is missing code-transparency gate '$required'."
        }
    }
    if ($androidBuild.IndexOf(
            'Generated default APK set does not match', [StringComparison]::Ordinal) -gt
        $androidBuild.IndexOf(
            'Copy-Item -LiteralPath $bundle.FullName -Destination $finalBundle',
            [StringComparison]::Ordinal)) {
        throw 'Android AAB is published before code-transparency verification.'
    }
    if ($androidBuild.Contains('firebaseTarget') -or
        $androidBuild.Contains('Copy-Item -LiteralPath $GoogleServicesJson') -or
        -not $androidBuild.Contains('DeepGoogleServicesJson')) {
        throw 'Android build must consume external Firebase JSON without mutating the workspace.'
    }
    if ($androidBuild.IndexOf('Open-PinnedAndroidBundletool', [StringComparison]::Ordinal) -lt 0 -or
        $androidBuild.IndexOf('Open-PinnedAndroidBundletool', [StringComparison]::Ordinal) -gt
        $androidBuild.IndexOf('dotnet publish', [StringComparison]::Ordinal)) {
        throw 'Android publish must verify the pinned bundletool before building.'
    }
    $preparationBuild = [IO.File]::ReadAllText(
        (Join-Path $root 'eng\prepare-android-code-transparency.ps1'))
    if ($preparationBuild.Contains('firebaseTarget') -or
        $preparationBuild.Contains('Copy-Item -LiteralPath $GoogleServicesJson') -or
        -not $preparationBuild.Contains('DeepGoogleServicesJson')) {
        throw 'Android transparency preparation must not mutate the workspace Firebase file.'
    }
    if ($preparationBuild.IndexOf('Open-PinnedAndroidBundletool', [StringComparison]::Ordinal) -lt 0 -or
        $preparationBuild.IndexOf('Open-PinnedAndroidBundletool', [StringComparison]::Ordinal) -gt
        $preparationBuild.IndexOf('dotnet publish', [StringComparison]::Ordinal)) {
        throw 'Android transparency preparation must verify pinned bundletool before building.'
    }
    foreach ($script in @($androidBuild, $preparationBuild)) {
        if (-not $script.Contains('Close-PinnedAndroidBundletool')) {
            throw 'Android bundletool lease is not closed by a release script.'
        }
    }
    $windowsBuild = [IO.File]::ReadAllText((Join-Path $root 'eng\build-windows-msix.ps1'))
    if ([regex]::Matches($windowsBuild, '(?m)^\s*\$trustMsBuildArguments\s*$').Count -lt 2) {
        throw 'Every Windows MSBuild/publish invocation must receive the complete trust bundle arguments.'
    }
}
finally {
    if ([IO.Directory]::Exists($temp)) { [IO.Directory]::Delete($temp, $true) }
}
