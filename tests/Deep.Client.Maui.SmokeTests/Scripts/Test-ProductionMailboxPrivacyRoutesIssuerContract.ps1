$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$issuerScript = Join-Path $repoRoot 'eng\Issue-ProductionMailboxPrivacyRoutes.ps1'
$issuerProject = Join-Path $repoRoot `
    'eng\Deep.ProductionMailboxPrivacyRoutesIssuer\Deep.ProductionMailboxPrivacyRoutesIssuer.csproj'
$keygenProject = Join-Path $repoRoot `
    'tests\Deep.SyntheticPrivacyRouteKeys\Deep.SyntheticPrivacyRouteKeys.csproj'
$sandboxLeaf = "deep-privacy-route-issuer-contract-$([Guid]::NewGuid().ToString('N'))"
$sandbox = Join-Path ([IO.Path]::GetTempPath()) $sandboxLeaf
$privateKey = Join-Path $sandbox 'mr-x.private'
$publicKey = Join-Path $sandbox 'mr-x.public'
$candidate = Join-Path $sandbox 'candidate.json'
$invalid = Join-Path $sandbox 'invalid.json'
$output = Join-Path $sandbox 'published'
$owner = [Security.Principal.WindowsIdentity]::GetCurrent().Name

function Write-Utf8NoBom([string]$Path, [string]$Value) {
    [IO.File]::WriteAllText($Path, $Value, [Text.UTF8Encoding]::new($false))
}

function New-CanonicalCandidate([long]$NotBefore, [long]$Expires, [string]$PrimaryOrigin) {
    $network = '71' * 16
    $routerMarkers = @(0x10, 0x11, 0x12, 0x30, 0x31, 0x32)
    $keyMarkers = @(0x20, 0x21, 0x22, 0x40, 0x41, 0x42)
    $hop = for ($index = 0; $index -lt 6; $index++) {
        $router = ('{0:x2}' -f $routerMarkers[$index]) * 32
        $key = ('{0:x2}' -f $keyMarkers[$index]) * 32
        '{"routerId":"' + $router + '","x25519PublicKey":"' + $key + '"}'
    }
    return '{"schemaVersion":1,"developmentOnly":false,"networkId":"' + $network +
        '","notBeforeUnixSeconds":' + $NotBefore + ',"expiresUnixSeconds":' + $Expires +
        ',"primary":{"entryOrigin":"' + $PrimaryOrigin + '","hops":[' +
        ($hop[0..2] -join ',') + ']},"fallback":{"entryOrigin":"https://privacy-b.example/","hops":[' +
        ($hop[3..5] -join ',') + ']}}'
}

function Get-TreeDigest([string]$Path) {
    $lines = foreach ($name in @(
        'production-mailbox-privacy-routes.v1.json',
        'production-mailbox-privacy-routes.v1.sig',
        'production-mailbox-privacy-routes.v1.pub')) {
        $file = Get-Item -Force -LiteralPath (Join-Path $Path $name)
        "$name`t$($file.Length)`t$((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant())"
    }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($lines -join "`n") + "`n")
    try {
        $hasher = [Security.Cryptography.SHA256]::Create()
        try {
            $hash = $hasher.ComputeHash($bytes)
            try {
                return ([BitConverter]::ToString($hash)).Replace('-', '').ToLowerInvariant()
            }
            finally { [Array]::Clear($hash, 0, $hash.Length) }
        } finally { $hasher.Dispose() }
    } finally { [Array]::Clear($bytes, 0, $bytes.Length) }
}

function Invoke-ExpectedFailure([string]$CandidatePath) {
    $failed = $false
    try {
        & $issuerScript -CandidateJsonPath $CandidatePath `
            -MrXPrivateKeyPath $privateKey -MrXPublicKeyPath $publicKey `
            -OutputDirectory $output 2>$null | Out-Null
    } catch {
        $failed = $true
    }
    if (-not $failed) { throw 'Issuer unexpectedly accepted an invalid synthetic input.' }
}

function Remove-Sandbox {
    if (-not (Test-Path -LiteralPath $sandbox)) { return }
    $full = [IO.Path]::GetFullPath($sandbox).TrimEnd('\', '/')
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + '\'
    if (-not $full.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $full) -cne $sandboxLeaf) {
        throw 'Refusing to remove a non-owned issuer contract sandbox.'
    }
    foreach ($file in Get-ChildItem -Force -File -Recurse -LiteralPath $full) {
        $file.IsReadOnly = $false
    }
    Remove-Item -Force -Recurse -LiteralPath $full
}

[IO.Directory]::CreateDirectory($sandbox) | Out-Null
try {
    dotnet restore $issuerProject --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Issuer locked restore failed.' }
    dotnet build $issuerProject -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Issuer build failed.' }
    dotnet restore $keygenProject --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic key generator locked restore failed.' }
    dotnet build $keygenProject -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic key generator build failed.' }
    dotnet run --project $keygenProject -c Release --no-build --no-restore -- generate `
        $privateKey $publicKey
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic key generation failed.' }

    $icacls = Join-Path $env:SystemRoot 'System32\icacls.exe'
    & $icacls $privateKey '/inheritance:r' '/grant:r' "${owner}:F" 'SYSTEM:F' `
        'Administrators:F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not protect the synthetic private key ACL.' }
    (Get-Item -Force -LiteralPath $privateKey).IsReadOnly = $true
    $privateHash = (Get-FileHash -LiteralPath $privateKey -Algorithm SHA256).Hash

    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $first = New-CanonicalCandidate ($now - 60) ($now + 3600) 'https://privacy-a.example/'
    Write-Utf8NoBom $candidate $first
    & $issuerScript -CandidateJsonPath $candidate `
        -MrXPrivateKeyPath $privateKey -MrXPublicKeyPath $publicKey `
        -OutputDirectory $output | Out-Null
    $names = @(Get-ChildItem -Force -LiteralPath $output | Sort-Object Name |
        ForEach-Object Name)
    $expectedNames = @(
        'production-mailbox-privacy-routes.v1.json',
        'production-mailbox-privacy-routes.v1.pub',
        'production-mailbox-privacy-routes.v1.sig')
    if ($names.Count -ne 3 -or (Compare-Object $expectedNames $names)) {
        throw 'Issuer did not publish the exact three-artifact set.'
    }
    if ([Text.Encoding]::UTF8.GetString(
        [IO.File]::ReadAllBytes((Join-Path $output $expectedNames[0]))) -cne $first -or
        (Get-Item -LiteralPath (Join-Path $output $expectedNames[1])).Length -ne 32 -or
        (Get-Item -LiteralPath (Join-Path $output $expectedNames[2])).Length -ne 64) {
        throw 'Issuer changed candidate bytes or emitted invalid artifact lengths.'
    }
    $firstDigest = Get-TreeDigest $output

    Write-Utf8NoBom $invalid (" " + $first)
    Invoke-ExpectedFailure $invalid
    if ((Get-TreeDigest $output) -cne $firstDigest) {
        throw 'Failed issuance changed the previously published artifact set.'
    }
    Write-Utf8NoBom $invalid ($first.Replace('"developmentOnly":false',
        '"developmentOnly":true'))
    Invoke-ExpectedFailure $invalid
    Write-Utf8NoBom $invalid ($first.Replace('https://privacy-a.example/',
        'http://privacy-a.example/'))
    Invoke-ExpectedFailure $invalid
    Write-Utf8NoBom $invalid (New-CanonicalCandidate ($now - 7200) ($now - 301) `
        'https://privacy-a.example/')
    Invoke-ExpectedFailure $invalid
    $overlap = $first.Replace(('30' * 32), ('10' * 32))
    Write-Utf8NoBom $invalid $overlap
    Invoke-ExpectedFailure $invalid

    (Get-Item -Force -LiteralPath $privateKey).IsReadOnly = $false
    Invoke-ExpectedFailure $candidate
    (Get-Item -Force -LiteralPath $privateKey).IsReadOnly = $true

    $second = New-CanonicalCandidate ($now - 30) ($now + 7200) 'https://privacy-c.example/'
    Write-Utf8NoBom $candidate $second
    & $issuerScript -CandidateJsonPath $candidate `
        -MrXPrivateKeyPath $privateKey -MrXPublicKeyPath $publicKey `
        -OutputDirectory $output | Out-Null
    if ((Get-TreeDigest $output) -ceq $firstDigest -or
        [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes(
            (Join-Path $output 'production-mailbox-privacy-routes.v1.json'))) -cne $second) {
        throw 'Atomic rotation did not publish the complete second candidate.'
    }
    if ((Test-Path -LiteralPath (Join-Path $sandbox '.published.stage')) -or
        (Test-Path -LiteralPath (Join-Path $sandbox '.published.backup')) -or
        (Test-Path -LiteralPath (Join-Path $sandbox '.published.issue.lock'))) {
        throw 'Issuer left transient publication state behind.'
    }
    if ((Get-FileHash -LiteralPath $privateKey -Algorithm SHA256).Hash -cne $privateHash -or
        -not (Get-Item -Force -LiteralPath $privateKey).IsReadOnly) {
        throw 'Issuer changed the protected private key fixture.'
    }

    $scriptText = Get-Content -Raw -LiteralPath $issuerScript
    $programText = Get-Content -Raw -LiteralPath (Join-Path (Split-Path $issuerProject) 'Program.cs')
    foreach ($required in @('Assert-ProtectedPrivateKey', '.stage', '.backup',
        '[IO.Directory]::Move($stage, $output)')) {
        if ($scriptText.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
            throw "Issuer script is missing required contract marker: $required"
        }
    }
    foreach ($required in @('PublicKeyAuth.SignDetached', 'PublicKeyAuth.VerifyDetached',
        'CryptographicOperations.ZeroMemory(privateKey)', 'FileOptions.WriteThrough')) {
        if ($programText.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
            throw "Issuer implementation is missing required contract marker: $required"
        }
    }

    Write-Output 'Production mailbox privacy-route issuer contract: PASS'
} finally {
    Remove-Sandbox
}
