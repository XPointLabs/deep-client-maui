Set-StrictMode -Version Latest

function Import-ProductionTrustBundle {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [switch]$RequireAndroid
    )

    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "Production trust-floor bundle '$resolved' does not exist."
    }
    function Assert-NoTrustBundleReparsePoint([string]$candidate) {
        $cursor = Get-Item -LiteralPath $candidate -Force
        while ($null -ne $cursor) {
            if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Production trust-floor bundle path contains a reparse point.'
            }
            $cursor = if ($cursor -is [IO.FileInfo]) {
                $cursor.Directory
            }
            elseif ($cursor -is [IO.DirectoryInfo]) {
                $cursor.Parent
            }
            else {
                $null
            }
        }
    }
    Assert-NoTrustBundleReparsePoint $resolved
    $stream = [IO.File]::Open(
        $resolved,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::None)
    $bytes = $null
    try {
        if ($stream.Length -le 0 -or $stream.Length -gt 65536) {
            throw 'Production trust-floor bundle is empty or oversized.'
        }
        $bytes = [byte[]]::new([int]$stream.Length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            if ($read -le 0) { throw 'Production trust-floor bundle ended unexpectedly.' }
            $offset += $read
        }
        if ($stream.Length -ne $bytes.Length) {
            throw 'Production trust-floor bundle changed during read.'
        }
        Assert-NoTrustBundleReparsePoint $resolved
        $utf8 = [Text.UTF8Encoding]::new($false, $true)
        $json = $utf8.GetString($bytes)
        if ($json.Contains('\')) {
            throw 'Production trust-floor bundle contains non-canonical JSON escapes.'
        }
        $document = $json | ConvertFrom-Json
    }
    catch {
        throw "Production trust-floor bundle is not valid strict JSON: $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $bytes) { [Array]::Clear($bytes, 0, $bytes.Length) }
        $stream.Dispose()
    }

    function Assert-ExactProperties([object]$value, [string[]]$expected, [string]$name) {
        if ($null -eq $value) { throw "Production trust-floor '$name' object is missing." }
        $actual = @($value.PSObject.Properties.Name | Sort-Object)
        $wanted = @($expected | Sort-Object)
        if ($actual.Count -ne $wanted.Count -or
            @(Compare-Object $actual $wanted -SyncWindow 0).Count -ne 0) {
            throw "Production trust-floor '$name' object has missing or unknown properties."
        }
    }
    Assert-ExactProperties $document @('schemaVersion', 'trustFloor', 'android') 'root'
    Assert-ExactProperties $document.trustFloor @(
        'mrXPublicKeySha256', 'networkId', 'authorityGeneration', 'authorityHash',
        'revocationGeneration', 'revocationHeadHash', 'revocationSnapshotHash',
        'topologyGeneration', 'topologyHash') 'trustFloor'
    if ($null -ne $document.android) {
        Assert-ExactProperties $document.android @(
            'buildIdSha256', 'applicationId', 'versionCode',
            'playAppSigningLineageSha256') 'android'
    }
    if ($null -eq $document -or $document.schemaVersion -ne 1 -or
        $null -eq $document.trustFloor) {
        throw 'Production trust-floor bundle schema is invalid.'
    }
    # ConvertFrom-Json on Windows PowerShell accepts duplicate names. This closed schema uses
    # only the property names below, each exactly once, so count their raw JSON tokens before
    # accepting the parsed object. Escaped spellings are deliberately non-canonical and rejected.
    $schemaProperties = @(
        'schemaVersion', 'trustFloor', 'android',
        'mrXPublicKeySha256', 'networkId', 'authorityGeneration', 'authorityHash',
        'revocationGeneration', 'revocationHeadHash', 'revocationSnapshotHash',
        'topologyGeneration', 'topologyHash', 'buildIdSha256', 'applicationId',
        'versionCode', 'playAppSigningLineageSha256')
    foreach ($propertyName in $schemaProperties) {
        $expectedCount = if ($propertyName -in @(
            'buildIdSha256', 'applicationId', 'versionCode',
            'playAppSigningLineageSha256') -and $null -eq $document.android) { 0 } else { 1 }
        $pattern = '"' + [regex]::Escape($propertyName) + '"\s*:'
        if ([regex]::Matches($json, $pattern).Count -ne $expectedCount) {
            throw "Production trust-floor property '$propertyName' is missing or duplicated."
        }
    }

    function Require-LowerHex([object]$value, [int]$length, [string]$name) {
        $text = [string]$value
        if ($text -notmatch "^[0-9a-f]{$length}$" -or $text -match '^0+$') {
            throw "Production trust-floor field '$name' is invalid."
        }
        return $text
    }
    function Require-Generation([object]$value, [string]$name) {
        $text = [string]$value
        [UInt64]$parsed = 0
        if ($text -notmatch '^[1-9][0-9]*$' -or
            -not [UInt64]::TryParse($text, [ref]$parsed) -or $parsed -eq 0) {
            throw "Production trust-floor field '$name' is invalid."
        }
        return $text
    }

    $trust = $document.trustFloor
    $result = [ordered]@{
        DeepProductionMrXPublicKeySha256 = Require-LowerHex $trust.mrXPublicKeySha256 64 'mrXPublicKeySha256'
        DeepProductionNetworkId = Require-LowerHex $trust.networkId 32 'networkId'
        DeepProductionAuthorityGeneration = Require-Generation $trust.authorityGeneration 'authorityGeneration'
        DeepProductionAuthorityHash = Require-LowerHex $trust.authorityHash 64 'authorityHash'
        DeepProductionRevocationGeneration = Require-Generation $trust.revocationGeneration 'revocationGeneration'
        DeepProductionRevocationHeadHash = Require-LowerHex $trust.revocationHeadHash 64 'revocationHeadHash'
        DeepProductionRevocationSnapshotHash = Require-LowerHex $trust.revocationSnapshotHash 64 'revocationSnapshotHash'
        DeepProductionTopologyGeneration = Require-Generation $trust.topologyGeneration 'topologyGeneration'
        DeepProductionTopologyHash = Require-LowerHex $trust.topologyHash 64 'topologyHash'
    }

    if ($RequireAndroid) {
        $android = $document.android
        if ($null -eq $android -or
            [string]$android.applicationId -cne 'network.xpoint.deep') {
            throw 'Production Android build identity is missing or has the wrong applicationId.'
        }
        $versionCode = Require-Generation $android.versionCode 'android.versionCode'
        $lineage = @($android.playAppSigningLineageSha256)
        if ($lineage.Count -lt 1 -or $lineage.Count -gt 32) {
            throw 'Production Android Play app-signing lineage count is invalid.'
        }
        $normalizedLineage = @($lineage | ForEach-Object {
            Require-LowerHex $_ 64 'android.playAppSigningLineageSha256'
        })
        if (@($normalizedLineage | Sort-Object -Unique).Count -ne $normalizedLineage.Count) {
            throw 'Production Android Play app-signing lineage contains duplicates.'
        }
        $result.DeepProductionAndroidBuildIdSha256 =
            Require-LowerHex $android.buildIdSha256 64 'android.buildIdSha256'
        $result.DeepProductionAndroidApplicationId = 'network.xpoint.deep'
        $result.DeepProductionAndroidVersionCode = $versionCode
        $result.DeepProductionAndroidSignerLineageSha256 = $normalizedLineage -join '|'
    }
    return $result
}
