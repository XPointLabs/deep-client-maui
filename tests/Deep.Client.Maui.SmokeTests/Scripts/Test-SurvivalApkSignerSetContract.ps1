$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$scriptPath = Join-Path $repoRoot 'eng\Invoke-SurvivalDevClient.ps1'
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $scriptPath, [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw 'Survival client script does not parse.' }
$function = $ast.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -ceq 'Get-ApkSignerSha256'
}, $true)
if ($null -eq $function) { throw 'APK signer parser function is missing.' }
Invoke-Expression $function.Extent.Text

$root = Join-Path ([IO.Path]::GetTempPath()) ('deep-signer-set-' + [Guid]::NewGuid().ToString('N'))
try {
    $javaHome = Join-Path $root 'jdk'
    $null = New-Item -ItemType Directory -Path (Join-Path $javaHome 'bin') -Force
    $null = New-Item -ItemType File -Path (Join-Path $javaHome 'bin\java.exe') -Force
    $script:JavaHome = $javaHome
    $apk = Join-Path $root 'candidate.apk'
    $null = New-Item -ItemType File -Path $apk
    $fakeSigner = Join-Path $root 'fake-apksigner.ps1'
    Set-Content -LiteralPath $fakeSigner -Encoding UTF8 -Value @'
$env:DEEP_TEST_SIGNER_OUTPUT -split "`n" | ForEach-Object { $_.TrimEnd("`r") }
exit 0
'@
    $digestA = 'a' * 64
    $digestB = 'b' * 64

    $env:DEEP_TEST_SIGNER_OUTPUT = "Signer #1 certificate SHA-256 digest: $digestA"
    $actual = Get-ApkSignerSha256 -ApkSigner $fakeSigner -Apk $apk
    if ($actual -cne $digestA) { throw 'One canonical signer was not accepted exactly.' }

    $caseIndex = 0
    foreach ($invalid in @(
        "Signer #1 certificate SHA-256 digest: $digestA`nSigner #2 certificate SHA-256 digest: $digestB",
        "Signer #1 certificate SHA-256 digest: $digestA`nSigner #1 certificate SHA-256 digest: $digestA",
        "Signer #2 certificate SHA-256 digest: $digestB",
        "Signer #1 certificate SHA-256 digest: $($digestA.ToUpperInvariant())")) {
        $caseIndex++
        $env:DEEP_TEST_SIGNER_OUTPUT = $invalid
        $rejected = $false
        try {
            $null = Get-ApkSignerSha256 -ApkSigner $fakeSigner -Apk $apk
        } catch {
            $rejected = $_.Exception.Message.Contains(
                'APK must have exactly one canonical signer and no rotation ambiguity.')
        }
        if (-not $rejected) {
            throw "An ambiguous or noncanonical signer set was accepted (case $caseIndex)."
        }
    }
} finally {
    Remove-Item Env:DEEP_TEST_SIGNER_OUTPUT -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
