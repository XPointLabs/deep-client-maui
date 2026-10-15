[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedCommit,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{32}$')][string]$NetworkId,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$Xna1Pin,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$GenesisHeadPin,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$MrXPublicKeySha256,
    [Parameter(Mandatory)][string]$RegistryOrigin,
    [Parameter(Mandatory)][string]$BootstrapDirectory,
    [Parameter(Mandatory)][string]$RuntimeEnvironmentPath,
    [string]$PrivateCertificateAuthorityPath,
    [ValidateSet('win-x64', 'win-arm64')][string]$RuntimeIdentifier = 'win-arm64',
    [switch]$CompileOnly,
    [switch]$Execute
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($CompileOnly -and $Execute) {
    throw 'Compile-only cannot publish or qualify a physical build.'
}

function Assert-NoRedirect([string]$Path) {
    for ($cursor = $Path; -not [string]::IsNullOrWhiteSpace($cursor);
            $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if (Test-Path -LiteralPath $cursor) {
            if (((Get-Item -LiteralPath $cursor -Force).Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'DID2 build paths must not traverse reparse points.'
            }
        }
        if ([IO.Path]::GetDirectoryName($cursor) -ceq $cursor) { break }
    }
}

function Resolve-Input([string]$Path, [long]$Maximum) {
    if (-not [IO.Path]::IsPathFullyQualified($Path)) {
        throw 'DID2 build inputs require absolute paths.'
    }
    $full = [IO.Path]::GetFullPath($Path)
    Assert-NoRedirect $full
    $file = Get-Item -LiteralPath $full -Force
    if ($file.PSIsContainer -or $file.Length -le 0 -or $file.Length -gt $Maximum) {
        throw 'DID2 build input is absent or outside its size bound.'
    }
    return $full
}

$privateCa = $null
$privateCaSha256 = $null
if ($PSBoundParameters.ContainsKey('PrivateCertificateAuthorityPath')) {
    $privateCa = Resolve-Input $PrivateCertificateAuthorityPath 16384
    $encoded = [IO.File]::ReadAllBytes($privateCa)
    if ($encoded[0] -eq 0x30) { $der = $encoded }
    else {
        $text = [Text.UTF8Encoding]::new($false, $true).GetString($encoded)
        if ($text -cnotmatch '\A\s*-----BEGIN CERTIFICATE-----\s*([A-Za-z0-9+/=\r\n]+)\s*-----END CERTIFICATE-----\s*\z') {
            throw 'Private trust requires exactly one public CA certificate, without keys or extra material.'
        }
        $der = [Convert]::FromBase64String($Matches[1])
    }
    if ([Security.Cryptography.X509Certificates.X509Certificate2]::GetCertContentType($der) -ne
            [Security.Cryptography.X509Certificates.X509ContentType]::Cert) {
        throw 'Private trust accepts a public certificate only, not a certificate/key container.'
    }
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($der)
    try {
        $constraints = @($certificate.Extensions | Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension] })
        $usage = @($certificate.Extensions | Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509KeyUsageExtension] })
        if ($certificate.HasPrivateKey -or $constraints.Count -ne 1 -or -not $constraints[0].CertificateAuthority -or
            $usage.Count -ne 1 -or ($usage[0].KeyUsages -band [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign) -eq 0 -or
            [Convert]::ToBase64String($certificate.RawData) -cne [Convert]::ToBase64String($der)) {
            throw 'Private trust requires an exact public CA certificate with certificate-signing usage.'
        }
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $privateCaSha256 = ([BitConverter]::ToString($sha.ComputeHash($der))).Replace('-', '').ToLowerInvariant() }
        finally { $sha.Dispose() }
    } finally { $certificate.Dispose() }
}
$origin = $null
if (-not [Uri]::TryCreate($RegistryOrigin, [UriKind]::Absolute, [ref]$origin) -or
    $origin.Scheme -cne 'https' -or
    $origin.Port -lt 1 -or
    $origin.HostNameType -notin @([UriHostNameType]::Dns, [UriHostNameType]::IPv4, [UriHostNameType]::IPv6) -or
    ($null -eq $privateCa -and ($origin.IsLoopback -or $origin.HostNameType -ne [UriHostNameType]::Dns -or -not $origin.IsDefaultPort)) -or
    $origin.AbsoluteUri -cne $RegistryOrigin -or $origin.AbsolutePath -cne '/' -or
    $origin.Query.Length -ne 0 -or $origin.Fragment.Length -ne 0 -or
    $origin.UserInfo.Length -ne 0) {
    throw 'DID2 build requires a canonical HTTPS Registry origin within its explicit trust profile.'
}
foreach ($value in @($NetworkId, $Xna1Pin, $GenesisHeadPin, $MrXPublicKeySha256)) {
    if ($value -cmatch '^0+$') { throw 'DID2 build pins and network ID must be nonzero.' }
}
$commit = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -cne $ExpectedCommit) {
    throw 'DID2 build source differs from the expected committed revision.'
}
$dirty = @(& git -C $repo status --porcelain)
if ($LASTEXITCODE -ne 0 -or (-not $CompileOnly -and $dirty.Count -ne 0)) {
    throw 'DID2 build requires a clean committed MAUI worktree.'
}
if (-not [IO.Path]::IsPathFullyQualified($BootstrapDirectory)) {
    throw 'DID2 bootstrap requires an absolute directory.'
}
$bootstrap = [IO.Path]::GetFullPath($BootstrapDirectory)
foreach ($asset in @('xna1.0000.bin', 'dts1.0000.bin', 'did2-genesis-adh1.bin')) {
    [void](Resolve-Input (Join-Path $bootstrap $asset) $(if ($asset -like '*adh1*') {4096} else {65535}))
}
$runtime = Resolve-Input $RuntimeEnvironmentPath 65535
$result = [ordered]@{
    schema = 'deep.did2-https-windows-build.v1'
    commit = $commit
    runtimeIdentifier = $RuntimeIdentifier
    status = 'preflight'
    execute = [bool]$Execute
    installed = $false
    deviceDeliveryVerified = $false
    compileOnly = [bool]$CompileOnly
    sourceClean = ($dirty.Count -eq 0)
    privateCaSha256 = $privateCaSha256
}
if ($Execute -or $CompileOnly) {
    $artifactRoot = Join-Path $repo 'artifacts\did2-https-windows'
    $output = Join-Path $artifactRoot ([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N'))
    Assert-NoRedirect $output
    [void][IO.Directory]::CreateDirectory($output)
    $buildVerb = if ($CompileOnly) { 'build' } else { 'publish' }
    $arguments = @($buildVerb, (Join-Path $repo 'src\Deep.Client.Maui\Deep.Client.Maui.csproj'),
        '-c', 'Debug', '-f', 'net10.0-windows10.0.19041.0',
        "-p:RuntimeIdentifierOverride=$RuntimeIdentifier", '-p:WindowsPackageType=None',
        '-p:DeepPhysicalE2E=true', '-p:DeepLocalDev=false', '-p:DeepDid2AccountProbe=false',
        '-p:DeepDid2HttpsAdmission=true', "-p:DeepPhysicalUatNetworkId=$NetworkId",
        "-p:DeepDid2HttpsOrigin=$RegistryOrigin", "-p:DeepDid2CanaryXna1Pin=$Xna1Pin",
        "-p:DeepDid2CanaryAdh1Pin=$GenesisHeadPin", "-p:DeepDid2BootstrapDirectory=$bootstrap",
        "-p:DeepSurvivalRuntimeEnv=$runtime", "-p:DeepMrXPublicKeySha256=$MrXPublicKeySha256",
        "-p:BaseOutputPath=$(Join-Path $output 'build-bin')\",
        '-p:DeepProtocolSourceCutover=true', '-p:BuildInParallel=false',
        '-p:UseSharedCompilation=false', '-nodeReuse:false', '-m:1', '-o', $output)
    if ($null -ne $privateCa) {
        $arguments += @("-p:DeepDid2PrivateCertificateAuthority=$privateCa", "-p:DeepDid2PrivateCaSha256=$privateCaSha256")
    }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw 'DID2 Windows build failed; no client was installed or launched.' }
    if ($CompileOnly) {
        $result.status = 'compiled-working-tree-not-device-qualified'
        $result | ConvertTo-Json -Depth 3
        return
    }
    $app = Join-Path $output 'Deep.Client.Maui.exe'
    $dll = Join-Path $output 'Deep.Client.Maui.dll'
    $result.status = 'built-not-installed'
    $result.apphostSha256 = (Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash.ToLowerInvariant()
    $result.assemblySha256 = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
    $result.app = $app
}
$result | ConvertTo-Json -Depth 3
