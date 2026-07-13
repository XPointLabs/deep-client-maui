param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9.-]{3,50}$')]
    [string]$PackageIdentityName,

    [Parameter(Mandatory = $true)]
    [string]$Publisher,

    [string]$PublisherDisplayName = "XPoint Labs",

    [Parameter(Mandatory = $true)]
    [Guid]$WnsAppId,

    [Parameter(Mandatory = $true)]
    [Guid]$WnsRemoteId,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string]$CertificateThumbprint,

    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier = 'win-x64',

    [switch]$AllowUntrustedSelfSignedCertificate
)

$ErrorActionPreference = 'Stop'
$targetFramework = 'net10.0-windows10.0.19041.0'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\Deep.Client.Maui\Deep.Client.Maui.csproj'))
$template = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\Deep.Client.Maui\Platforms\Windows\Package.appxmanifest.template'))
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts\windows-release'))
$intermediate = Join-Path $releaseRoot "intermediate-$RuntimeIdentifier"
$appxPackageDir = (Join-Path $intermediate 'AppPackages') + [IO.Path]::DirectorySeparatorChar
$packageArchitecture = switch ($RuntimeIdentifier) {
    'win-x64' { 'x64' }
    'win-arm64' { 'arm64' }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

function ConvertTo-XmlAttributeValue([string]$value) {
    return [Security.SecurityElement]::Escape($value)
}

function Resolve-WindowsSdkTool([string]$name) {
    $tool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter $name -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($null -eq $tool) {
        throw "$name was not found in the Windows SDK."
    }

    return $tool.FullName
}

function Resolve-ProjectTargetName {
    $arguments = @(
        'msbuild', $project,
        '-nologo',
        '-verbosity:quiet',
        '-getProperty:TargetName,TargetFramework',
        "-p:TargetFramework=$targetFramework",
        '-p:Configuration=Release',
        "-p:RuntimeIdentifierOverride=$RuntimeIdentifier",
        '-p:WindowsPackageType=MSIX'
    )
    $propertyOutput = @(& dotnet @arguments)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not resolve the packaged executable name (dotnet msbuild exited with $LASTEXITCODE)."
    }

    try {
        $properties = ($propertyOutput -join [Environment]::NewLine) | ConvertFrom-Json
    }
    catch {
        throw "Could not parse the MSBuild TargetName response: $($_.Exception.Message)"
    }

    $targetName = [string]$properties.Properties.TargetName
    if ([string]::IsNullOrWhiteSpace($targetName) -or
        $targetName -ne [IO.Path]::GetFileName($targetName) -or
        $targetName.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) {
        throw "MSBuild returned an invalid TargetName '$targetName'."
    }

    return $targetName
}

function Get-ManifestTokens([string]$manifestText) {
    return @(
        [regex]::Matches($manifestText, '(?:\$[A-Za-z][A-Za-z0-9_.-]*\$|__[A-Za-z][A-Za-z0-9_]*__)') |
            ForEach-Object { $_.Value } |
            Sort-Object -Unique
    )
}

function Assert-NoUnresolvedManifestTokens([string]$manifestText, [string]$description) {
    $tokens = @(Get-ManifestTokens $manifestText)
    if ($tokens.Count -ne 0) {
        throw "$description contains unresolved manifest token(s): $($tokens -join ', ')."
    }
}

function Read-MsixManifestText([string]$packagePath) {
    $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $entry = $archive.GetEntry('AppxManifest.xml')
        if ($null -eq $entry) {
            throw "Package '$packagePath' does not contain AppxManifest.xml."
        }

        $reader = [IO.StreamReader]::new($entry.Open(), [Text.Encoding]::UTF8)
        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Test-MsixContainsEntry([string]$packagePath, [string]$entryName) {
    $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName.Equals($entryName, [StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }

        return $false
    }
    finally {
        $archive.Dispose()
    }
}

function Remove-SafeDirectory([string]$path, [string]$root) {
    $fullPath = [IO.Path]::GetFullPath($path)
    $fullRoot = [IO.Path]::GetFullPath($root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $rootPrefix = $fullRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove '$fullPath' because it is outside '$fullRoot'."
    }

    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
}

if ($PackageIdentityName.StartsWith('.') -or $PackageIdentityName.EndsWith('.') -or $PackageIdentityName.Contains('..')) {
    throw 'PackageIdentityName contains an invalid dot sequence.'
}

if ($WnsAppId -eq [Guid]::Empty -or $WnsRemoteId -eq [Guid]::Empty) {
    throw 'WNS application and remote identifiers must be non-empty GUIDs.'
}

$certificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Thumbprint -eq $CertificateThumbprint.ToUpperInvariant() } |
    Select-Object -First 1
if ($null -eq $certificate -or -not $certificate.HasPrivateKey) {
    throw 'The Windows package-signing certificate is missing from CurrentUser\My or has no private key.'
}

if ($certificate.NotBefore -gt (Get-Date) -or $certificate.NotAfter -le (Get-Date)) {
    throw 'The Windows package-signing certificate is not currently valid.'
}

if ($certificate.Subject -ne $Publisher) {
    throw "Publisher must exactly match the signing certificate subject '$($certificate.Subject)'."
}

$projectXml = [xml](Get-Content -LiteralPath $project -Raw -Encoding UTF8)
$displayVersion = $projectXml.SelectSingleNode('/Project/PropertyGroup/ApplicationDisplayVersion').InnerText.Trim()
$versionCode = $projectXml.SelectSingleNode('/Project/PropertyGroup/ApplicationVersion').InnerText.Trim()
$displayParts = @($displayVersion.Split('.'))
if ($displayParts.Count -gt 3 -or $displayParts.Count -lt 2 -or
    @($displayParts | Where-Object { $_ -notmatch '^\d+$' }).Count -ne 0 -or
    $versionCode -notmatch '^\d+$') {
    throw 'Application version cannot be converted to a four-part MSIX version.'
}

$packageVersionParts = @($displayParts + @('0', '0', '0'))[0..2] + @($versionCode)
if (@($packageVersionParts | Where-Object { [int64]$_ -gt 65535 }).Count -ne 0) {
    throw 'Every MSIX version component must be between 0 and 65535.'
}
$packageVersion = $packageVersionParts -join '.'

$targetName = Resolve-ProjectTargetName
$executableName = "$targetName.exe"

New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
Remove-SafeDirectory -Path $intermediate -Root $releaseRoot
New-Item -ItemType Directory -Path $intermediate -Force | Out-Null
$manifestPath = Join-Path $intermediate 'Package.appxmanifest'
$windowsEnvironmentPath = Join-Path $intermediate 'deep.windows.release.env'

$manifest = Get-Content -LiteralPath $template -Raw -Encoding UTF8
$manifest = $manifest.Replace('__PACKAGE_NAME__', (ConvertTo-XmlAttributeValue $PackageIdentityName))
$manifest = $manifest.Replace('__PUBLISHER__', (ConvertTo-XmlAttributeValue $Publisher))
$manifest = $manifest.Replace('__PUBLISHER_DISPLAY_NAME__', (ConvertTo-XmlAttributeValue $PublisherDisplayName))
$manifest = $manifest.Replace('__PACKAGE_VERSION__', $packageVersion)
$manifest = $manifest.Replace('__WNS_APP_ID__', $WnsAppId.ToString('D'))
$manifest = $manifest.Replace('__EXECUTABLE_NAME__', (ConvertTo-XmlAttributeValue $executableName))
$allowedBuildTokens = @('$placeholder$', '$targetentrypoint$')
$unexpectedTokens = @(
    Get-ManifestTokens $manifest |
        Where-Object { $_ -notin $allowedBuildTokens }
)
if ($unexpectedTokens.Count -ne 0) {
    throw "The generated package manifest contains unsupported token(s): $($unexpectedTokens -join ', ')."
}
[xml]$manifestDocument = $manifest
[IO.File]::WriteAllText($manifestPath, $manifest, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText(
    $windowsEnvironmentPath,
    "DEEP_WINDOWS_PUSH_REMOTE_ID=$($WnsRemoteId.ToString('D'))`n",
    [Text.UTF8Encoding]::new($false))

$arguments = @(
    'publish', $project,
    '-f', $targetFramework,
    '-c', 'Release',
    '--nologo',
    '-nodeReuse:false',
    "-p:RuntimeIdentifierOverride=$RuntimeIdentifier",
    '-p:WindowsPackageType=MSIX',
    '-p:AppxBundle=Never',
    '-p:UapAppxPackageBuildMode=SideloadOnly',
    '-p:AppxPackageIncludePrivateSymbols=false',
    "-p:AppxPackageDir=$appxPackageDir",
    '-p:AppxPackageSigningEnabled=true',
    "-p:PackageCertificateThumbprint=$($certificate.Thumbprint)",
    "-p:DeepWindowsAppxManifest=$manifestPath",
    "-p:DeepWindowsReleaseEnv=$windowsEnvironmentPath"
)
& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Windows MSIX build failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $appxPackageDir -PathType Container)) {
    throw "Build succeeded but the expected AppPackages directory '$appxPackageDir' was not produced."
}

$payloadDirectories = @(
    Get-ChildItem -LiteralPath $appxPackageDir -Directory |
        Where-Object { $_.Name.EndsWith('_Test', [StringComparison]::OrdinalIgnoreCase) }
)
if ($payloadDirectories.Count -ne 1) {
    throw "Expected exactly one MSIX sideload payload in '$appxPackageDir'; found $($payloadDirectories.Count)."
}
$sourcePayload = $payloadDirectories[0]

$mainPackages = @(
    Get-ChildItem -LiteralPath $sourcePayload.FullName -File |
        Where-Object { $_.Extension -eq '.msix' }
)
if ($mainPackages.Count -ne 1) {
    throw "Expected exactly one application MSIX in '$($sourcePayload.FullName)'; found $($mainPackages.Count)."
}
$package = $mainPackages[0]

foreach ($installScriptName in @('Install.ps1', 'Add-AppDevPackage.ps1')) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourcePayload.FullName $installScriptName) -PathType Leaf)) {
        throw "The sideload payload is missing $installScriptName."
    }
}
if (@(Get-ChildItem -LiteralPath $sourcePayload.FullName -File -Filter '*.cer').Count -eq 0) {
    throw 'The sideload payload does not contain the signing certificate exported by MSBuild.'
}

$packagedManifestText = Read-MsixManifestText $package.FullName
Assert-NoUnresolvedManifestTokens $packagedManifestText 'The packaged AppxManifest.xml'
[xml]$packagedManifest = $packagedManifestText

$namespace = [Xml.XmlNamespaceManager]::new($packagedManifest.NameTable)
$namespace.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$namespace.AddNamespace('com', 'http://schemas.microsoft.com/appx/manifest/com/windows10')
$namespace.AddNamespace('desktop', 'http://schemas.microsoft.com/appx/manifest/desktop/windows10')
$namespace.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')
$identity = $packagedManifest.SelectSingleNode('/f:Package/f:Identity', $namespace)
$application = $packagedManifest.SelectSingleNode('/f:Package/f:Applications/f:Application', $namespace)
$pushClass = $packagedManifest.SelectSingleNode("//com:Class[@Id='$($WnsAppId.ToString('D'))']", $namespace)
$toastActivation = $packagedManifest.SelectSingleNode('//desktop:ToastNotificationActivation', $namespace)
$shareTarget = $packagedManifest.SelectSingleNode("//uap:Extension[@Category='windows.shareTarget']/uap:ShareTarget", $namespace)
if ($null -eq $identity -or $null -eq $application -or
    $identity.Name -ne $PackageIdentityName -or $identity.Publisher -ne $Publisher -or
    $identity.Version -ne $packageVersion -or $null -eq $pushClass -or
    $null -eq $toastActivation -or $null -eq $shareTarget) {
    throw 'Packaged identity, WNS/toast activation, or Windows Share metadata does not match the requested release.'
}

$comServers = @($packagedManifest.SelectNodes('//com:ExeServer', $namespace))
$invalidComServers = @(
    $comServers |
        Where-Object { $_.GetAttribute('Executable') -ne $executableName }
)
if ($application.GetAttribute('Executable') -ne $executableName -or
    $comServers.Count -eq 0 -or $invalidComServers.Count -ne 0) {
    throw "Every application and COM activation entry must use the packaged executable '$executableName'."
}
if (-not (Test-MsixContainsEntry $package.FullName $executableName)) {
    throw "The manifest references '$executableName', but that executable is absent from the application MSIX."
}

$packageDependencies = @($packagedManifest.SelectNodes('/f:Package/f:Dependencies/f:PackageDependency', $namespace))
$windowsAppRuntimeDependencies = @(
    $packageDependencies |
        Where-Object { $_.GetAttribute('Name').StartsWith('Microsoft.WindowsAppRuntime.', [StringComparison]::Ordinal) }
)
if ($windowsAppRuntimeDependencies.Count -eq 0) {
    throw 'The application MSIX does not declare a Microsoft.WindowsAppRuntime framework dependency.'
}

$dependenciesDirectory = Join-Path $sourcePayload.FullName 'Dependencies'
$dependencyPackageFiles = @(
    if (Test-Path -LiteralPath $dependenciesDirectory -PathType Container) {
        Get-ChildItem -LiteralPath $dependenciesDirectory -Recurse -File |
            Where-Object { $_.Extension -in @('.msix', '.appx') }
    }
)
if ($dependencyPackageFiles.Count -eq 0) {
    throw 'The sideload payload contains no dependency packages.'
}

$dependencyPayloads = @(
    foreach ($dependencyPackage in $dependencyPackageFiles) {
        $dependencyManifestText = Read-MsixManifestText $dependencyPackage.FullName
        Assert-NoUnresolvedManifestTokens $dependencyManifestText "Dependency '$($dependencyPackage.Name)' AppxManifest.xml"
        if (-not (Test-MsixContainsEntry $dependencyPackage.FullName 'AppxSignature.p7x')) {
            throw "Dependency package '$($dependencyPackage.FullName)' is unsigned."
        }
        [xml]$dependencyManifest = $dependencyManifestText
        $dependencyNamespace = [Xml.XmlNamespaceManager]::new($dependencyManifest.NameTable)
        $dependencyNamespace.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
        $dependencyIdentity = $dependencyManifest.SelectSingleNode('/f:Package/f:Identity', $dependencyNamespace)
        if ($null -eq $dependencyIdentity) {
            throw "Dependency package '$($dependencyPackage.FullName)' has no package identity."
        }

        [pscustomobject]@{
            File = $dependencyPackage
            Name = $dependencyIdentity.GetAttribute('Name')
            Publisher = $dependencyIdentity.GetAttribute('Publisher')
            Version = [version]$dependencyIdentity.GetAttribute('Version')
            Architecture = $dependencyIdentity.GetAttribute('ProcessorArchitecture')
        }
    }
)

foreach ($dependency in $packageDependencies) {
    $requiredName = $dependency.GetAttribute('Name')
    $requiredPublisher = $dependency.GetAttribute('Publisher')
    $requiredVersion = [version]$dependency.GetAttribute('MinVersion')
    $matchingDependencies = @(
        $dependencyPayloads |
            Where-Object {
                $_.Name -eq $requiredName -and
                $_.Publisher -eq $requiredPublisher -and
                $_.Version -ge $requiredVersion -and
                $_.Architecture -in @($packageArchitecture, 'neutral', '')
            }
    )
    if ($matchingDependencies.Count -eq 0) {
        throw "The sideload payload does not contain a compatible $packageArchitecture package for dependency '$requiredName' >= $requiredVersion."
    }
}

$signTool = Resolve-WindowsSdkTool 'signtool.exe'
$previousErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    $signatureVerificationOutput = @(& $signTool verify /pa /all $package.FullName 2>&1)
    $signatureVerificationExitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousErrorActionPreference
}
if ($signatureVerificationExitCode -ne 0) {
    $authenticode = Get-AuthenticodeSignature -LiteralPath $package.FullName
    $isExpectedUntrustedSelfSignedCertificate =
        $AllowUntrustedSelfSignedCertificate.IsPresent -and
        $certificate.Subject -eq $certificate.Issuer -and
        $authenticode.Status -eq [Management.Automation.SignatureStatus]::UnknownError -and
        $authenticode.SignerCertificate.Thumbprint -eq $certificate.Thumbprint -and
        $authenticode.StatusMessage -match 'root certificate which is not trusted'
    if (-not $isExpectedUntrustedSelfSignedCertificate) {
        throw "MSIX signature verification failed for '$($package.FullName)': $($signatureVerificationOutput -join ' ')"
    }
}

$safeDisplayVersion = $displayVersion -replace '[^0-9A-Za-z._-]', '-'
$payloadName = "Deep-$safeDisplayVersion-v$versionCode-$RuntimeIdentifier-sideload"
$finalPayload = Join-Path $releaseRoot $payloadName
Remove-SafeDirectory -Path $finalPayload -Root $releaseRoot
Copy-Item -LiteralPath $sourcePayload.FullName -Destination $finalPayload -Recurse

$hashManifestPath = Join-Path $finalPayload 'SHA256SUMS.txt'
$hashLines = @(
    Get-ChildItem -LiteralPath $finalPayload -Recurse -File |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = $_.FullName.Substring($finalPayload.Length + 1).Replace('\', '/')
            $fileHash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            "$fileHash  $relativePath"
        }
)
[IO.File]::WriteAllLines($hashManifestPath, $hashLines, [Text.UTF8Encoding]::new($false))

$finalArchive = "$finalPayload.zip"
if (Test-Path -LiteralPath $finalArchive) {
    Remove-Item -LiteralPath $finalArchive -Force
}
[IO.Compression.ZipFile]::CreateFromDirectory(
    $finalPayload,
    $finalArchive,
    [IO.Compression.CompressionLevel]::NoCompression,
    $false)

$archiveHash = (Get-FileHash -LiteralPath $finalArchive -Algorithm SHA256).Hash
Write-Output $finalArchive
Write-Output "SHA256=$archiveHash"
Write-Output "ExpandedPayload=$finalPayload"
