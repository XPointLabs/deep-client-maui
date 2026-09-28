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
    [Parameter(Mandatory)][string]$SigningKeyStore,
    [Parameter(Mandatory)][string]$SigningPasswordFile,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_-]{1,80}$')][string]$SigningAlias,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$ExpectedSignerSha256,
    [switch]$Execute
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

# Reuse the existing no-write source/bootstrap/HTTPS preflight. It does not
# build Windows, install or launch an app, or read private signing material.
$common = @{}
foreach ($name in @('ExpectedCommit', 'NetworkId', 'Xna1Pin', 'GenesisHeadPin',
    'MrXPublicKeySha256', 'RegistryOrigin', 'BootstrapDirectory', 'RuntimeEnvironmentPath')) {
    $common[$name] = $PSBoundParameters[$name]
}
$preflight = (& (Join-Path $PSScriptRoot 'Invoke-Did2HttpsWindowsBuild.ps1') @common) |
    ConvertFrom-Json
if ($preflight.status -cne 'preflight') { throw 'DID2 common preflight did not complete.' }

function Assert-Input([string]$Path, [long]$Maximum) {
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw 'Signing inputs require absolute paths.' }
    $full = [IO.Path]::GetFullPath($Path)
    for ($cursor = $full; -not [string]::IsNullOrWhiteSpace($cursor);
        $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if ((Test-Path -LiteralPath $cursor) -and
            (((Get-Item -LiteralPath $cursor -Force).Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw 'DID2 signing paths must not traverse reparse points.'
        }
        if ([IO.Path]::GetDirectoryName($cursor) -ceq $cursor) { break }
    }
    $file = Get-Item -LiteralPath $full -Force
    if ($file.PSIsContainer -or $file.Length -le 0 -or $file.Length -gt $Maximum) {
        throw 'DID2 signing input is absent or outside its size bound.'
    }
    return $full
}
$keyStore = Assert-Input $SigningKeyStore 1048576
$passwordFile = Assert-Input $SigningPasswordFile 4096
$java = 'C:\Program Files\Android\openjdk\jdk-21.0.8\bin\java.exe'
$signer = 'C:\Program Files (x86)\Android\android-sdk\build-tools\36.0.0\lib\apksigner.jar'
foreach ($tool in @($java, $signer)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw 'Pinned APK signature tool is unavailable.' }
}
$result = [ordered]@{
    schema = 'deep.did2-https-android-build.v1'
    commit = $preflight.commit
    package = 'network.xpoint.deep.did2https'
    runtimeIdentifier = 'android-arm64'
    signerSha256 = $ExpectedSignerSha256
    status = 'preflight'
    execute = [bool]$Execute
    installed = $false
    deviceDeliveryVerified = $false
}
if ($Execute) {
    $output = Join-Path $repo ('artifacts\did2-https-android\' +
        [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N'))
    # All parents are repository-owned; reject redirected artifact roots too.
    for ($cursor = $output; -not [string]::IsNullOrWhiteSpace($cursor);
        $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if ((Test-Path -LiteralPath $cursor) -and
            (((Get-Item -LiteralPath $cursor -Force).Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw 'Redirected Android output is forbidden.' }
        if ([IO.Path]::GetDirectoryName($cursor) -ceq $cursor) { break }
    }
    [void][IO.Directory]::CreateDirectory($output)
    $arguments = @('build', (Join-Path $repo 'src\Deep.Client.Maui\Deep.Client.Maui.csproj'),
        '-c', 'Debug', '-f', 'net10.0-android', '-p:RuntimeIdentifier=android-arm64',
        '-p:AndroidPackageFormats=apk', '-p:DeepPhysicalE2E=true', '-p:DeepLocalDev=false',
        '-p:DeepDid2AccountProbe=false', '-p:DeepDid2HttpsAdmission=true',
        "-p:DeepPhysicalUatNetworkId=$NetworkId", "-p:DeepDid2HttpsOrigin=$RegistryOrigin",
        "-p:DeepDid2CanaryXna1Pin=$Xna1Pin", "-p:DeepDid2CanaryAdh1Pin=$GenesisHeadPin",
        "-p:DeepDid2BootstrapDirectory=$BootstrapDirectory", "-p:DeepSurvivalRuntimeEnv=$RuntimeEnvironmentPath",
        "-p:DeepMrXPublicKeySha256=$MrXPublicKeySha256", "-p:BaseOutputPath=$output\build-bin\",
        '-p:AndroidKeyStore=false', '-p:JavaSdkDirectory=C:\Program Files\Android\openjdk\jdk-21.0.8',
        '-p:AndroidSdkDirectory=C:\Program Files (x86)\Android\android-sdk',
        '-p:DeepProtocolSourceCutover=true',
        '-p:BuildInParallel=false', '-p:UseSharedCompilation=false',
        '-p:Aapt2DaemonMaxInstanceCount=1', '-nodeReuse:false', '-m:1')
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw 'DID2 Android build failed; no package was installed.' }
    $unsigned = Join-Path $output 'build-bin\Debug\net10.0-android\android-arm64\network.xpoint.deep.did2https-Signed.apk'
    if (-not (Test-Path -LiteralPath $unsigned -PathType Leaf)) { throw 'Exact DID2 HTTPS APK is absent.' }
    # Keep production key arguments outside MSBuild. Match the existing SDK
    # signing workflow, using explicit PKCS12 and file-based passwords only.
    $apk = Join-Path $output 'network.xpoint.deep.did2https-Signed.apk'
    $signArguments = @('-jar', $signer, 'sign', '--out', $apk, '--ks', $keyStore,
        '--ks-type', 'PKCS12', '--ks-key-alias', $SigningAlias,
        '--ks-pass', "file:$passwordFile", $unsigned)
    $signingOutput = (& $java @signArguments 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'Explicit pinned SDK APK signing failed; no package was installed.' }
    $verified = (& $java '-jar' $signer 'verify' '--print-certs' $apk 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0 -or
        $verified -notmatch 'Signer #1 certificate SHA-256 digest: ([0-9a-f]{64})' -or
        $Matches[1] -cne $ExpectedSignerSha256) { throw 'DID2 APK signer does not match its explicit pin.' }
    $result.status = 'built-signature-verified-not-installed'
    $result.apkSha256 = (Get-FileHash -LiteralPath $apk -Algorithm SHA256).Hash.ToLowerInvariant()
    $result.apk = $apk
}
$result | ConvertTo-Json -Depth 3
