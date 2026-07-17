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
