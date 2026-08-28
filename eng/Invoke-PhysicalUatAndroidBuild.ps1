[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^(10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[01])\.)')]
    [string]$LanHost,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9](?:[a-z0-9.-]{0,251}[a-z0-9])?$')]
    [string]$PublicHost,
    [string]$DevOpsRoot = (Join-Path $PSScriptRoot '..\..\deep-devops'),
    [string]$MailboxSecretRoot = 'C:\Work\DeepSession\secrets\survival-uat-production-mailbox',
    [string]$TlsSecretRoot = 'C:\Work\DeepSession\secrets\survival-uat-tls',
    [string]$Keystore = $env:XPOINT_ANDROID_KEYSTORE,
    [string]$PasswordFile = $env:XPOINT_ANDROID_SIGNING_PASSWORD_FILE,
    [string]$Alias = $(if ($env:XPOINT_ANDROID_KEY_ALIAS) { $env:XPOINT_ANDROID_KEY_ALIAS } else { 'xpoint-upload' }),
    [string]$RuntimeEnvironmentPath = (Join-Path $PSScriptRoot 'survival.dev.env'),
    [string]$ApkSignerPath = $env:DEEP_APKSIGNER,
    [string]$AaptPath,
    [string]$ZipAlignPath,
    [string]$JavaHome = $env:JAVA_HOME,
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$devops = [IO.Path]::GetFullPath($DevOpsRoot)
$mailboxSecrets = [IO.Path]::GetFullPath($MailboxSecretRoot)
$tlsSecrets = [IO.Path]::GetFullPath($TlsSecretRoot)
$uatArtifacts = Join-Path $devops 'artifacts\survival-dev\production-mailbox-uat'
$xnodeSecrets = Join-Path $devops '.secrets\survival-dev'
$xnode = [IO.Path]::GetFullPath((Join-Path $devops '..\xnode'))
$authorityState = Join-Path $xnodeSecrets 'mailbox-authority-state.v1.json'
$project = Join-Path $repo 'src\Deep.Client.Maui\Deep.Client.Maui.csproj'
$transparencyTool = Join-Path $PSScriptRoot 'tools\Deep.AndroidTransparency.Tool\Deep.AndroidTransparency.Tool.csproj'
$uatTransparencySigner = Join-Path $PSScriptRoot 'tools\Deep.AndroidTransparency.UatSigner\Deep.AndroidTransparency.UatSigner.csproj'
$bootstrap = Join-Path $devops 'scripts\Initialize-SurvivalUatProductionMailbox.ps1'
$launcher = Join-Path $PSScriptRoot 'Invoke-SurvivalDevClient.ps1'
$applicationId = 'network.xpoint.deep.e2e'

function Resolve-ExactFile([string]$Path, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw "$Label path is required." }
    $full = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "$Label is unavailable." }
    for ($cursor = $full; -not [string]::IsNullOrWhiteSpace($cursor); $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if (((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Label path traverses a reparse point."
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -ceq $cursor) { break }
    }
    return (Resolve-Path -LiteralPath $full).ProviderPath
}

function Resolve-AndroidTool([string]$Explicit, [string]$Name) {
    if (-not [string]::IsNullOrWhiteSpace($Explicit) -and (Test-Path -LiteralPath $Explicit -PathType Leaf)) {
        return (Resolve-Path -LiteralPath $Explicit).ProviderPath
    }
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -ne $command) { return $command.Source }
    $leaf = switch ($Name) {
        'apksigner' { 'apksigner.bat' }
        'zipalign' { 'zipalign.exe' }
        default { 'aapt.exe' }
    }
    $sdkRoots = @("$env:LOCALAPPDATA\Android\Sdk\build-tools",
        'C:\Program Files (x86)\Android\android-sdk\build-tools')
    $candidate = Get-ChildItem $sdkRoots -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName $leaf } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $candidate) { throw "$Name is unavailable." }
    return $candidate
}

function Invoke-Checked([string]$File, [object[]]$Arguments, [string]$Label) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Label failed with exit code $LASTEXITCODE." }
}

function Restart-ProductionLikeUat {
    $savedEnvironment = @{}
    foreach ($name in @('SURVIVAL_BIND_HOST', 'SURVIVAL_UAT_PUBLIC_HOST',
            'SURVIVAL_UAT_TLS_SECRET_DIR', 'SURVIVAL_UAT_MAILBOX_SECRET_DIR',
            'SURVIVAL_UAT_MAILBOX_ARTIFACT_DIR')) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
    }
    try {
        $env:SURVIVAL_BIND_HOST = $LanHost
        $env:SURVIVAL_UAT_PUBLIC_HOST = $PublicHost
        $env:SURVIVAL_UAT_TLS_SECRET_DIR = $tlsSecrets
        $env:SURVIVAL_UAT_MAILBOX_SECRET_DIR = $mailboxSecrets
        $env:SURVIVAL_UAT_MAILBOX_ARTIFACT_DIR = $uatArtifacts
        $compose = @('compose', '-p', 'deep-survival-dev',
            '-f', (Join-Path $devops 'docker-compose.survival.dev.yml'),
            '-f', (Join-Path $devops 'docker-compose.survival-uat-tls.dev.yml'),
            '-f', (Join-Path $devops 'docker-compose.production-mailbox-uat.dev.yml'))
        $stateInit = 'survival-uat-production-mailbox-state-init'
        Invoke-Checked docker ($compose + @('up', '--force-recreate', '--no-deps',
            '--abort-on-container-exit', '--exit-code-from', $stateInit, $stateInit)) `
            'production-like UAT monotonic state transition'
        $uatServices = @('xnode-1', 'xnode-2', 'xnode-3', 'xnode-4', 'xnode-5',
            'xnode-6', 'registry', 'survival-uat-tls-ingress')
        Invoke-Checked docker ($compose + @('up', '-d', '--force-recreate', '--no-deps') +
            $uatServices) 'production-like UAT service recreation'
        Invoke-Checked docker ($compose + @('up', '-d', '--no-deps', '--wait',
            '--wait-timeout', '240') + $uatServices) 'production-like UAT readiness'
    } finally {
        foreach ($entry in $savedEnvironment.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value)
        }
    }
}

if (-not (Test-Path -LiteralPath $uatArtifacts -PathType Container)) {
    throw 'Existing production-like UAT artifacts are required as the predecessor.'
}
foreach ($path in @($authorityState, (Join-Path $uatArtifacts 'trust-floor.json'),
        (Join-Path $uatArtifacts 'authority.pma1'), (Join-Path $mailboxSecrets 'mrx.seed'))) {
    [void](Resolve-ExactFile $path 'UAT predecessor input')
}
$keystoreFile = Resolve-ExactFile $Keystore 'Android keystore'
$password = Resolve-ExactFile $PasswordFile 'Android signing password file'
$runtime = Resolve-ExactFile $RuntimeEnvironmentPath 'physical runtime environment'
if ($Alias -cnotmatch '^[A-Za-z0-9_.-]{1,128}$') { throw 'Android signing alias is non-canonical.' }

if ([string]::IsNullOrWhiteSpace($JavaHome)) {
    $JavaHome = @('C:\Program Files\Android\openjdk\jdk-21.0.8',
        'C:\Program Files\Microsoft\jdk-21.0.8.9-hotspot') |
        Where-Object { Test-Path -LiteralPath (Join-Path $_ 'bin\java.exe') -PathType Leaf } |
        Select-Object -First 1
}
$keytool = Resolve-ExactFile (Join-Path $JavaHome 'bin\keytool.exe') 'keytool'
$apkSigner = Resolve-AndroidTool $ApkSignerPath 'apksigner'
$aapt = Resolve-AndroidTool $AaptPath 'aapt'
$zipAlign = Resolve-AndroidTool $ZipAlignPath 'zipalign'

$projectXml = [xml](Get-Content -Raw -LiteralPath $project)
$versionCode = [string]$projectXml.SelectSingleNode('/Project/PropertyGroup/ApplicationVersion').InnerText
if ($versionCode -cnotmatch '^[1-9][0-9]*$') { throw 'Android versionCode is invalid.' }

$keytoolOutput = @(& $keytool -list -v -keystore $keystoreFile -storetype PKCS12 -alias $Alias `
    '-storepass:file' $password 2>&1)
if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect the Android signing certificate.' }
$signerMatch = [regex]::Match(($keytoolOutput -join "`n"), 'SHA256:\s*([0-9A-F:]{95})')
if (-not $signerMatch.Success) { throw 'Android signing certificate SHA-256 was not returned.' }
$signerLineage = $signerMatch.Groups[1].Value.Replace(':', '').ToLowerInvariant()
$keytoolOutput = $null

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repo ('artifacts\physical-uat-android\' +
        [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'))
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    if (@(Get-ChildItem -Force -LiteralPath $output).Count -ne 0) {
        throw 'Physical UAT output directory must be new or empty.'
    }
} else { [void][IO.Directory]::CreateDirectory($output) }

$passwordLeaseDirectory = Join-Path ([IO.Path]::GetTempPath()) `
    ("deep-android-signing-{0}" -f [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($passwordLeaseDirectory)
$currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
& icacls.exe $passwordLeaseDirectory '/inheritance:r' '/grant:r' `
    "*$($currentSid):(OI)(CI)F" '*S-1-5-18:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Unable to protect the temporary Android signing source.' }
$passwordLease = Join-Path $passwordLeaseDirectory 'password-source'
try {
Invoke-Checked dotnet @('run', '--project', $transparencyTool, '-c', 'Release', '--',
    'duplicate-password-source', '--source', $password, '--output', $passwordLease) `
    'temporary Android signing source preparation'

$previousTrustPath = Join-Path $output 'predecessor-trust-floor.json'
$previousAuthorityPath = Join-Path $output 'predecessor-authority.pma1'
Copy-Item -LiteralPath (Join-Path $uatArtifacts 'trust-floor.json') -Destination $previousTrustPath
Copy-Item -LiteralPath (Join-Path $uatArtifacts 'authority.pma1') -Destination $previousAuthorityPath
. (Join-Path $PSScriptRoot 'Read-ProductionTrustBundle.ps1')
$previousTrust = Import-ProductionTrustBundle -Path $previousTrustPath

$routes = Join-Path $output 'privacy-routes'
[void][IO.Directory]::CreateDirectory($routes)
Invoke-Checked dotnet @(
    'run', '--project', (Join-Path $devops 'tools\survival-mailbox-driver\SurvivalMailboxDriver.csproj'),
    '--configuration', 'Release', "-p:XNodeSource=$xnode", '--',
    'publish-production-uat-routes', '--secrets-dir', $xnodeSecrets,
    '--private-dir', $mailboxSecrets, '--output-dir', $routes,
    '--authority-state', $authorityState, '--public-host', $PublicHost) 'UAT privacy-route publication'
$routesJson = Resolve-ExactFile (Join-Path $routes 'production-mailbox-privacy-routes.v1.json') 'UAT privacy-route JSON'
$routesSignature = Resolve-ExactFile (Join-Path $routes 'production-mailbox-privacy-routes.v1.sig') 'UAT privacy-route signature'
$routesPublicKey = Resolve-ExactFile (Join-Path $routes 'production-mailbox-privacy-routes.v1.pub') 'UAT privacy-route public key'
$mrXPublicKeySha256 = (Get-FileHash -LiteralPath $routesPublicKey -Algorithm SHA256).Hash.ToLowerInvariant()
if ($mrXPublicKeySha256 -cne [string]$previousTrust.DeepProductionMrXPublicKeySha256) {
    throw 'UAT privacy-route Mr. X root differs from the predecessor trust floor.'
}

$uatProperties = @(
    "-p:DeepPhysicalUatMrXPublicKeySha256=$($previousTrust.DeepProductionMrXPublicKeySha256)",
    "-p:DeepPhysicalUatNetworkId=$($previousTrust.DeepProductionNetworkId)",
    "-p:DeepPhysicalUatAuthorityGeneration=$($previousTrust.DeepProductionAuthorityGeneration)",
    "-p:DeepPhysicalUatAuthorityHash=$($previousTrust.DeepProductionAuthorityHash)",
    "-p:DeepPhysicalUatRevocationGeneration=$($previousTrust.DeepProductionRevocationGeneration)",
    "-p:DeepPhysicalUatRevocationHeadHash=$($previousTrust.DeepProductionRevocationHeadHash)",
    "-p:DeepPhysicalUatRevocationSnapshotHash=$($previousTrust.DeepProductionRevocationSnapshotHash)",
    "-p:DeepPhysicalUatTopologyGeneration=$($previousTrust.DeepProductionTopologyGeneration)",
    "-p:DeepPhysicalUatTopologyHash=$($previousTrust.DeepProductionTopologyHash)",
    "-p:DeepPhysicalUatAndroidApplicationId=$applicationId",
    "-p:DeepPhysicalUatAndroidVersionCode=$versionCode",
    "-p:DeepPhysicalUatAndroidSignerLineageSha256=$signerLineage",
    "-p:DeepPhysicalUatPrivacyRoutesJson=$routesJson",
    "-p:DeepPhysicalUatPrivacyRoutesSignature=$routesSignature",
    "-p:DeepPhysicalUatPrivacyRoutesPublicKey=$routesPublicKey")
$signingProperties = @(
    '-p:AndroidKeyStore=true', "-p:AndroidSigningKeyStore=$keystoreFile",
    "-p:AndroidSigningKeyAlias=$Alias", "-p:AndroidSigningKeyPass=file:$passwordLease",
    "-p:AndroidSigningStorePass=file:$passwordLease")
Invoke-Checked dotnet (@(
    'build', $project, '-f', 'net10.0-android', '-c', 'Debug',
    '-p:DeepPhysicalE2E=true', "-p:DeepSurvivalRuntimeEnv=$runtime",
    "-p:DeepMrXPublicKeySha256=$mrXPublicKeySha256",
    '-p:DeepPhysicalUatAndroidTransparencyPreparation=true', '-nodeReuse:false',
    '-m:1', '-p:BuildInParallel=false', '-p:UseSharedCompilation=false',
    '-p:Aapt2DaemonMaxInstanceCount=1', "/bl:$output\candidate-build.binlog") +
    $uatProperties + $signingProperties) 'physical UAT transparency candidate build'

$builtApk = Resolve-ExactFile (Join-Path $repo "src\Deep.Client.Maui\bin\Debug\net10.0-android\$applicationId-Signed.apk") 'candidate APK'
$candidateApk = Join-Path $output 'candidate-without-act1.apk'
$previousJavaHome = $env:JAVA_HOME
try {
    $env:JAVA_HOME = $JavaHome
    Invoke-Checked $apkSigner @('sign', '--out', $candidateApk,
        '--ks', $keystoreFile, '--ks-type', 'PKCS12', '--ks-key-alias', $Alias,
        '--ks-pass', "file:$passwordLease", '--key-pass', "file:$passwordLease",
        $builtApk) 'explicit candidate APK signing'
} finally { $env:JAVA_HOME = $previousJavaHome }
$badgingOutput = @(& $aapt dump badging $candidateApk)
$candidateBadgingExitCode = $LASTEXITCODE
$badging = $badgingOutput | Select-Object -First 1
if ($candidateBadgingExitCode -ne 0 -or
    $badging -notmatch "name='$([regex]::Escape($applicationId))'" -or
    $badging -notmatch "versionCode='$versionCode'") { throw 'Candidate APK package/version is invalid.' }
$previousJavaHome = $env:JAVA_HOME
try {
    $env:JAVA_HOME = $JavaHome
    $signerOutput = @(& $apkSigner verify --print-certs $candidateApk 2>&1)
    $candidateVerifyExitCode = $LASTEXITCODE
} finally { $env:JAVA_HOME = $previousJavaHome }
$apkSignerMatch = [regex]::Match(($signerOutput -join "`n"),
    '^Signer #1 certificate SHA-256 digest: ([0-9a-f]{64})$',
    [Text.RegularExpressions.RegexOptions]::Multiline)
if ($candidateVerifyExitCode -ne 0 -or -not $apkSignerMatch.Success -or
    $apkSignerMatch.Groups[1].Value -cne $signerLineage) {
    throw 'Candidate APK signer does not match the inspected keystore certificate.'
}

$inventory = Join-Path $output 'candidate-inventory.tsv'
[IO.File]::WriteAllText($inventory, "base`t$candidateApk`n", [Text.UTF8Encoding]::new($false))
$unsignedAct1 = Join-Path $output 'android-code-transparency.unsigned.act1'
$signingBytes = Join-Path $output 'android-code-transparency.signing.bin'
Invoke-Checked dotnet @('run', '--project', $transparencyTool, '-c', 'Release', '--',
    'prepare', '--inventory', $inventory, '--application-id', $applicationId,
    '--version-code', $versionCode, '--signer-lineage', $signerLineage,
    '--mrx-public-key', ([BitConverter]::ToString([IO.File]::ReadAllBytes($routesPublicKey)).
        Replace('-', '').ToLowerInvariant()),
    '--unsigned-manifest', $unsignedAct1, '--signing-bytes', $signingBytes) 'ACT1 preparation'
$act1Signature = Join-Path $output 'android-code-transparency.signature.bin'
$act1PublicKey = Join-Path $output 'android-code-transparency.mrx.pub'
Invoke-Checked dotnet @('run', '--project', $uatTransparencySigner, '-c', 'Release', '--',
    'sign-uat-seed', '--seed', (Join-Path $mailboxSecrets 'mrx.seed'),
    '--signing-bytes', $signingBytes, '--signature', $act1Signature,
    '--public-key', $act1PublicKey) 'UAT ACT1 signing'
if ((Get-FileHash -LiteralPath $act1PublicKey -Algorithm SHA256).Hash.ToLowerInvariant() -cne
    $mrXPublicKeySha256) { throw 'ACT1 signer differs from the UAT Mr. X root.' }
$act1 = Join-Path $output 'android-code-transparency.act1'
Invoke-Checked dotnet @('run', '--project', $transparencyTool, '-c', 'Release', '--',
    'assemble', '--unsigned-manifest', $unsignedAct1, '--signature', $act1Signature,
    '--output', $act1) 'ACT1 finalization'
$act1Sha256 = (Get-FileHash -LiteralPath $act1 -Algorithm SHA256).Hash.ToLowerInvariant()

$clientTrustPath = Join-Path $output 'physical-client-trust-floor.json'
$clientTrust = [ordered]@{
    schemaVersion = 1
    trustFloor = [ordered]@{
        mrXPublicKeySha256 = $previousTrust.DeepProductionMrXPublicKeySha256
        networkId = $previousTrust.DeepProductionNetworkId
        authorityGeneration = $previousTrust.DeepProductionAuthorityGeneration
        authorityHash = $previousTrust.DeepProductionAuthorityHash
        revocationGeneration = $previousTrust.DeepProductionRevocationGeneration
        revocationHeadHash = $previousTrust.DeepProductionRevocationHeadHash
        revocationSnapshotHash = $previousTrust.DeepProductionRevocationSnapshotHash
        topologyGeneration = $previousTrust.DeepProductionTopologyGeneration
        topologyHash = $previousTrust.DeepProductionTopologyHash
    }
    android = [ordered]@{
        buildIdSha256 = $act1Sha256
        applicationId = $applicationId
        versionCode = $versionCode
        playAppSigningLineageSha256 = @($signerLineage)
    }
}
[IO.File]::WriteAllText($clientTrustPath, ($clientTrust | ConvertTo-Json -Depth 4) + "`n",
    [Text.UTF8Encoding]::new($false))
$validatedClientTrust = Import-ProductionTrustBundle -Path $clientTrustPath -RequireAndroid `
    -ExpectedAndroidApplicationId $applicationId
if ([string]$validatedClientTrust.DeepProductionAndroidBuildIdSha256 -cne $act1Sha256) {
    throw 'Physical client trust-floor ACT1 binding failed validation.'
}

& $launcher -Target Android -BuildOnly -NoInstall -RuntimeEnvironmentPath $runtime `
    -MrXPublicKeySha256 $mrXPublicKeySha256 -PhysicalUatTrustFloorBundle $clientTrustPath `
    -PhysicalUatPrivacyRoutesJson $routesJson -PhysicalUatPrivacyRoutesSignature $routesSignature `
    -PhysicalUatPrivacyRoutesPublicKey $routesPublicKey `
    -PhysicalUatAndroidTransparencyManifest $act1 -AndroidKeystore $keystoreFile `
    -AndroidSigningPasswordFile $passwordLease -AndroidSigningKeyAlias $Alias `
    -ApkSignerPath $apkSigner -JavaHome $JavaHome | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Final physical UAT Android build failed.' }

$finalBuiltApk = Resolve-ExactFile (Join-Path $repo "src\Deep.Client.Maui\bin\Debug\net10.0-android\$applicationId-Signed.apk") 'final physical UAT APK'
$finalApk = Join-Path $output 'Deep-physical-uat-android.apk'
$previousJavaHome = $env:JAVA_HOME
try {
    $env:JAVA_HOME = $JavaHome
    Invoke-Checked $apkSigner @('sign', '--out', $finalApk,
        '--ks', $keystoreFile, '--ks-type', 'PKCS12', '--ks-key-alias', $Alias,
        '--ks-pass', "file:$passwordLease", '--key-pass', "file:$passwordLease",
        $finalBuiltApk) 'explicit final APK signing'
    $finalSignerOutput = @(& $apkSigner verify --print-certs $finalApk 2>&1)
    $finalVerifyExitCode = $LASTEXITCODE
} finally { $env:JAVA_HOME = $previousJavaHome }
$finalSignerMatch = [regex]::Match(($finalSignerOutput -join "`n"),
    '^Signer #1 certificate SHA-256 digest: ([0-9a-f]{64})$',
    [Text.RegularExpressions.RegexOptions]::Multiline)
if ($finalVerifyExitCode -ne 0 -or -not $finalSignerMatch.Success -or
    $finalSignerMatch.Groups[1].Value -cne $signerLineage) {
    throw 'Final APK signer does not match the UAT trust-floor lineage.'
}
$finalInventory = Join-Path $output 'final-inventory.tsv'
[IO.File]::WriteAllText($finalInventory, "base`t$finalApk`n", [Text.UTF8Encoding]::new($false))
$finalVerifyArguments = @('run', '--project', $transparencyTool, '-c', 'Release', '--',
    'verify', '--manifest', $act1, '--expected-manifest-sha256', $act1Sha256,
    '--expected-mrx-sha256', $mrXPublicKeySha256, '--application-id', $applicationId,
    '--version-code', $versionCode, '--signer-lineage', $signerLineage,
    '--inventory', $finalInventory)
& dotnet @finalVerifyArguments
$finalSemanticExitCode = $LASTEXITCODE
if ($finalSemanticExitCode -ne 0) {
    $restartInventory = Join-Path $output 'restart-candidate-inventory.tsv'
    [IO.File]::WriteAllText($restartInventory, "base`t$finalApk`n",
        [Text.UTF8Encoding]::new($false))
    $restartUnsignedAct1 = Join-Path $output 'android-code-transparency.restart.unsigned.act1'
    $restartSigningBytes = Join-Path $output 'android-code-transparency.restart.signing.bin'
    Invoke-Checked dotnet @('run', '--project', $transparencyTool, '-c', 'Release', '--',
        'prepare', '--inventory', $restartInventory, '--application-id', $applicationId,
        '--version-code', $versionCode, '--signer-lineage', $signerLineage,
        '--mrx-public-key', ([BitConverter]::ToString([IO.File]::ReadAllBytes($routesPublicKey)).
            Replace('-', '').ToLowerInvariant()), '--unsigned-manifest', $restartUnsignedAct1,
        '--signing-bytes', $restartSigningBytes) 'restart ACT1 preparation'
    $restartSignature = Join-Path $output 'android-code-transparency.restart.signature.bin'
    $restartPublicKey = Join-Path $output 'android-code-transparency.restart.mrx.pub'
    Invoke-Checked dotnet @('run', '--project', $uatTransparencySigner, '-c', 'Release', '--',
        'sign-uat-seed', '--seed', (Join-Path $mailboxSecrets 'mrx.seed'),
        '--signing-bytes', $restartSigningBytes, '--signature', $restartSignature,
        '--public-key', $restartPublicKey) 'restart UAT ACT1 signing'
    if ((Get-FileHash -LiteralPath $restartPublicKey -Algorithm SHA256).Hash.ToLowerInvariant() `
            -cne $mrXPublicKeySha256) { throw 'Restart ACT1 signer differs from UAT Mr. X.' }
    $restartAct1 = Join-Path $output 'android-code-transparency.restart.act1'
    Invoke-Checked dotnet @('run', '--project', $transparencyTool, '-c', 'Release', '--',
        'assemble', '--unsigned-manifest', $restartUnsignedAct1,
        '--signature', $restartSignature, '--output', $restartAct1) `
        'restart ACT1 finalization'
    $restartAct1Sha256 = (Get-FileHash -LiteralPath $restartAct1 -Algorithm SHA256).
        Hash.ToLowerInvariant()

    $clientTrust['android']['buildIdSha256'] = $restartAct1Sha256
    $restartClientTrustPath = Join-Path $output 'physical-client-trust-floor.restart.json'
    [IO.File]::WriteAllText($restartClientTrustPath,
        ($clientTrust | ConvertTo-Json -Depth 4) + "`n", [Text.UTF8Encoding]::new($false))
    $validatedRestartClient = Import-ProductionTrustBundle -Path $restartClientTrustPath `
        -RequireAndroid -ExpectedAndroidApplicationId $applicationId
    if ([string]$validatedRestartClient.DeepProductionAndroidBuildIdSha256 -cne
            $restartAct1Sha256) { throw 'Restart client trust-floor ACT1 binding failed.' }
    $clientTrustPath = $restartClientTrustPath

    $mismatchCandidate = Join-Path $output 'semantic-mismatch-candidate.apk'
    Move-Item -LiteralPath $finalApk -Destination $mismatchCandidate
    $postProcessUnaligned = Join-Path $output 'restart-postprocess-unaligned.apk'
    $postProcessAligned = Join-Path $output 'restart-postprocess-aligned.apk'
    Invoke-Checked dotnet @('run', '--project', $transparencyTool, '-c', 'Release', '--',
        'replace-act1', '--apk', $mismatchCandidate, '--manifest', $restartAct1,
        '--output', $postProcessUnaligned) 'exact restart ACT1 replacement'
    Invoke-Checked $zipAlign @('-f', '-p', '4', $postProcessUnaligned,
        $postProcessAligned) 'restart APK zipalign'
    Invoke-Checked $zipAlign @('-c', '-p', '4', $postProcessAligned) `
        'restart APK alignment verification'
    $previousJavaHome = $env:JAVA_HOME
    try {
        $env:JAVA_HOME = $JavaHome
        Invoke-Checked $apkSigner @('sign', '--out', $finalApk,
            '--ks', $keystoreFile, '--ks-type', 'PKCS12', '--ks-key-alias', $Alias,
            '--ks-pass', "file:$passwordLease", '--key-pass', "file:$passwordLease",
            $postProcessAligned) 'explicit restart APK signing'
        $restartSignerOutput = @(& $apkSigner verify --print-certs $finalApk 2>&1)
        $restartSignerExitCode = $LASTEXITCODE
    } finally { $env:JAVA_HOME = $previousJavaHome }
    $restartSignerMatch = [regex]::Match(($restartSignerOutput -join "`n"),
        '^Signer #1 certificate SHA-256 digest: ([0-9a-f]{64})$',
        [Text.RegularExpressions.RegexOptions]::Multiline)
    if ($restartSignerExitCode -ne 0 -or -not $restartSignerMatch.Success -or
        $restartSignerMatch.Groups[1].Value -cne $signerLineage) {
        throw 'Restart APK signer does not match the UAT trust-floor lineage.'
    }
    $act1 = $restartAct1
    $act1Sha256 = $restartAct1Sha256
    [IO.File]::WriteAllText($finalInventory, "base`t$finalApk`n",
        [Text.UTF8Encoding]::new($false))
    Invoke-Checked dotnet @('run', '--project', $transparencyTool, '-c', 'Release', '--',
        'verify', '--manifest', $act1, '--expected-manifest-sha256', $act1Sha256,
        '--expected-mrx-sha256', $mrXPublicKeySha256, '--application-id', $applicationId,
        '--version-code', $versionCode, '--signer-lineage', $signerLineage,
        '--inventory', $finalInventory) 'restart final physical UAT ACT1 verification'
}
$predecessorGeneration = [uint64]$previousTrust.DeepProductionAuthorityGeneration
if ($predecessorGeneration -eq [uint64]::MaxValue) {
    throw 'Physical UAT predecessor generation cannot advance.'
}
$expectedServerGeneration = $predecessorGeneration + 1
& $bootstrap -LanHost $LanHost -PublicHost $PublicHost -TlsSecretRoot $tlsSecrets `
    -MailboxSecretRoot $mailboxSecrets -AndroidSigningCertificateSha256 $signerLineage `
    -AndroidBuildArtifactSha256 $act1Sha256 -AndroidApplicationId $applicationId `
    -AndroidVersionCode $versionCode -AndroidSignerLineageSha256 $signerLineage `
    -PreviousTrustFloorBundle $previousTrustPath -PreviousAuthorityArtifact $previousAuthorityPath `
    -XNodeRepository $xnode -OutputDirectory $uatArtifacts | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'UAT successor bootstrap failed.' }
$serverTrustPath = Resolve-ExactFile (Join-Path $uatArtifacts 'trust-floor.json') `
    'successor UAT trust floor'
$validatedServerTrust = Import-ProductionTrustBundle -Path $serverTrustPath -RequireAndroid `
    -ExpectedAndroidApplicationId $applicationId
if ([uint64]$validatedServerTrust.DeepProductionAuthorityGeneration -ne
        $expectedServerGeneration -or
    [string]$validatedServerTrust.DeepProductionAndroidBuildIdSha256 -cne $act1Sha256 -or
    [string]$validatedServerTrust.DeepProductionAndroidVersionCode -cne $versionCode -or
    [string]$validatedServerTrust.DeepProductionAndroidSignerLineageSha256 -cne $signerLineage) {
    throw 'Successor UAT trust floor does not bind one exact generation and ACT1/package/signer tuple.'
}
$serverTrustOutputPath = Join-Path $output 'server-trust-floor.json'
Copy-Item -LiteralPath $serverTrustPath -Destination $serverTrustOutputPath
Restart-ProductionLikeUat

$finalBadgingOutput = @(& $aapt dump badging $finalApk)
$finalBadgingExitCode = $LASTEXITCODE
$finalBadging = $finalBadgingOutput | Select-Object -First 1
if ($finalBadgingExitCode -ne 0 -or
    $finalBadging -notmatch "name='$([regex]::Escape($applicationId))'" -or
    $finalBadging -notmatch "versionCode='$versionCode'") {
    throw 'Final APK package/version is invalid.'
}
$finalApkSha256 = (Get-FileHash -LiteralPath $finalApk -Algorithm SHA256).Hash.ToLowerInvariant()

[pscustomobject]@{
    schema = 'deep-physical-uat-android-build.v1'
    package = $applicationId
    versionCode = $versionCode
    signerLineageSha256 = $signerLineage
    act1 = $act1
    act1Sha256 = $act1Sha256
    apk = $finalApk
    apkSha256 = $finalApkSha256
    clientTrustFloor = $clientTrustPath
    serverTrustFloor = $serverTrustOutputPath
    installed = $false
} | Format-List
} finally {
    if (Test-Path -LiteralPath $passwordLease -PathType Leaf) {
        & dotnet run --project $transparencyTool -c Release -- `
            dispose-password-source --path $passwordLease
        if ($LASTEXITCODE -ne 0) { throw 'Temporary Android signing source cleanup failed.' }
    } elseif (Test-Path -LiteralPath $passwordLeaseDirectory -PathType Container) {
        throw 'Temporary Android signing source directory remains without its exact lease.'
    }
}
