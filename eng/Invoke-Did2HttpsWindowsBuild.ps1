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
    [ValidateSet('win-x64', 'win-arm64')][string]$RuntimeIdentifier = 'win-arm64',
    [switch]$Execute
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

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

$origin = $null
if (-not [Uri]::TryCreate($RegistryOrigin, [UriKind]::Absolute, [ref]$origin) -or
    $origin.Scheme -cne 'https' -or $origin.IsLoopback -or
    $origin.HostNameType -ne [UriHostNameType]::Dns -or -not $origin.IsDefaultPort -or
    $origin.AbsoluteUri -cne $RegistryOrigin -or $origin.AbsolutePath -cne '/' -or
    $origin.Query.Length -ne 0 -or $origin.Fragment.Length -ne 0 -or
    $origin.UserInfo.Length -ne 0) {
    throw 'DID2 build requires a canonical DNS HTTPS Registry origin.'
}
foreach ($value in @($NetworkId, $Xna1Pin, $GenesisHeadPin, $MrXPublicKeySha256)) {
    if ($value -cmatch '^0+$') { throw 'DID2 build pins and network ID must be nonzero.' }
}
$commit = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -cne $ExpectedCommit) {
    throw 'DID2 build source differs from the expected committed revision.'
}
$dirty = @(& git -C $repo status --porcelain)
if ($LASTEXITCODE -ne 0 -or $dirty.Count -ne 0) {
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
}
if ($Execute) {
    $artifactRoot = Join-Path $repo 'artifacts\did2-https-windows'
    $output = Join-Path $artifactRoot ([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N'))
    Assert-NoRedirect $output
    [void][IO.Directory]::CreateDirectory($output)
    $arguments = @('publish', (Join-Path $repo 'src\Deep.Client.Maui\Deep.Client.Maui.csproj'),
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
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw 'DID2 Windows build failed; no client was installed or launched.' }
    $app = Join-Path $output 'Deep.Client.Maui.exe'
    $dll = Join-Path $output 'Deep.Client.Maui.dll'
    $result.status = 'built-not-installed'
    $result.apphostSha256 = (Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash.ToLowerInvariant()
    $result.assemblySha256 = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
    $result.app = $app
}
$result | ConvertTo-Json -Depth 3
