[CmdletBinding()]
param(
    [string]$RuntimeEnvironmentPath,
    [string]$MrXPublicKeySha256 = $env:DEEP_MR_X_PUBLIC_KEY_SHA256,
    [string]$PhysicalUatTrustFloorBundle = $env:DEEP_PHYSICAL_UAT_TRUST_FLOOR_BUNDLE,
    [string]$PhysicalUatPrivacyRoutesJson = $env:DEEP_PHYSICAL_UAT_PRIVACY_ROUTES_JSON,
    [string]$PhysicalUatPrivacyRoutesSignature = $env:DEEP_PHYSICAL_UAT_PRIVACY_ROUTES_SIGNATURE,
    [string]$PhysicalUatPrivacyRoutesPublicKey = $env:DEEP_PHYSICAL_UAT_PRIVACY_ROUTES_PUBLIC_KEY,
    [string]$CertificateThumbprint = $env:DEEP_PHYSICAL_UAT_WINDOWS_CERTIFICATE_THUMBPRINT,
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier = 'win-arm64',
    [ValidateSet('network.xpoint.deep.e2e')]
    [string]$PackageIdentityName = 'network.xpoint.deep.e2e',
    [switch]$NoBuild,
    [switch]$NoInstall,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'Read-ProductionTrustBundle.ps1')
$project = Join-Path $repoRoot 'src\Deep.Client.Maui\Deep.Client.Maui.csproj'
$manifestTemplate = Join-Path $repoRoot 'src\Deep.Client.Maui\Platforms\Windows\Package.appxmanifest.template'
$targetFramework = 'net10.0-windows10.0.19041.0'
$artifactRoot = Join-Path $repoRoot "artifacts\windows-physical-uat\$RuntimeIdentifier"
$intermediate = Join-Path $artifactRoot 'intermediate'
$appPackages = (Join-Path $intermediate 'AppPackages') + [IO.Path]::DirectorySeparatorChar
$manifestPath = Join-Path $intermediate 'Package.appxmanifest'
$approvalPath = Join-Path $artifactRoot 'windows-uat-pma-approval.json'

function Resolve-CanonicalFile {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Label)

    if ([string]::IsNullOrWhiteSpace($Path)) { throw "$Label path is required." }
    $full = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "$Label must be an existing file."
    }
    for ($current = $full; -not [string]::IsNullOrWhiteSpace($current); $current = [IO.Path]::GetDirectoryName($current)) {
        $item = Get-Item -LiteralPath $current -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Label path must not traverse a reparse point."
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -ceq $current) { break }
    }
    return (Resolve-Path -LiteralPath $full).ProviderPath
}

function Remove-SafeDirectory {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Root)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if (-not $fullPath.StartsWith(
            $fullRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to remove a Windows UAT path outside its artifact root.'
    }
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
}

function ConvertTo-LowerHex([byte[]]$Bytes) {
    return ([BitConverter]::ToString($Bytes).Replace('-', '')).ToLowerInvariant()
}

function ConvertTo-XmlAttributeValue([string]$Value) {
    return [Security.SecurityElement]::Escape($Value)
}

if ([string]::IsNullOrWhiteSpace($RuntimeEnvironmentPath)) {
    $RuntimeEnvironmentPath = Join-Path $PSScriptRoot 'survival.dev.env'
}
$runtimeEnvironment = Resolve-CanonicalFile $RuntimeEnvironmentPath 'Runtime environment'
$trustFloor = Resolve-CanonicalFile $PhysicalUatTrustFloorBundle 'Physical UAT trust-floor bundle'
$privacyJson = Resolve-CanonicalFile $PhysicalUatPrivacyRoutesJson 'Physical UAT privacy-route JSON'
$privacySignature = Resolve-CanonicalFile $PhysicalUatPrivacyRoutesSignature 'Physical UAT privacy-route signature'
$privacyPublicKey = Resolve-CanonicalFile $PhysicalUatPrivacyRoutesPublicKey 'Physical UAT privacy-route public key'

if ($MrXPublicKeySha256 -cnotmatch '^[0-9a-f]{64}$') {
    throw 'DEEP_MR_X_PUBLIC_KEY_SHA256 must be exactly 64 lowercase hexadecimal characters.'
}
if ($CertificateThumbprint -cnotmatch '^[0-9A-Fa-f]{40}$') {
    throw 'A canonical Windows UAT package-signing certificate thumbprint is required.'
}
$certificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object Thumbprint -eq $CertificateThumbprint.ToUpperInvariant() |
    Select-Object -First 1
if ($null -eq $certificate -or -not $certificate.HasPrivateKey) {
    throw 'The Windows UAT package-signing certificate is unavailable or has no private key.'
}
if ($certificate.NotBefore -gt (Get-Date) -or $certificate.NotAfter -le (Get-Date)) {
    throw 'The Windows UAT package-signing certificate is not currently valid.'
}
$ekuExtension = @($certificate.Extensions | Where-Object {
    $null -ne $_.Oid -and $_.Oid.Value -eq '2.5.29.37'
})
$codeSigning = @(
    if ($ekuExtension.Count -eq 1) {
        $ekuExtension[0].EnhancedKeyUsages | Where-Object {
            $_.Value -eq '1.3.6.1.5.5.7.3.3'
        }
    }
)
if ($ekuExtension.Count -ne 1 -or $codeSigning.Count -ne 1) {
    throw 'The Windows UAT certificate is not an unambiguous code-signing certificate.'
}
$sha = [Security.Cryptography.SHA256]::Create()
try { $signerSha256 = ConvertTo-LowerHex ($sha.ComputeHash($certificate.RawData)) }
finally { $sha.Dispose() }
$publisher = $certificate.Subject

$uat = Import-ProductionTrustBundle -Path $trustFloor
if ([string]$uat.DeepProductionMrXPublicKeySha256 -cne $MrXPublicKeySha256) {
    throw 'Physical UAT trust floor differs from the build-pinned Mr. X key.'
}
$properties = @(
    '-p:DeepPhysicalE2E=true',
    "-p:DeepSurvivalRuntimeEnv=$runtimeEnvironment",
    "-p:DeepMrXPublicKeySha256=$MrXPublicKeySha256",
    "-p:DeepPhysicalUatMrXPublicKeySha256=$($uat.DeepProductionMrXPublicKeySha256)",
    "-p:DeepPhysicalUatNetworkId=$($uat.DeepProductionNetworkId)",
    "-p:DeepPhysicalUatAuthorityGeneration=$($uat.DeepProductionAuthorityGeneration)",
    "-p:DeepPhysicalUatAuthorityHash=$($uat.DeepProductionAuthorityHash)",
    "-p:DeepPhysicalUatRevocationGeneration=$($uat.DeepProductionRevocationGeneration)",
    "-p:DeepPhysicalUatRevocationHeadHash=$($uat.DeepProductionRevocationHeadHash)",
    "-p:DeepPhysicalUatRevocationSnapshotHash=$($uat.DeepProductionRevocationSnapshotHash)",
    "-p:DeepPhysicalUatTopologyGeneration=$($uat.DeepProductionTopologyGeneration)",
    "-p:DeepPhysicalUatTopologyHash=$($uat.DeepProductionTopologyHash)",
    "-p:DeepPhysicalUatPrivacyRoutesJson=$privacyJson",
    "-p:DeepPhysicalUatPrivacyRoutesSignature=$privacySignature",
    "-p:DeepPhysicalUatPrivacyRoutesPublicKey=$privacyPublicKey",
    "-p:DeepPhysicalUatWindowsPackageName=$PackageIdentityName",
    "-p:DeepPhysicalUatWindowsPublisher=$publisher",
    "-p:DeepPhysicalUatWindowsSigningCertificateSha256=$signerSha256"
)

if (-not $NoBuild) {
    New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
    Remove-SafeDirectory -Path $intermediate -Root $artifactRoot
    New-Item -ItemType Directory -Path $intermediate -Force | Out-Null

    $projectXml = [xml](Get-Content -LiteralPath $project -Raw -Encoding UTF8)
    $displayVersion = $projectXml.SelectSingleNode('/Project/PropertyGroup/ApplicationDisplayVersion').InnerText.Trim()
    $versionCode = $projectXml.SelectSingleNode('/Project/PropertyGroup/ApplicationVersion').InnerText.Trim()
    $parts = @($displayVersion.Split('.'))
    if ($parts.Count -notin @(2, 3) -or @($parts | Where-Object { $_ -notmatch '^\d+$' }).Count -ne 0 -or
        $versionCode -notmatch '^\d+$') {
        throw 'Application version cannot be converted to a Windows UAT MSIX version.'
    }
    $packageVersion = (@($parts + @('0', '0'))[0..2] + @($versionCode)) -join '.'
    $targetNameOutput = @(& dotnet msbuild $project -nologo -verbosity:quiet `
        -getProperty:TargetName,TargetFramework -p:TargetFramework=$targetFramework -p:Configuration=Debug `
        -p:RuntimeIdentifierOverride=$RuntimeIdentifier -p:WindowsPackageType=MSIX @properties)
    if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the Windows UAT executable name.' }
    $targetNameDocument = ($targetNameOutput -join [Environment]::NewLine) | ConvertFrom-Json
    $targetName = [string]$targetNameDocument.Properties.TargetName
    if ([string]::IsNullOrWhiteSpace($targetName) -or $targetName -ne [IO.Path]::GetFileName($targetName)) {
        throw 'Windows UAT TargetName is invalid.'
    }
    $executable = "$targetName.exe"
    if ($executable -cne 'Deep.Client.Maui.exe') {
        throw 'Windows UAT executable differs from the production protocol application identity.'
    }

    $manifest = Get-Content -LiteralPath $manifestTemplate -Raw -Encoding UTF8
    $manifest = $manifest.Replace('__PACKAGE_NAME__', (ConvertTo-XmlAttributeValue $PackageIdentityName))
    $manifest = $manifest.Replace('__PUBLISHER__', (ConvertTo-XmlAttributeValue $publisher))
    $manifest = $manifest.Replace('__PUBLISHER_DISPLAY_NAME__', 'XPoint Labs Development')
    $manifest = $manifest.Replace('__PACKAGE_VERSION__', $packageVersion)
    $manifest = $manifest.Replace('__WNS_APP_ID__', 'aaf87f5e-fac8-46f7-ad1d-d5d67a3e52c6')
    $manifest = $manifest.Replace('__EXECUTABLE_NAME__', $executable)
    $unexpectedTokens = @([regex]::Matches($manifest,
        '(?:\$[A-Za-z][A-Za-z0-9_.-]*\$|__[A-Za-z][A-Za-z0-9_]*__)') |
        ForEach-Object Value | Where-Object { $_ -notin @('$placeholder$', '$targetentrypoint$') })
    if ($unexpectedTokens.Count -ne 0) { throw 'Windows UAT manifest has unresolved identity tokens.' }
    [xml]$null = $manifest
    [IO.File]::WriteAllText($manifestPath, $manifest, [Text.UTF8Encoding]::new($false))

    & dotnet publish $project -f $targetFramework -c Debug --nologo -nodeReuse:false `
        -p:RuntimeIdentifierOverride=$RuntimeIdentifier -p:WindowsPackageType=MSIX `
        -p:AppxBundle=Never -p:UapAppxPackageBuildMode=SideloadOnly `
        -p:AppxPackageIncludePrivateSymbols=false -p:DebugSymbols=false -p:DebugType=None `
        -p:AppxPackageDir=$appPackages `
        -p:AppxPackageSigningEnabled=true -p:PackageCertificateThumbprint=$($certificate.Thumbprint) `
        -p:DeepWindowsAppxManifest=$manifestPath @properties
    if ($LASTEXITCODE -ne 0) { throw 'Windows physical UAT signed MSIX build failed.' }
}

$payloads = @(Get-ChildItem -LiteralPath $appPackages -Directory -ErrorAction Stop |
    Where-Object Name -Like '*_Test')
if ($payloads.Count -ne 1) { throw 'Expected exactly one Windows physical UAT sideload payload.' }
$packages = @(Get-ChildItem -LiteralPath $payloads[0].FullName -File -Filter '*.msix')
if ($packages.Count -ne 1) { throw 'Expected exactly one Windows physical UAT application MSIX.' }
$package = $packages[0]
$signature = Get-AuthenticodeSignature -LiteralPath $package.FullName
if ($null -eq $signature.SignerCertificate) {
    throw 'Windows physical UAT MSIX has no readable signing certificate.'
}
$sha = [Security.Cryptography.SHA256]::Create()
try { $packageSigner = ConvertTo-LowerHex ($sha.ComputeHash($signature.SignerCertificate.RawData)) }
finally { $sha.Dispose() }
if ($packageSigner -cne $signerSha256) { throw 'Windows physical UAT MSIX signer differs from its build pin.' }

$installed = $null
if (-not $NoInstall) {
    $productionBefore = @(Get-AppxPackage -Name 'network.xpoint.deep' | Select-Object PackageFullName)
    $packageArchitecture = $RuntimeIdentifier.Substring('win-'.Length)
    $dependencies = @(Get-ChildItem -LiteralPath (Join-Path $payloads[0].FullName 'Dependencies') `
        -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Extension -in @('.msix', '.appx') -and
            $_.Directory.Name -eq $packageArchitecture
        } | Select-Object -ExpandProperty FullName)
    $install = @{ Path = $package.FullName; ForceApplicationShutdown = $true }
    if ($dependencies.Count -ne 0) { $install.DependencyPath = $dependencies }
    Add-AppxPackage @install
    $installedPackages = @(Get-AppxPackage -Name $PackageIdentityName)
    if ($installedPackages.Count -ne 1) { throw 'Installed Windows UAT package identity is ambiguous.' }
    $installed = $installedPackages[0]
    if ($installed.Publisher -cne $publisher) { throw 'Installed Windows UAT publisher differs from the build pin.' }
    $productionAfter = @(Get-AppxPackage -Name 'network.xpoint.deep' | Select-Object PackageFullName)
    if (($productionBefore | ConvertTo-Json -Compress) -cne ($productionAfter | ConvertTo-Json -Compress)) {
        throw 'Production Windows package state changed during UAT installation.'
    }

    & dotnet run --project (Join-Path $PSScriptRoot 'Deep.WindowsUatAttestation.Tool\Deep.WindowsUatAttestation.Tool.csproj') `
        --configuration Release -- `
        --installed-root $installed.InstallLocation `
        --package-name $installed.Name `
        --package-full-name $installed.PackageFullName `
        --package-family-name $installed.PackageFamilyName `
        --publisher $installed.Publisher `
        --signer-sha256 $signerSha256 `
        --output $approvalPath
    if ($LASTEXITCODE -ne 0) { throw 'Installed Windows UAT artifact measurement failed.' }

    if (-not $NoLaunch) {
        Start-Process explorer.exe -ArgumentList "shell:AppsFolder\$($installed.PackageFamilyName)!App"
    }
}

$approval = if (Test-Path -LiteralPath $approvalPath -PathType Leaf) {
    Get-Content -LiteralPath $approvalPath -Raw | ConvertFrom-Json
} else { $null }
[pscustomobject]@{
    Package = $package.FullName
    Installed = $null -ne $installed
    PackageFullName = $(if ($null -ne $installed) { $installed.PackageFullName } else { $null })
    WindowsSigningCertificateSha256 = $signerSha256
    WindowsBuildArtifactSha256 = $(if ($null -ne $approval) { $approval.buildArtifactSha256 } else { $null })
    PmaApprovalTuple = $(if ($null -ne $approval) { $approvalPath } else { $null })
    ProductionPackageUntouched = $true
    UnpackagedCopySupported = $false
} | Format-List | Out-Host
