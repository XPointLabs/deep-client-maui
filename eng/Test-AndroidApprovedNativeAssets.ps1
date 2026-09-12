[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ApkPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$resolvedApk = (Resolve-Path -LiteralPath $ApkPath).Path
$expected = [ordered]@{
    'assets/deep-native/libdeep_mlkem.so' = [ordered]@{
        bytes = 67576L
        sha256 = '5528f0ff05cbda00bdcd648ef72dcc870c3dde3535aa5f77457b554e93261fa5'
    }
    'assets/deep-native/libdeep_mlkem_braid.so' = [ordered]@{
        bytes = 612376L
        sha256 = 'dd51b21ddd836c84a978616596a749c34bf6258532e2ae2f931f235a124cacff'
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($resolvedApk)
try {
    $nativeEntries = @($archive.Entries | Where-Object {
        $_.FullName.StartsWith('assets/deep-native/', [StringComparison]::Ordinal)
    })
    if ($nativeEntries.Count -ne $expected.Count) {
        throw "APK approved native asset inventory differs: expected $($expected.Count), actual $($nativeEntries.Count)."
    }
    foreach ($entryName in $expected.Keys) {
        $matches = @($nativeEntries | Where-Object {
            [StringComparer]::Ordinal.Equals($_.FullName, $entryName)
        })
        if ($matches.Count -ne 1) {
            throw "APK must contain exactly one '$entryName' entry."
        }
        $entry = $matches[0]
        $approval = $expected[$entryName]
        if ($entry.Length -ne $approval.bytes) {
            throw "APK native asset '$entryName' length differs from its approval."
        }
        $stream = $entry.Open()
        try {
            $actual = [Convert]::ToHexString(
                [Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
        }
        finally {
            $stream.Dispose()
        }
        if (-not [StringComparer]::Ordinal.Equals($actual, $approval.sha256)) {
            throw "APK native asset '$entryName' digest differs from its approval."
        }
    }
}
finally {
    $archive.Dispose()
}

Write-Output "PASS Android APK contains the exact approved ML-KEM runtime asset set"
