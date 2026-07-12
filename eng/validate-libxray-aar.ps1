param(
    [Parameter(Mandatory = $true)]
    [string]$AarPath
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$ExpectedAarSha256 = "3A8E15665C42A4A9385A04E9409B65F8368D775D872BE6FBB2F469393D6682F1"

function Assert-Range([uint64]$Offset, [uint64]$Size, [uint64]$Length, [string]$Description) {
    if ($Offset -gt $Length -or $Size -gt ($Length - $Offset)) {
        throw "$Description points outside the ELF file."
    }
}

function Get-SectionName(
    [byte[]]$Bytes,
    [uint64]$StringTableOffset,
    [uint64]$StringTableSize,
    [uint32]$NameOffset) {
    if ($NameOffset -ge $StringTableSize) {
        throw "A section name points outside .shstrtab."
    }

    $cursor = [uint64]$StringTableOffset + $NameOffset
    $limit = [uint64]$StringTableOffset + $StringTableSize
    $characters = New-Object System.Collections.Generic.List[byte]
    while ($cursor -lt $limit -and $Bytes[$cursor] -ne 0) {
        $characters.Add($Bytes[$cursor])
        $cursor++
    }
    return [Text.Encoding]::ASCII.GetString($characters.ToArray())
}

function Assert-Elf([byte[]]$Bytes, [string]$Name) {
    if ($Bytes.Length -lt 64 -or $Bytes[0] -ne 0x7f -or $Bytes[1] -ne 0x45 -or
        $Bytes[2] -ne 0x4c -or $Bytes[3] -ne 0x46) {
        throw "$Name is not an ELF library."
    }
    if ($Bytes[4] -ne 2 -or $Bytes[5] -ne 1) {
        throw "$Name must be a little-endian ELF64 library."
    }

    $length = [uint64]$Bytes.LongLength
    $programOffset = [BitConverter]::ToUInt64($Bytes, 32)
    $sectionOffset = [BitConverter]::ToUInt64($Bytes, 40)
    $programEntrySize = [BitConverter]::ToUInt16($Bytes, 54)
    $programCount = [BitConverter]::ToUInt16($Bytes, 56)
    $sectionEntrySize = [BitConverter]::ToUInt16($Bytes, 58)
    $sectionCount = [BitConverter]::ToUInt16($Bytes, 60)
    $stringTableIndex = [BitConverter]::ToUInt16($Bytes, 62)

    Assert-Range $programOffset ([uint64]$programEntrySize * $programCount) $length "$Name program table"
    Assert-Range $sectionOffset ([uint64]$sectionEntrySize * $sectionCount) $length "$Name section table"
    if ($programEntrySize -lt 56 -or $sectionEntrySize -lt 64 -or $stringTableIndex -ge $sectionCount) {
        throw "$Name has invalid ELF table metadata."
    }

    $loadSegments = 0
    for ($index = 0; $index -lt $programCount; $index++) {
        $header = [int]($programOffset + ([uint64]$index * $programEntrySize))
        if ([BitConverter]::ToUInt32($Bytes, $header) -eq 1) {
            $loadSegments++
            $alignment = [BitConverter]::ToUInt64($Bytes, $header + 48)
            if ($alignment -lt 16384) {
                throw "$Name contains a LOAD segment aligned to $alignment bytes; Google Play requires 16 KB compatibility."
            }
        }
    }
    if ($loadSegments -eq 0) {
        throw "$Name does not contain LOAD segments."
    }

    $stringHeader = [int]($sectionOffset + ([uint64]$stringTableIndex * $sectionEntrySize))
    if ([BitConverter]::ToUInt32($Bytes, $stringHeader + 4) -ne 3) {
        throw "$Name .shstrtab entry is not a string table."
    }
    $stringTableOffset = [BitConverter]::ToUInt64($Bytes, $stringHeader + 24)
    $stringTableSize = [BitConverter]::ToUInt64($Bytes, $stringHeader + 32)
    Assert-Range $stringTableOffset $stringTableSize $length "$Name .shstrtab"

    $dynamicFound = $false
    for ($index = 0; $index -lt $sectionCount; $index++) {
        $header = [int]($sectionOffset + ([uint64]$index * $sectionEntrySize))
        $nameOffset = [BitConverter]::ToUInt32($Bytes, $header)
        $sectionName = Get-SectionName $Bytes $stringTableOffset $stringTableSize $nameOffset
        if ($sectionName -eq ".dynamic" -and [BitConverter]::ToUInt32($Bytes, $header + 4) -eq 6) {
            $dynamicFound = $true
        }
    }
    if (-not $dynamicFound) {
        throw "$Name does not contain a valid .dynamic section."
    }
}

$resolvedAar = (Resolve-Path -LiteralPath $AarPath).Path
$actualAarSha256 = (Get-FileHash -LiteralPath $resolvedAar -Algorithm SHA256).Hash.ToUpperInvariant()
if ($actualAarSha256 -ne $ExpectedAarSha256) {
    throw "libXray AAR SHA-256 mismatch. Expected $ExpectedAarSha256, got $actualAarSha256."
}

$archive = [IO.Compression.ZipFile]::OpenRead($resolvedAar)
try {
    if (@($archive.Entries | Where-Object { $_.FullName.Contains('\') }).Count -gt 0) {
        throw "The AAR contains Windows path separators."
    }
    if ($null -eq $archive.GetEntry("res/")) {
        throw "The AAR must contain the res/ directory required by the Android build tools."
    }

    foreach ($entryName in @("jni/arm64-v8a/libgojni.so", "jni/x86_64/libgojni.so")) {
        $entry = $archive.GetEntry($entryName)
        if ($null -eq $entry) {
            throw "The AAR does not contain $entryName."
        }

        $memory = New-Object IO.MemoryStream
        $stream = $entry.Open()
        try {
            $stream.CopyTo($memory)
            Assert-Elf $memory.ToArray() $entryName
        }
        finally {
            $stream.Dispose()
            $memory.Dispose()
        }
    }
}
finally {
    $archive.Dispose()
}

Write-Host "Validated pinned libXray AAR ($actualAarSha256) and native ELF layout in $resolvedAar."
