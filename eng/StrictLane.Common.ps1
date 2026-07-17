Set-StrictMode -Version Latest

function Test-FullyQualifiedPath {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    $method = [IO.Path].GetMethod(
        'IsPathFullyQualified',
        [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::Static,
        $null,
        [Type[]]@([string]),
        $null)
    if ($null -ne $method) {
        return [bool]$method.Invoke($null, @($Path))
    }
    $uri = $null
    return [Uri]::TryCreate($Path, [UriKind]::Absolute, [ref]$uri) -and $uri.IsFile
}

function Remove-EndingDirectorySeparator {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $pathRoot = [IO.Path]::GetPathRoot($fullPath)
    while ($fullPath.Length -gt $pathRoot.Length -and
        ($fullPath.EndsWith([IO.Path]::DirectorySeparatorChar) -or
         $fullPath.EndsWith([IO.Path]::AltDirectorySeparatorChar))) {
        $fullPath = $fullPath.Substring(0, $fullPath.Length - 1)
    }
    return $fullPath
}

function Get-RelativePathCompat {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Candidate
    )

    $canonicalRoot = Remove-EndingDirectorySeparator ([IO.Path]::GetFullPath($Root))
    $canonicalCandidate = [IO.Path]::GetFullPath($Candidate)
    $getRelativePath = [IO.Path].GetMethod(
        'GetRelativePath',
        [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::Static,
        $null,
        [Type[]]@([string], [string]),
        $null)
    if ($null -ne $getRelativePath) {
        return [string]$getRelativePath.Invoke($null, @($canonicalRoot, $canonicalCandidate))
    }
    $rootUri = [Uri]($canonicalRoot + [IO.Path]::DirectorySeparatorChar)
    $candidateUri = [Uri]$canonicalCandidate
    return [Uri]::UnescapeDataString(
        $rootUri.MakeRelativeUri($candidateUri).OriginalString).Replace('/', [IO.Path]::DirectorySeparatorChar)
}

function Get-CanonicalContainedPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Root,
        [Parameter(Mandatory)]
        [string]$Candidate,
        [switch]$AllowRoot
    )

    if (-not (Test-FullyQualifiedPath $Root) -or -not (Test-FullyQualifiedPath $Candidate)) {
        throw 'Containment checks require absolute paths.'
    }

    $canonicalRoot = Remove-EndingDirectorySeparator ([IO.Path]::GetFullPath($Root))
    $canonicalCandidate = [IO.Path]::GetFullPath($Candidate)
    # Windows PowerShell 5.1 hosts .NET Framework, which lacks Path.GetRelativePath.
    # CI/release uses pwsh/.NET; the compatibility branch in this helper keeps local
    # negative tests meaningful without returning to prefix comparisons.
    $relative = Get-RelativePathCompat -Root $canonicalRoot -Candidate $canonicalCandidate
    $segments = @($relative.Split(
        @([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar),
        [StringSplitOptions]::RemoveEmptyEntries))
    $escapesRoot = (Test-FullyQualifiedPath $relative) -or
        $segments.Count -eq 0 -or
        $segments[0] -eq '..' -or
        $segments.Contains('..')
    if ($relative -eq '.') {
        if ($AllowRoot) {
            return $canonicalRoot
        }
        throw 'The selected path must be a child of the containment root.'
    }
    if ($escapesRoot) {
        throw "Path '$canonicalCandidate' is outside '$canonicalRoot'."
    }

    return $canonicalCandidate
}

function Assert-NoReparsePointInPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Root,
        [Parameter(Mandatory)]
        [string]$Candidate
    )

    $canonicalRoot = Get-CanonicalContainedPath -Root $Root -Candidate $Candidate -AllowRoot
    $rootPath = Remove-EndingDirectorySeparator ([IO.Path]::GetFullPath($Root))
    $current = $canonicalRoot
    while ($true) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to traverse reparse point '$current'."
            }
        }
        if ([StringComparer]::OrdinalIgnoreCase.Equals($current, $rootPath)) {
            break
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrWhiteSpace($parent) -or [StringComparer]::OrdinalIgnoreCase.Equals($parent, $current)) {
            throw 'Failed to reach the containment root while inspecting path components.'
        }
        $current = Remove-EndingDirectorySeparator $parent
    }
}

function Remove-ContainedTree {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Root,
        [Parameter(Mandatory)]
        [string]$Candidate
    )

    $canonicalCandidate = Get-CanonicalContainedPath -Root $Root -Candidate $Candidate
    Assert-NoReparsePointInPath -Root $Root -Candidate $canonicalCandidate
    if (Test-Path -LiteralPath $canonicalCandidate) {
        Remove-Item -LiteralPath $canonicalCandidate -Recurse -Force
    }
}

function Get-Sha256Lower {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-StrictPayloadSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$PayloadRoot,
        [Parameter(Mandatory)][string]$ExecutablePath
    )

    $canonicalRoot = Get-CanonicalContainedPath -Root $RepositoryRoot -Candidate $PayloadRoot
    $canonicalExecutable = Get-CanonicalContainedPath -Root $canonicalRoot -Candidate $ExecutablePath
    Assert-NoReparsePointInPath -Root $RepositoryRoot -Candidate $canonicalRoot
    Assert-NoReparsePointInPath -Root $canonicalRoot -Candidate $canonicalExecutable
    if (-not (Test-Path -LiteralPath $canonicalExecutable -PathType Leaf)) {
        throw 'The selected Windows executable does not exist.'
    }

    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($canonicalRoot)
    $files = [Collections.Generic.List[object]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Windows payload contains reparse point '$($item.FullName)'."
            }
            if ($item.PSIsContainer) {
                $pending.Push($item.FullName)
                continue
            }
            if (-not (Test-Path -LiteralPath $item.FullName -PathType Leaf)) {
                throw "Windows payload contains unsupported filesystem entry '$($item.FullName)'."
            }
            $relative = (Get-RelativePathCompat -Root $canonicalRoot -Candidate $item.FullName).Replace('\', '/')
            if ([string]::IsNullOrWhiteSpace($relative) -or
                $relative.StartsWith('/') -or
                $relative.Contains('\') -or
                $relative.Split('/').Contains('..') -or
                -not $seen.Add($relative)) {
                throw "Windows payload contains a duplicate or non-canonical path '$relative'."
            }
            $files.Add([ordered]@{
                relativePath = $relative
                length = [long]$item.Length
                sha256 = Get-Sha256Lower -Path $item.FullName
            })
        }
    }

    $ordered = @($files | Sort-Object { $_.relativePath })
    if ($ordered.Count -eq 0) {
        throw 'The Windows payload directory is empty.'
    }
    $executableRelative = (Get-RelativePathCompat -Root $canonicalRoot -Candidate $canonicalExecutable).Replace('\', '/')
    if (-not $seen.Contains($executableRelative)) {
        throw 'The selected executable is not part of the payload snapshot.'
    }
    return [ordered]@{
        payloadRoot = (Get-RelativePathCompat -Root $RepositoryRoot -Candidate $canonicalRoot).Replace('\', '/')
        executable = $executableRelative
        files = $ordered
    }
}

function Assert-StrictPayloadSnapshotsEqual {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Expected,
        [Parameter(Mandatory)][object]$Actual
    )

    if ($Expected.payloadRoot -ne $Actual.payloadRoot -or
        $Expected.executable -ne $Actual.executable -or
        @($Expected.files).Count -ne @($Actual.files).Count) {
        throw 'Windows payload identity changed during strict execution.'
    }
    for ($index = 0; $index -lt @($Expected.files).Count; $index++) {
        $left = @($Expected.files)[$index]
        $right = @($Actual.files)[$index]
        if ($left.relativePath -cne $right.relativePath -or
            [long]$left.length -ne [long]$right.length -or
            $left.sha256 -cne $right.sha256) {
            throw "Windows payload changed during strict execution at '$($left.relativePath)'."
        }
    }
}
