param(
    [Parameter(Mandatory = $true)][string]$TrustFloorBundle,
    [Parameter(Mandatory = $true)][string]$MrXEd25519PublicKey,
    [Parameter(Mandatory = $true)][string]$PlaySignerLineageSha256,
    [Parameter(Mandatory = $true)][string]$BundletoolJar,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$Keystore = $env:XPOINT_ANDROID_KEYSTORE,
    [string]$PasswordFile = $env:XPOINT_ANDROID_SIGNING_PASSWORD_FILE,
    [string]$Alias = $(if ($env:XPOINT_ANDROID_KEY_ALIAS) { $env:XPOINT_ANDROID_KEY_ALIAS } else { 'xpoint-upload' }),
    [string]$GoogleServicesJson = $env:XPOINT_GOOGLE_SERVICES_JSON,
    [string]$AndroidSdkRoot = $(if ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } elseif ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { "${env:ProgramFiles(x86)}\Android\android-sdk" }),
    [string]$JavaHome = $env:JAVA_HOME
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Read-ProductionTrustBundle.ps1')
. (Join-Path $PSScriptRoot 'Assert-AndroidBundletool.ps1')
$trust = Import-ProductionTrustBundle -Path $TrustFloorBundle
$bundletoolLease = Open-PinnedAndroidBundletool -Path $BundletoolJar
$BundletoolJar = $bundletoolLease.Path
try {
$trustArguments = @($trust.GetEnumerator() | ForEach-Object { "-p:$($_.Key)=$($_.Value)" })

function Read-ExclusiveBoundedUtf8 {
    param([Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][int]$MaximumBytes)
    $resolved = [IO.Path]::GetFullPath($Path)
    $cursor = Get-Item -LiteralPath $resolved -Force
    while ($null -ne $cursor) {
        if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'External Android build input path contains a reparse point.'
        }
        $cursor = if ($cursor -is [IO.FileInfo]) { $cursor.Directory } else { $cursor.Parent }
    }
    $stream = [IO.File]::Open($resolved, [IO.FileMode]::Open,
        [IO.FileAccess]::Read, [IO.FileShare]::None)
    $bytes = $null
    try {
        if ($stream.Length -le 0 -or $stream.Length -gt $MaximumBytes) {
            throw 'External Android build input is empty or oversized.'
        }
        $bytes = [byte[]]::new([int]$stream.Length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            if ($read -le 0) { throw 'External Android build input ended unexpectedly.' }
            $offset += $read
        }
        $cursor = Get-Item -LiteralPath $resolved -Force
        while ($null -ne $cursor) {
            if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'External Android build input path changed to a reparse point.'
            }
            $cursor = if ($cursor -is [IO.FileInfo]) { $cursor.Directory } else { $cursor.Parent }
        }
        return [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
    }
    finally {
        if ($null -ne $bytes) { [Array]::Clear($bytes, 0, $bytes.Length) }
        $stream.Dispose()
    }
}

if ($MrXEd25519PublicKey -notmatch '^[0-9a-f]{64}$' -or
    $PlaySignerLineageSha256 -notmatch '^[0-9a-f]{64}(\|[0-9a-f]{64}){0,31}$') {
    throw 'Mr. X public key or Play app-signing lineage is non-canonical.'
}
$lineage = $PlaySignerLineageSha256.Split('|')
if (@($lineage | Sort-Object -Unique).Count -ne $lineage.Count) {
    throw 'Play app-signing lineage contains duplicates.'
}
$publicKeyBytes = [byte[]]::new(32)
for ($index = 0; $index -lt 32; $index++) {
    $publicKeyBytes[$index] = [Convert]::ToByte($MrXEd25519PublicKey.Substring($index * 2, 2), 16)
}
$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $publicKeyHash = [BitConverter]::ToString(
        $sha256.ComputeHash($publicKeyBytes)).Replace('-', '').ToLowerInvariant()
}
finally { $sha256.Dispose(); [Array]::Clear($publicKeyBytes, 0, $publicKeyBytes.Length) }
if ($publicKeyHash -cne [string]$trust.DeepProductionMrXPublicKeySha256) {
    throw 'Mr. X public key does not match the compiled trust-floor hash.'
}
foreach ($path in @($Keystore, $PasswordFile, $BundletoolJar)) {
    if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Android transparency preparation input '$path' is unavailable."
    }
}

$project = Join-Path $PSScriptRoot '..\src\Deep.Client.Maui\Deep.Client.Maui.csproj'
$projectXml = [xml](Get-Content -LiteralPath $project -Raw)
$applicationId = $projectXml.SelectSingleNode('/Project/PropertyGroup/ApplicationId').InnerText.Trim()
$versionCode = $projectXml.SelectSingleNode('/Project/PropertyGroup/ApplicationVersion').InnerText.Trim()
if ($applicationId -cne 'network.xpoint.deep' -or $versionCode -notmatch '^[1-9][0-9]*$') {
    throw 'Android release package/version metadata is invalid.'
}
if ([string]::IsNullOrWhiteSpace($GoogleServicesJson) -or
    -not (Test-Path -LiteralPath $GoogleServicesJson -PathType Leaf)) {
    throw 'Production Firebase configuration is unavailable.'
}
$GoogleServicesJson = [IO.Path]::GetFullPath($GoogleServicesJson)
$firebase = Read-ExclusiveBoundedUtf8 -Path $GoogleServicesJson -MaximumBytes (1024 * 1024) |
    ConvertFrom-Json
if (@($firebase.client | ForEach-Object {
        $_.client_info.android_client_info.package_name }) -notcontains $applicationId) {
    throw 'Firebase configuration does not contain the release package.'
}
& (Join-Path $PSScriptRoot 'validate-libxray-aar.ps1') `
    -AarPath (Join-Path $PSScriptRoot '..\src\Deep.Client.Maui\Platforms\Android\Jars\libXray.aar')

if ([string]::IsNullOrWhiteSpace($JavaHome)) {
    $javaRoot = Get-ChildItem "$env:ProgramFiles\Android\openjdk" -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Select-Object -First 1
    if ($null -ne $javaRoot) { $JavaHome = $javaRoot.FullName }
}
$java = Join-Path $JavaHome 'bin\java.exe'
if (-not (Test-Path -LiteralPath $java -PathType Leaf)) { throw 'JAVA_HOME is invalid.' }
$aapt = Get-ChildItem (Join-Path $AndroidSdkRoot 'build-tools') -Recurse -Filter 'aapt.exe' |
    Sort-Object FullName -Descending | Select-Object -First 1
if ($null -eq $aapt) { throw 'Android SDK aapt.exe is unavailable.' }

$temp = Join-Path ([IO.Path]::GetTempPath()) ('deep-act1-prepare-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    dotnet publish $project -f net10.0-android -c Release `
        -p:AndroidPackageFormats=aab `
        -p:AndroidKeyStore=true `
        -p:AndroidSigningKeyStore="$Keystore" `
        -p:AndroidSigningKeyAlias="$Alias" `
        -p:AndroidSigningStorePass="file:$PasswordFile" `
        -p:DeepProductionAndroidTransparencyPreparation=true `
        "-p:DeepProductionAndroidApplicationId=$applicationId" `
        "-p:DeepProductionAndroidVersionCode=$versionCode" `
        "-p:DeepProductionAndroidSignerLineageSha256=$PlaySignerLineageSha256" `
        "-p:DeepGoogleServicesJson=$GoogleServicesJson" `
        @trustArguments -nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw 'Android transparency candidate build failed.' }
    $bundle = Get-ChildItem (Join-Path $PSScriptRoot '..\src\Deep.Client.Maui\bin\Release\net10.0-android') `
        -Recurse -Filter 'network.xpoint.deep-Signed.aab' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $bundle) { throw 'Android transparency candidate AAB was not produced.' }

    $apks = Join-Path $temp 'candidate.apks'
    & $java -jar $BundletoolJar build-apks "--bundle=$($bundle.FullName)" "--output=$apks" `
        --mode=default "--ks=$Keystore" "--ks-key-alias=$Alias" `
        "--ks-pass=file:$PasswordFile" --overwrite
    if ($LASTEXITCODE -ne 0) { throw 'bundletool candidate expansion failed.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($apks)
    $inventory = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    try {
        if ($archive.Entries.Count -gt 16384) {
            throw 'Candidate APKS archive has too many entries.'
        }
        $entries = @($archive.Entries | Where-Object {
            -not [string]::IsNullOrEmpty($_.Name) -and $_.Name.EndsWith('.apk', [StringComparison]::Ordinal)
        })
        if ($entries.Count -lt 1 -or $entries.Count -gt 4096) {
            throw 'Candidate APKS artifact count is outside strict bounds.'
        }
        $number = 0
        foreach ($entry in $entries) {
            if ($entry.FullName.Contains('\') -or $entry.FullName.StartsWith('/') -or
                @($entry.FullName.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0 -or
                $entry.Length -le 0 -or $entry.Length -gt 512MB -or
                ($entry.Length -gt 4MB -and
                    ($entry.CompressedLength -le 0 -or
                        ($entry.Length / $entry.CompressedLength) -gt 1000))) {
                throw 'Candidate APKS contains an unsafe entry.'
            }
            $number++
            $apk = Join-Path $temp ("split-{0:D4}.apk" -f $number)
            $source = $entry.Open()
            $target = [IO.File]::Open($apk, [IO.FileMode]::CreateNew,
                [IO.FileAccess]::Write, [IO.FileShare]::None)
            try {
                $buffer = [byte[]]::new(131072)
                [long]$written = 0
                while (($read = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $written += $read
                    if ($written -gt $entry.Length) {
                        throw 'Candidate APK expanded past its declaration.'
                    }
                    $target.Write($buffer, 0, $read)
                }
                if ($written -ne $entry.Length) { throw 'Candidate APK is truncated.' }
            }
            finally { $source.Dispose(); $target.Dispose() }
            $badgingOutput = & $aapt.FullName dump badging $apk
            $badging = $badgingOutput | Select-Object -First 1
            if ($LASTEXITCODE -ne 0 -or $badging -notmatch "name='$applicationId'" -or
                $badging -notmatch "versionCode='$versionCode'") {
                throw "Candidate APK tuple is invalid: $badging"
            }
            $match = [regex]::Match($badging, "(?:^|\s)split='([^']+)'(?:\s|$)")
            $identity = if ($match.Success) { $match.Groups[1].Value } else { 'base' }
            if ($identity -notmatch '^[A-Za-z0-9_.-]{1,256}$' -or
                -not $inventory.TryAdd($identity, $apk)) {
                throw "Candidate split identity '$identity' is invalid or duplicated."
            }
        }
    }
    finally { $archive.Dispose() }
    $inventoryPath = Join-Path $temp 'inventory.tsv'
    $inventoryText = (($inventory.GetEnumerator() | Sort-Object Key | ForEach-Object {
        "$($_.Key)`t$($_.Value)" }) -join "`n") + "`n"
    [IO.File]::WriteAllText($inventoryPath, $inventoryText, [Text.UTF8Encoding]::new($false, $true))

    $output = [IO.Path]::GetFullPath($OutputDirectory)
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $unsigned = Join-Path $output 'android-code-transparency.unsigned.act1'
    $signing = Join-Path $output 'android-code-transparency.signing.bin'
    $tool = Join-Path $PSScriptRoot 'tools\Deep.AndroidTransparency.Tool\Deep.AndroidTransparency.Tool.csproj'
    & dotnet run --project $tool -c Release -- prepare `
        --inventory $inventoryPath --application-id $applicationId --version-code $versionCode `
        --signer-lineage $PlaySignerLineageSha256 --mrx-public-key $MrXEd25519PublicKey `
        --unsigned-manifest $unsigned --signing-bytes $signing
    if ($LASTEXITCODE -ne 0) { throw 'ACT1 signing request generation failed.' }
    Write-Output $unsigned
    Write-Output $signing
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
}
finally { Close-PinnedAndroidBundletool -Lease $bundletoolLease }
