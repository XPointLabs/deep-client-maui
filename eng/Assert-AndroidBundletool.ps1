function Assert-NoReparsePath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $cursor = Get-Item -LiteralPath ([IO.Path]::GetFullPath($Path)) -Force
    while ($null -ne $cursor) {
        if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Bundletool path contains a reparse point.'
        }
        $cursor = if ($cursor -is [IO.FileInfo]) { $cursor.Directory } else { $cursor.Parent }
    }
}

function New-PrivateBundletoolDirectory {
    $directory = Join-Path ([IO.Path]::GetTempPath()) `
        ('deep-bundletool-lease-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    try {
        $security = [Security.AccessControl.DirectorySecurity]::new()
        $security.SetAccessRuleProtection($true, $false)
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            $identity,
            [Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
                [Security.AccessControl.InheritanceFlags]::ObjectInherit,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)
        $security.AddAccessRule($rule)
        Set-Acl -LiteralPath $directory -AclObject $security
        Assert-NoReparsePath -Path $directory
        return $directory
    }
    catch {
        if ([IO.Directory]::Exists($directory)) {
            [IO.Directory]::Delete($directory, $true)
        }
        throw
    }
}

function Open-VerifiedBundletoolLease {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256,
        [scriptblock]$AfterFlush
    )
    if ($ExpectedSha256 -notmatch '^[0-9a-f]{64}$' -or
        [string]::IsNullOrWhiteSpace($Path) -or
        -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw 'Pinned bundletool input is invalid.'
    }
    $resolved = [IO.Path]::GetFullPath($Path)
    Assert-NoReparsePath -Path $resolved
    $source = [IO.File]::Open($resolved, [IO.FileMode]::Open,
        [IO.FileAccess]::Read, [IO.FileShare]::None)
    $directory = $null
    $lease = $null
    try {
        if ($source.Length -le 0 -or $source.Length -gt 256MB) {
            throw 'Bundletool is empty or oversized.'
        }
        $directory = New-PrivateBundletoolDirectory
        $destinationPath = Join-Path $directory 'bundletool-all-1.18.3.jar'
        $lease = [IO.File]::Open($destinationPath, [IO.FileMode]::CreateNew,
            [IO.FileAccess]::ReadWrite, [IO.FileShare]::Read)
        $sha256 = [Security.Cryptography.SHA256]::Create()
        try {
            $buffer = [byte[]]::new(131072)
            [long]$written = 0
            while (($read = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $written += $read
                if ($written -gt $source.Length) {
                    throw 'Bundletool expanded past its declared length.'
                }
                $sha256.TransformBlock($buffer, 0, $read, $null, 0) | Out-Null
                $lease.Write($buffer, 0, $read)
            }
            $sha256.TransformFinalBlock([byte[]]::new(0), 0, 0) | Out-Null
            if ($written -ne $source.Length) { throw 'Bundletool copy is truncated.' }
            $actual = [BitConverter]::ToString($sha256.Hash).Replace('-', '').ToLowerInvariant()
            if ($actual -cne $ExpectedSha256) {
                throw 'Bundletool SHA-256 mismatch; release requires pinned bundletool 1.18.3.'
            }
            $lease.Flush($true)
        }
        finally { $sha256.Dispose() }
        if ($null -ne $AfterFlush) { & $AfterFlush $destinationPath }
        Assert-NoReparsePath -Path $resolved
        Assert-NoReparsePath -Path $destinationPath
        $lease.Position = 0
        $verification = [Security.Cryptography.SHA256]::Create()
        try {
            $copiedHash = [BitConverter]::ToString(
                $verification.ComputeHash($lease)).Replace('-', '').ToLowerInvariant()
        }
        finally { $verification.Dispose() }
        if ($copiedHash -cne $ExpectedSha256) {
            throw 'Leased bundletool copy failed verification.'
        }
        $lease.Position = 0
        Assert-NoReparsePath -Path $destinationPath
        return [pscustomobject]@{
            Path = $destinationPath
            Directory = $directory
            Lease = $lease
        }
    }
    catch {
        if ($null -ne $lease) { $lease.Dispose() }
        if ($null -ne $directory -and [IO.Directory]::Exists($directory)) {
            [IO.Directory]::Delete($directory, $true)
        }
        throw
    }
    finally { $source.Dispose() }
}

function Open-PinnedAndroidBundletool {
    param([Parameter(Mandatory = $true)][string]$Path)
    return Open-VerifiedBundletoolLease -Path $Path -ExpectedSha256 `
        'a099cfa1543f55593bc2ed16a70a7c67fe54b1747bb7301f37fdfd6d91028e29'
}

function Close-PinnedAndroidBundletool {
    param([Parameter(Mandatory = $true)]$Lease)
    if ($null -ne $Lease.Lease) { $Lease.Lease.Dispose() }
    if (-not [string]::IsNullOrWhiteSpace([string]$Lease.Directory) -and
        [IO.Directory]::Exists([string]$Lease.Directory)) {
        [IO.Directory]::Delete([string]$Lease.Directory, $true)
    }
}
