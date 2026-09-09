[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('ProvisionIdentity', 'Attach', 'GroupText', 'PayloadMatrix', 'PrivacyFallback', 'Call', 'RestartDurability', 'ManualResendAfterRestart', 'AutomaticRetryAfterRestart', 'AckCrashWindow', 'NegativeRuntime')]
    [string]$Phase,
    [string]$AndroidSerial = '192.168.1.45:43337',
    [string]$MailboxBootstrapRoot = 'C:\Work\DeepSession\secrets\mailbox-bootstrap',
    [string]$MrXPublicKeySha256 = $env:DEEP_MR_X_PUBLIC_KEY_SHA256,
    [string]$AndroidPickerFileId = 'android:id/title',
    [string]$AndroidPickerConfirmId,
    [string]$WindowsUatApprovalTuplePath,
    [switch]$ResetAndroidE2eLocalState,
    [switch]$ResetWindowsUatLocalState,
    [switch]$Execute
)

# This is an opt-in physical lane. It is non-destructive unless the caller supplies
# one of the explicit UAT reset switches for ProvisionIdentity; those closed paths
# confirm the application's own reset dialog inside the exact physical runtime. The
# Android switch applies only to the separate E2E package. The production package is
# always snapshotted before/after and is never altered.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (($ResetAndroidE2eLocalState -or $ResetWindowsUatLocalState) -and
    $Phase -cne 'ProvisionIdentity') {
    throw 'UAT local reset switches are allowed only for ProvisionIdentity.'
}

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Deep.PhysicalE2E
{
    public sealed class BoundedProcessResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; }
    }

    public static class BoundedProcess
    {
        private const uint JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const uint CreateSuspended = 0x00000004;
        private const uint CreateNoWindow = 0x08000000;
        private const uint StartfUseStdHandles = 0x00000100;
        private const uint HandleFlagInherit = 0x00000001;
        private const uint WaitObject0 = 0;
        private const uint WaitTimeout = 258;
        private const int MaximumOutputBytes = 1024 * 1024;

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimitInformation
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimitInformation
        {
            public BasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Length;
            public IntPtr SecurityDescriptor;
            public int InheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Cb;
            public string Reserved;
            public string Desktop;
            public string Title;
            public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute;
            public uint Flags;
            public short ShowWindow, Reserved2;
            public IntPtr ReservedPointer, StandardInput, StandardOutput, StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr ProcessHandle, ThreadHandle;
            public uint ProcessId, ThreadId;
        }

        private sealed class OutputBudget
        {
            private readonly IntPtr job;
            private int total;

            public OutputBudget(IntPtr job) { this.job = job; }

            public void Add(int count)
            {
                if (Interlocked.Add(ref total, count) > MaximumOutputBytes)
                {
                    TerminateJobObject(job, 0xE0000003);
                    throw new InvalidDataException("Bounded child exceeded the aggregate output limit.");
                }
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, uint infoClass,
            IntPtr information, uint informationLength);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out IntPtr read, out IntPtr write,
            ref SecurityAttributes attributes, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine,
            IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
            uint creationFlags, IntPtr environment, string currentDirectory,
            ref StartupInfo startupInfo, out ProcessInformation processInformation);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        public static BoundedProcessResult Run(
            string executable, string[] arguments, string workingDirectory, int timeoutMilliseconds)
        {
            if (timeoutMilliseconds < 1) throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw new Win32Exception();
            IntPtr childInput = IntPtr.Zero, parentInput = IntPtr.Zero;
            IntPtr parentOutput = IntPtr.Zero, childOutput = IntPtr.Zero;
            IntPtr parentError = IntPtr.Zero, childError = IntPtr.Zero;
            var information = new ProcessInformation();
            try
            {
                var limits = new ExtendedLimitInformation();
                limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
                int size = Marshal.SizeOf(typeof(ExtendedLimitInformation));
                IntPtr memory = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(limits, memory, false);
                    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, memory, (uint)size))
                        throw new Win32Exception();
                }
                finally { Marshal.FreeHGlobal(memory); }

                CreatePipePair(out childInput, out parentInput, false);
                CreatePipePair(out childOutput, out parentOutput, true);
                CreatePipePair(out childError, out parentError, true);
                var startup = new StartupInfo
                {
                    Cb = Marshal.SizeOf(typeof(StartupInfo)),
                    Flags = StartfUseStdHandles,
                    StandardInput = childInput,
                    StandardOutput = childOutput,
                    StandardError = childError
                };
                var commandLine = new StringBuilder();
                commandLine.Append(Quote(executable));
                if (arguments.Length != 0) commandLine.Append(' ').Append(BuildCommandLine(arguments));
                if (!CreateProcessW(executable, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                        CreateSuspended | CreateNoWindow, IntPtr.Zero, workingDirectory,
                        ref startup, out information))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                Close(ref childInput);
                Close(ref childOutput);
                Close(ref childError);
                Close(ref parentInput);
                if (!AssignProcessToJobObject(job, information.ProcessHandle))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                using (var output = new FileStream(new SafeFileHandle(parentOutput, true),
                           FileAccess.Read, 4096, false))
                using (var error = new FileStream(new SafeFileHandle(parentError, true),
                           FileAccess.Read, 4096, false))
                {
                    parentOutput = IntPtr.Zero;
                    parentError = IntPtr.Zero;
                    var budget = new OutputBudget(job);
                    var stdout = ReadCappedAsync(output, budget);
                    var stderr = ReadCappedAsync(error, budget);
                    if (ResumeThread(information.ThreadHandle) == UInt32.MaxValue)
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    Close(ref information.ThreadHandle);
                    var wait = WaitForSingleObject(information.ProcessHandle, (uint)timeoutMilliseconds);
                    if (wait == WaitTimeout)
                    {
                        TerminateJobObject(job, 0xE0000002);
                        WaitForSingleObject(information.ProcessHandle, 10000);
                        throw new TimeoutException("Bounded child exceeded its deadline and its job was terminated.");
                    }
                    if (wait != WaitObject0) throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (!Task.WaitAll(new Task[] { stdout, stderr }, 10000))
                        throw new TimeoutException("Bounded child output did not close after process exit.");
                    uint exitCode;
                    if (!GetExitCodeProcess(information.ProcessHandle, out exitCode))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    return new BoundedProcessResult {
                        ExitCode = unchecked((int)exitCode),
                        Output = Encoding.UTF8.GetString(stdout.Result)
                            + Encoding.UTF8.GetString(stderr.Result)
                    };
                }
            }
            catch
            {
                TerminateJobObject(job, 0xE0000004);
                if (information.ProcessHandle != IntPtr.Zero)
                    WaitForSingleObject(information.ProcessHandle, 10000);
                throw;
            }
            finally
            {
                Close(ref information.ThreadHandle);
                Close(ref information.ProcessHandle);
                Close(ref childInput); Close(ref parentInput);
                Close(ref childOutput); Close(ref parentOutput);
                Close(ref childError); Close(ref parentError);
                CloseHandle(job);
            }
        }

        private static async Task<byte[]> ReadCappedAsync(Stream stream, OutputBudget budget)
        {
            using (var memory = new MemoryStream())
            {
                var buffer = new byte[8192];
                while (true)
                {
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (read == 0) return memory.ToArray();
                    budget.Add(read);
                    memory.Write(buffer, 0, read);
                }
            }
        }

        private static void CreatePipePair(out IntPtr child, out IntPtr parent, bool parentReads)
        {
            var attributes = new SecurityAttributes {
                Length = Marshal.SizeOf(typeof(SecurityAttributes)), InheritHandle = 1
            };
            IntPtr read;
            IntPtr write;
            if (!CreatePipe(out read, out write, ref attributes, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            child = parentReads ? write : read;
            parent = parentReads ? read : write;
            if (!SetHandleInformation(parent, HandleFlagInherit, 0))
            {
                Close(ref child); Close(ref parent);
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        private static void Close(ref IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            CloseHandle(handle);
            handle = IntPtr.Zero;
        }

        private static string BuildCommandLine(string[] arguments)
        {
            var result = new StringBuilder();
            foreach (string argument in arguments)
            {
                if (result.Length != 0) result.Append(' ');
                result.Append(Quote(argument ?? String.Empty));
            }
            return result.ToString();
        }

        private static string Quote(string value)
        {
            if (value.Length != 0 && value.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
                return value;
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char ch in value)
            {
                if (ch == '\\') { slashes++; continue; }
                if (ch == '"')
                {
                    result.Append('\\', slashes * 2 + 1).Append('"');
                    slashes = 0;
                    continue;
                }
                result.Append('\\', slashes).Append(ch);
                slashes = 0;
            }
            result.Append('\\', slashes * 2).Append('"');
            return result.ToString();
        }
    }
}
'@

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$devOpsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot '..\deep-devops'))
$chaosManifestPath = Join-Path $repoRoot 'eng\physical-chaos-dependencies.v1.json'
$chaosManifestSha256 = 'f23077464f15eaf1a788f4d585a409ef1ed89b8657ac288ce6246683626b7584'
$androidPackage = 'network.xpoint.deep.e2e'
$productionPackage = 'network.xpoint.deep'
$policyPath = Join-Path $repoRoot '.secrets\android-lab\approved-policy.json'

function Test-AbsoluteWindowsPath([string]$Path) {
    return -not [string]::IsNullOrWhiteSpace($Path) -and
        ($Path -cmatch '^[A-Za-z]:[\\/]' -or
         $Path -cmatch '^\\\\[^\\/]+[\\/][^\\/]+')
}

function Assert-AbsoluteExisting([string]$Path, [string]$Label, [switch]$Directory) {
    if (-not (Test-AbsoluteWindowsPath $Path) -or
        -not (Test-Path -LiteralPath $Path -PathType $(if ($Directory) { 'Container' } else { 'Leaf' }))) {
        throw "$Label must be an existing absolute $($(if ($Directory) { 'directory' } else { 'file' }))."
    }
    return [IO.Path]::GetFullPath($Path)
}

function Invoke-AdbQuiet([string[]]$Arguments) {
    $output = @(& $script:adb @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "ADB command failed: $($Arguments[0..([Math]::Min(3, $Arguments.Count - 1))] -join ' ')" }
    return $output
}

function Get-PackageSnapshot([string]$Package) {
    $value = @(Invoke-AdbQuiet @('-s', $AndroidSerial, 'shell', 'pm', 'path', $Package) |
        ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join "`n"
    if ([string]::IsNullOrWhiteSpace($value)) { throw "Required package '$Package' is absent." }
    return $value
}

function Get-Sha256([string]$Path) {
    $stream = [IO.File]::Open(
        $Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
    } finally {
        $algorithm.Dispose()
        $stream.Dispose()
    }
}

function Get-TextSha256([string]$Value) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $digest = $algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value))
        return -join ($digest | ForEach-Object { $_.ToString('x2') })
    } finally {
        $algorithm.Dispose()
    }
}

function Read-ExactPinnedUtf8([string]$Path, [string]$ExpectedSha256, [int]$MaximumBytes) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    $bytes = $null
    try {
        if ($stream.Length -lt 1 -or $stream.Length -gt $MaximumBytes) {
            throw 'Pinned UTF-8 file length is outside its closed bound.'
        }
        $bytes = [byte[]]::new([int]$stream.Length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            if ($read -eq 0) { throw 'Pinned UTF-8 file ended before its declared length.' }
            $offset += $read
        }
        if ($stream.ReadByte() -ne -1) { throw 'Pinned UTF-8 file changed while being read.' }
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try {
            $digest = -join ($algorithm.ComputeHash($bytes) |
                ForEach-Object { $_.ToString('x2') })
        } finally {
            $algorithm.Dispose()
        }
        if ($digest -cne $ExpectedSha256) {
            throw 'Pinned UTF-8 file does not match the reviewed SHA-256.'
        }
        return [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
    } finally {
        if ($null -ne $bytes) { [Array]::Clear($bytes, 0, $bytes.Length) }
        $stream.Dispose()
    }
}

function Read-ExactPinnedBytes([string]$Path, [string]$ExpectedSha256, [int]$MaximumBytes) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try {
        if ($stream.Length -lt 1 -or $stream.Length -gt $MaximumBytes) {
            throw 'Pinned dependency length is outside its closed bound.'
        }
        $bytes = [byte[]]::new([int]$stream.Length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            if ($read -eq 0) { throw 'Pinned dependency ended before its declared length.' }
            $offset += $read
        }
        if ($stream.ReadByte() -ne -1) { throw 'Pinned dependency changed while being read.' }
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try {
            $digest = -join ($algorithm.ComputeHash($bytes) |
                ForEach-Object { $_.ToString('x2') })
        } finally { $algorithm.Dispose() }
        if ($digest -cne $ExpectedSha256) {
            throw 'Pinned dependency bytes do not match the reviewed SHA-256.'
        }
        return ,$bytes
    } finally { $stream.Dispose() }
}

function Assert-RegularNonReparseFile([string]$Path, [string]$Label) {
    $full = Assert-AbsoluteExisting $Path $Label
    $item = Get-Item -Force -LiteralPath $full
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label must be a regular non-reparse file."
    }
    return $full
}

function Assert-AuthorityPathAncestors([string]$Root, [string]$Path, [string]$Label) {
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $pathFull = [IO.Path]::GetFullPath($Path)
    if (-not $pathFull.StartsWith($rootFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label escaped its authority root."
    }
    $currentPath = $pathFull
    while ($true) {
        $current = Get-Item -Force -LiteralPath $currentPath
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Label traversed a reparse point."
        }
        if ([StringComparer]::OrdinalIgnoreCase.Equals(
                $current.FullName.TrimEnd('\'), $rootFull)) {
            break
        }
        $parent = [IO.Path]::GetDirectoryName($currentPath)
        if ([string]::IsNullOrWhiteSpace($parent) -or
            [StringComparer]::OrdinalIgnoreCase.Equals($parent, $currentPath)) {
            throw "$Label did not reach its authority root."
        }
        $currentPath = $parent
    }
}

function Assert-ChaosDependencyAuthority {
    $manifestFile = Assert-RegularNonReparseFile $script:chaosManifestPath 'Chaos dependency manifest'
    $manifestText = Read-ExactPinnedUtf8 $manifestFile $script:chaosManifestSha256 65536
    $manifest = $manifestText | ConvertFrom-Json
    Assert-ExactJsonProperties $manifest @(
        'schema', 'reviewedDevOpsCommit', 'dependencyTreeSha256',
        'files', 'closedDirectories', 'systemExecutables') 'Chaos dependency manifest'
    if ($manifest.schema -cne 'deep.physical-chaos-dependencies.v1' -or
        $manifest.reviewedDevOpsCommit -cne '70fb9d786ba8d927dfb0e9bc7dbc1f13f6581d35' -or
        $manifest.dependencyTreeSha256 -cnotmatch '^[a-f0-9]{64}$') {
        throw 'Chaos dependency manifest identity is invalid.'
    }
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
        $script:devOpsRoot,
        [IO.Path]::GetFullPath((Join-Path $script:repoRoot '..\deep-devops')))) {
        throw 'Chaos authority must use the sibling deep-devops checkout.'
    }

    $lines = [Collections.Generic.List[string]]::new()
    $declaredFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $previous = $null
    foreach ($entry in @($manifest.files)) {
        Assert-ExactJsonProperties $entry @('path', 'sha256') 'Chaos dependency file'
        $relative = [string]$entry.path
        $sha256 = [string]$entry.sha256
        if ($relative -cnotmatch '^[a-z0-9][a-z0-9._/-]*$' -or $relative.Contains('\') -or
            [IO.Path]::IsPathRooted($relative) -or $relative -match '(^|/)\.\.?(/|$)' -or
            $relative.EndsWith('.env', [StringComparison]::OrdinalIgnoreCase) -or
            $sha256 -cnotmatch '^[a-f0-9]{64}$' -or
            ($null -ne $previous -and [StringComparer]::Ordinal.Compare($previous, $relative) -ge 0) -or
            -not $declaredFiles.Add($relative)) {
            throw 'Chaos dependency file list is not canonical, sorted, and unique.'
        }
        $previous = $relative
        $full = [IO.Path]::GetFullPath((Join-Path $script:devOpsRoot ($relative.Replace('/', '\'))))
        Assert-RegularNonReparseFile $full "Chaos dependency $relative" | Out-Null
        Assert-AuthorityPathAncestors $script:devOpsRoot $full "Chaos dependency $relative"
        if ((Get-Sha256 $full) -cne $sha256) { throw 'Chaos dependency SHA-256 mismatch.' }
        $lines.Add("file:$relative=$sha256`n")
    }
    $haproxyConfigRelativePath = 'config/survival-uat-tls/haproxy.cfg'
    if (-not $declaredFiles.Contains($haproxyConfigRelativePath)) {
        throw 'Chaos dependency manifest must pin the HAProxy configuration.'
    }

    $previous = $null
    foreach ($relativeValue in @($manifest.closedDirectories)) {
        $relative = [string]$relativeValue
        if ($relative -cnotmatch '^[a-z0-9][a-z0-9._/-]*$' -or $relative.Contains('\') -or
            [IO.Path]::IsPathRooted($relative) -or $relative -match '(^|/)\.\.?(/|$)' -or
            ($null -ne $previous -and [StringComparer]::Ordinal.Compare($previous, $relative) -ge 0)) {
            throw 'Chaos closed directory list is not canonical, sorted, and unique.'
        }
        $previous = $relative
        $full = [IO.Path]::GetFullPath((Join-Path $script:devOpsRoot ($relative.Replace('/', '\'))))
        Assert-AbsoluteExisting $full "Chaos closed directory $relative" -Directory | Out-Null
        Assert-AuthorityPathAncestors $script:devOpsRoot $full "Chaos closed directory $relative"
        $actualFiles = @()
        foreach ($item in Get-ChildItem -Force -Recurse -LiteralPath $full) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Chaos closed directory contains a reparse point.'
            }
            if ($item.PSIsContainer) {
                throw 'Chaos closed directory contains an undeclared directory.'
            } else {
                $actualFiles += $item.FullName.Substring(
                    $script:devOpsRoot.TrimEnd('\').Length + 1).Replace('\', '/')
            }
        }
        $expectedFiles = @($declaredFiles | Where-Object {
            $_.StartsWith($relative + '/', [StringComparison]::Ordinal) })
        if (@(Compare-Object -CaseSensitive -ReferenceObject @($expectedFiles | Sort-Object) `
                -DifferenceObject @($actualFiles | Sort-Object)).Count -ne 0) {
            throw 'Chaos closed directory file set does not match the reviewed manifest.'
        }
        $lines.Add("directory:$relative`n")
    }

    $expectedSystems = [ordered]@{
        docker = 'C:/Program Files/Docker/Docker/resources/bin/docker.exe'
        dockerCompose = 'C:/Program Files/Docker/Docker/resources/bin/docker-compose.exe'
        dotnet = 'C:/Program Files/dotnet/dotnet.exe'
        powershell = 'C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe'
        taskkill = 'C:/Windows/System32/taskkill.exe'
    }
    $systemPaths = @{}
    $previous = $null
    foreach ($entry in @($manifest.systemExecutables)) {
        Assert-ExactJsonProperties $entry @('name', 'path', 'sha256') 'Chaos system executable'
        $name = [string]$entry.name
        $canonicalPath = [string]$entry.path
        $sha256 = [string]$entry.sha256
        if (-not $expectedSystems.Contains($name) -or
            $canonicalPath -cne $expectedSystems[$name] -or
            ($null -ne $previous -and [StringComparer]::Ordinal.Compare($previous, $name) -ge 0) -or
            $sha256 -cnotmatch '^[a-f0-9]{64}$') {
            throw 'Chaos system executable set is invalid.'
        }
        $previous = $name
        $nativePath = $canonicalPath.Replace('/', '\')
        Assert-RegularNonReparseFile $nativePath "Chaos system executable $name" | Out-Null
        Assert-AuthorityPathAncestors ([IO.Path]::GetPathRoot($nativePath)) `
            $nativePath "Chaos system executable $name"
        if ((Get-Sha256 $nativePath) -cne $sha256) { throw 'Chaos system executable SHA-256 mismatch.' }
        $systemPaths[$name] = $nativePath
        $lines.Add("system:$name|$canonicalPath=$sha256`n")
    }
    if ($systemPaths.Count -ne $expectedSystems.Count) {
        throw 'Chaos system executable set is incomplete.'
    }
    $orderedLines = $lines.ToArray()
    [Array]::Sort($orderedLines, [StringComparer]::Ordinal)
    $treeMaterial = 'deep.physical-chaos.dependency-tree.v1' + [char]0 + ($orderedLines -join '')
    if ((Get-TextSha256 $treeMaterial) -cne $manifest.dependencyTreeSha256) {
        throw 'Chaos dependency tree digest is invalid.'
    }
    return [pscustomobject]@{
        Launcher = Join-Path $script:devOpsRoot 'scripts\survival-dev.ps1'
        HAProxyConfig = Join-Path $script:devOpsRoot `
            ($haproxyConfigRelativePath.Replace('/', '\'))
        DevOpsRoot = $script:devOpsRoot
        Manifest = $manifestFile
        Files = @($manifest.files)
        Docker = $systemPaths['docker']
        DockerSha256 = [string](@($manifest.systemExecutables |
            Where-Object { $_.name -ceq 'docker' })[0].sha256)
        DockerCompose = $systemPaths['dockerCompose']
        DockerComposeSha256 = [string](@($manifest.systemExecutables |
            Where-Object { $_.name -ceq 'dockerCompose' })[0].sha256)
        PowerShell = $systemPaths['powershell']
        DotNet = $systemPaths['dotnet']
        ManifestSha256 = $script:chaosManifestSha256
        DependencyTreeSha256 = [string]$manifest.dependencyTreeSha256
    }
}

function New-ChaosDependencySnapshot([object]$Authority, [string]$SnapshotRoot) {
    if (Test-Path -LiteralPath $SnapshotRoot) {
        throw 'Chaos dependency snapshot path must be fresh.'
    }
    [void][IO.Directory]::CreateDirectory($SnapshotRoot)
    Set-ProtectedRunTree $SnapshotRoot
    $snapshotDevOps = Join-Path $SnapshotRoot 'deep-devops'
    [void][IO.Directory]::CreateDirectory($snapshotDevOps)
    foreach ($entry in @($Authority.Files)) {
        $relative = [string]$entry.path
        $source = Join-Path $Authority.DevOpsRoot ($relative.Replace('/', '\'))
        $destination = Join-Path $snapshotDevOps ($relative.Replace('/', '\'))
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
        $bytes = Read-ExactPinnedBytes $source ([string]$entry.sha256) (4 * 1024 * 1024)
        try {
            $stream = [IO.File]::Open(
                $destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
            finally { $stream.Dispose() }
        } finally { [Array]::Clear($bytes, 0, $bytes.Length) }
        if ((Get-Sha256 $destination) -cne [string]$entry.sha256) {
            throw 'Chaos dependency snapshot reread verification failed.'
        }
        [IO.File]::SetAttributes(
            $destination,
            [IO.File]::GetAttributes($destination) -bor [IO.FileAttributes]::ReadOnly)
    }
    $snapshotManifest = Join-Path $SnapshotRoot 'physical-chaos-dependencies.v1.json'
    $manifestBytes = Read-ExactPinnedBytes $Authority.Manifest $script:chaosManifestSha256 65536
    try { [IO.File]::WriteAllBytes($snapshotManifest, $manifestBytes) }
    finally { [Array]::Clear($manifestBytes, 0, $manifestBytes.Length) }
    [IO.File]::SetAttributes(
        $snapshotManifest,
        [IO.File]::GetAttributes($snapshotManifest) -bor [IO.FileAttributes]::ReadOnly)
    Set-ProtectedRunTree $SnapshotRoot
    $snapshotDigest = Get-TextSha256 (
        'deep.physical-chaos.execution-snapshot.v1' + [char]0 +
        $Authority.ManifestSha256 + '|' + $Authority.DependencyTreeSha256)
    $leases = [Collections.Generic.List[IO.FileStream]]::new()
    try {
        foreach ($entry in @($Authority.Files)) {
            $path = Join-Path $snapshotDevOps (([string]$entry.path).Replace('/', '\'))
            $leases.Add([IO.File]::Open(
                $path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read))
        }
        $leases.Add([IO.File]::Open(
            $snapshotManifest, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read))
    } catch {
        foreach ($lease in $leases) { $lease.Dispose() }
        throw
    }
    return [pscustomobject]@{
        Root = $SnapshotRoot
        DevOpsRoot = $snapshotDevOps
        Manifest = $snapshotManifest
        Files = @($Authority.Files)
        Launcher = Join-Path $snapshotDevOps 'scripts\survival-dev.ps1'
        HAProxyConfig = Join-Path $snapshotDevOps 'config\survival-uat-tls\haproxy.cfg'
        Docker = $Authority.Docker
        DockerSha256 = $Authority.DockerSha256
        DockerCompose = $Authority.DockerCompose
        DockerComposeSha256 = $Authority.DockerComposeSha256
        PowerShell = $Authority.PowerShell
        DotNet = $Authority.DotNet
        ManifestSha256 = $Authority.ManifestSha256
        DependencyTreeSha256 = $Authority.DependencyTreeSha256
        SnapshotSha256 = $snapshotDigest
        Leases = $leases
    }
}

function Close-ChaosDependencySnapshot([object]$Authority) {
    if ($null -eq $Authority) { return }
    $failures = [Collections.Generic.List[Exception]]::new()
    foreach ($lease in @($Authority.Leases)) {
        try { $lease.Dispose() } catch { $failures.Add($_.Exception) }
    }
    if ($failures.Count -ne 0) {
        throw [AggregateException]::new('Private chaos snapshot lease release failed.', $failures)
    }
}

function Get-ChaosExecutionAuthority {
    $authority = $script:chaosExecutionAuthority
    if ($null -eq $authority) { throw 'Private chaos execution snapshot is not initialized.' }
    if ((Get-Sha256 $authority.Manifest) -cne $authority.ManifestSha256) {
        throw 'Private chaos snapshot manifest changed after creation.'
    }
    foreach ($entry in @($authority.Files)) {
        $path = Join-Path $authority.DevOpsRoot (([string]$entry.path).Replace('/', '\'))
        $file = Assert-RegularNonReparseFile $path 'Private chaos snapshot dependency'
        Assert-AuthorityPathAncestors $authority.Root $file 'Private chaos snapshot dependency'
        if (-not ((Get-Item -Force -LiteralPath $file).Attributes -band
                [IO.FileAttributes]::ReadOnly) -or
            (Get-Sha256 $file) -cne [string]$entry.sha256) {
            throw 'Private chaos snapshot dependency changed after creation.'
        }
    }
    $expectedHAProxyConfig = Join-Path $authority.DevOpsRoot `
        'config\survival-uat-tls\haproxy.cfg'
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
            [IO.Path]::GetFullPath($authority.HAProxyConfig),
            [IO.Path]::GetFullPath($expectedHAProxyConfig))) {
        throw 'Private chaos HAProxy configuration escaped its reviewed snapshot path.'
    }
    return $authority
}

function Invoke-BoundedProcess(
    [string]$Executable,
    [string[]]$Arguments,
    [int]$TimeoutSeconds,
    [string]$WorkingDirectory = $script:repoRoot) {
    if ($TimeoutSeconds -lt 1) { throw 'Bounded process timeout must be positive.' }
    return [Deep.PhysicalE2E.BoundedProcess]::Run(
        $Executable, $Arguments, $WorkingDirectory, $TimeoutSeconds * 1000)
}

function Resolve-PolicyPinnedFile(
    [string]$RelativePath,
    [string]$ExpectedSha256,
    [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or
        $RelativePath -cnotmatch '^[A-Za-z0-9._/-]+$' -or
        $RelativePath.StartsWith('/') -or
        $RelativePath.Contains('\') -or
        @($RelativePath.Split('/') | Where-Object { $_ -ceq '.' -or $_ -ceq '..' }).Count -ne 0 -or
        $ExpectedSha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw "$Label has an invalid signed repository-relative path or SHA-256."
    }
    $root = $repoRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $path = [IO.Path]::GetFullPath((Join-Path $repoRoot $RelativePath))
    if (-not $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $path -PathType Leaf) -or
        ((Get-Item -Force -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        (Get-Sha256 $path) -cne $ExpectedSha256) {
        throw "$Label does not resolve to the exact signed regular file."
    }
    return $path
}

function New-CanonicalAndroidSelectorsJson {
    $roles = @(
        'Startup.Status', 'Startup.RuntimeFailureCode', 'StartupResetLocalStateButton',
        'Welcome.DisplayName', 'Welcome.Create', 'Conversations.Root',
        'PhysicalE2E.RuntimeReadyMarker',
        'Conversations.ProfileSettings', 'Conversations.NewConversationTop',
        'Conversations.ConversationRow', 'Settings.SessionId', 'Settings.Back',
        'StartConversation.NewMessage', 'StartConversation.CreateGroup',
        'StartConversation.AccountId', 'StartConversation.Close',
        'NewConversation.SessionId',
        'NewConversation.DisplayName', 'NewConversation.Start',
        'NewConversation.Error', 'NewConversation.Back', 'Chat.Back', 'Chat.Draft',
        'Chat.Send', 'Chat.MessageBody', 'Chat.MessageBubble', 'Chat.DeliveryStatus', 'Chat.Retry',
        'Chat.Attach', 'Chat.PickFile', 'Chat.PickPhoto', 'Chat.StagedAttachmentFilename',
        'Chat.StagedAttachmentMetadata',
        'Chat.AttachmentFilename', 'Chat.AttachmentMetadata', 'Chat.AttachmentOpen',
        'Chat.AttachmentSave', 'Chat.MessageAttachmentOpen', 'Chat.MessageAttachmentSave',
        'Chat.ImagePreview', 'Chat.ImageMetadata',
        'Chat.Voice', 'Chat.VoicePlayButton', 'PhysicalE2E.VoicePlaybackState',
        'Groups.GroupName', 'Groups.MemberSessionId', 'Groups.AddMember',
        'Groups.DraftMembers', 'Groups.Create',
        'GroupChat.Title', 'GroupChat.Draft', 'GroupChat.Send',
        'GroupChat.MessageBubble', 'GroupChat.MessageBody',
        'GroupChat.DeliveryStatus', 'GroupChat.Error',
        'PhysicalE2E.AckCorrelation',
        'Call.Root', 'Call.Status', 'Call.MediaState', 'Call.Microphone',
        'Call.MicrophoneState', 'Call.Hangup')
    $selectors = [ordered]@{}
    foreach ($role in $roles) {
        $selectors[$role] = "$androidPackage`:id/$role"
    }
    return ($selectors | ConvertTo-Json -Compress)
}

function Assert-ExactPhysicalTestResult([string]$TrxPath) {
    if (-not (Test-Path -LiteralPath $TrxPath -PathType Leaf)) {
        throw 'The physical test did not produce its runner-owned TRX result.'
    }
    [xml]$trx = Get-Content -Raw -LiteralPath $TrxPath
    $counters = $trx.TestRun.ResultSummary.Counters
    if ($null -eq $counters) { throw 'The physical TRX result has no counters.' }
    $total = [int]$counters.total
    $executed = [int]$counters.executed
    $passed = [int]$counters.passed
    $failed = [int]$counters.failed
    $notExecuted = [int]$counters.notExecuted
    if ($total -ne 1 -or $executed -ne 1 -or $passed -ne 1 -or
        $failed -ne 0 -or $notExecuted -ne 0) {
        throw "Physical MAU2 requires exactly one executed pass (total=$total executed=$executed passed=$passed failed=$failed notExecuted=$notExecuted)."
    }
}

function Get-TreeSha256([string]$Root) {
    $canonical = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath $canonical -PathType Container) -or
        ((Get-Item -Force -LiteralPath $canonical).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Tree digest root must be an existing regular directory.'
    }
    $entries = @(Get-ChildItem -LiteralPath $canonical -Recurse -Force)
    if (@($entries | Where-Object {
        ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    }).Count -ne 0) {
        throw 'Tree digest input must not contain reparse points.'
    }
    $paths = [string[]]@(Get-ChildItem -LiteralPath $canonical -File -Recurse -Force |
        ForEach-Object FullName)
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    $lines = foreach ($path in $paths) {
        $file = Get-Item -Force -LiteralPath $path
        $relative = $path.Substring($canonical.Length + 1).Replace('\', '/')
        "$relative`t$($file.Length)`t$(Get-Sha256 $path)"
    }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($lines -join "`n") + "`n")
    $hasher = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($hasher.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    } finally {
        $hasher.Dispose()
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
}

function Resolve-PolicyPinnedDirectory(
    [string]$RelativePath,
    [string]$ExpectedTreeSha256,
    [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or
        $RelativePath -cnotmatch '^[A-Za-z0-9._/-]+$' -or
        $RelativePath.StartsWith('/') -or
        $RelativePath.Contains('\') -or
        @($RelativePath.Split('/') | Where-Object { $_ -ceq '.' -or $_ -ceq '..' }).Count -ne 0 -or
        $ExpectedTreeSha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw "$Label has an invalid signed repository-relative path or tree SHA-256."
    }
    $root = $repoRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $path = [IO.Path]::GetFullPath((Join-Path $repoRoot $RelativePath))
    if (-not $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $path -PathType Container) -or
        (Get-TreeSha256 $path) -cne $ExpectedTreeSha256) {
        throw "$Label does not resolve to the exact signed regular directory tree."
    }
    return $path
}

function Resolve-InstalledWindowsUatPackage([string]$ApprovalPath) {
    $tuple = Get-Content -Raw -LiteralPath $ApprovalPath | ConvertFrom-Json
    Assert-ExactJsonProperties $tuple @(
        'schemaVersion', 'platform', 'applicationIdentity',
        'installedPackageName', 'installedPackageFullName',
        'installedPackageFamilyName', 'publisher',
        'signingCertificateSha256', 'buildArtifactSha256') `
        'Windows UAT approval tuple'
    if ($tuple.schemaVersion -ne 1 -or $tuple.platform -cne 'windows' -or
        $tuple.applicationIdentity -cne 'Deep.Client.Maui.exe' -or
        $tuple.installedPackageName -cne $androidPackage -or
        $tuple.installedPackageFullName -cnotmatch '^[A-Za-z0-9._-]{1,256}$' -or
        $tuple.installedPackageFamilyName -cnotmatch '^[A-Za-z0-9._-]{1,256}$' -or
        [string]::IsNullOrWhiteSpace([string]$tuple.publisher) -or
        ([string]$tuple.publisher).Length -gt 512 -or
        $tuple.signingCertificateSha256 -cnotmatch '^[a-f0-9]{64}$' -or
        $tuple.buildArtifactSha256 -cnotmatch '^[a-f0-9]{64}$') {
        throw 'Windows UAT approval tuple values are invalid.'
    }

    $packages = @(Get-AppxPackage -Name ([string]$tuple.installedPackageName))
    if ($packages.Count -ne 1) {
        throw 'Windows UAT package discovery did not resolve exactly one installed package.'
    }
    $package = $packages[0]
    if ($package.PackageFullName -cne [string]$tuple.installedPackageFullName -or
        $package.PackageFamilyName -cne [string]$tuple.installedPackageFamilyName -or
        $package.Publisher -cne [string]$tuple.publisher -or
        [string]$package.Status -cne 'Ok') {
        throw 'Installed Windows UAT package identity differs from its signed approval tuple.'
    }
    $installRoot = Assert-AbsoluteExisting ([string]$package.InstallLocation) `
        'Installed Windows UAT package root' -Directory
    $executable = Assert-AbsoluteExisting `
        (Join-Path $installRoot ([string]$tuple.applicationIdentity)) `
        'Installed Windows UAT executable'
    Assert-AbsoluteExisting (Join-Path $installRoot 'AppxSignature.p7x') `
        'Installed Windows UAT package signature' | Out-Null
    return [pscustomobject]@{
        Approval = $ApprovalPath
        InstallRoot = $installRoot
        Executable = $executable
        ArtifactSha256 = [string]$tuple.buildArtifactSha256
    }
}

function Set-ProtectedRunItem([string]$Path) {
    $item = Get-Item -Force -LiteralPath $Path
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Refusing to apply a run ACL through a reparse point.'
    }
    $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl = if ($item.PSIsContainer) {
        [Security.AccessControl.DirectorySecurity]::new()
    } else {
        [Security.AccessControl.FileSecurity]::new()
    }
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($owner, [Security.Principal.SecurityIdentifier]'S-1-5-18', [Security.Principal.SecurityIdentifier]'S-1-5-32-544')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, [Security.AccessControl.FileSystemRights]::FullControl, [Security.AccessControl.InheritanceFlags]::None, [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow))
    }
    if ($item.PSIsContainer) {
        $directory = [IO.DirectoryInfo]::new($Path)
        if ($null -ne $directory.PSObject.Methods['SetAccessControl']) {
            $directory.SetAccessControl(
                [Security.AccessControl.DirectorySecurity]$acl)
        } else {
            [IO.FileSystemAclExtensions]::SetAccessControl(
                $directory,
                [Security.AccessControl.DirectorySecurity]$acl)
        }
    } else {
        $file = [IO.FileInfo]::new($Path)
        if ($null -ne $file.PSObject.Methods['SetAccessControl']) {
            $file.SetAccessControl(
                [Security.AccessControl.FileSecurity]$acl)
        } else {
            [IO.FileSystemAclExtensions]::SetAccessControl(
                $file,
                [Security.AccessControl.FileSecurity]$acl)
        }
    }
}

function Assert-NonReparseDirectory([string]$Path, [string]$Label) {
    $item = Get-Item -Force -LiteralPath $Path
    if (-not $item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label must be a regular directory, never a reparse point."
    }
}

function Assert-ExactProtectedRunDirectory([string]$Path, [string]$Label) {
    Assert-NonReparseDirectory $Path $Label
    $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl = Get-Acl -LiteralPath $Path
    $actualOwner = $acl.GetOwner([Security.Principal.SecurityIdentifier])
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($sid in @($owner.Value, 'S-1-5-18', 'S-1-5-32-544')) {
        [void]$expected.Add($sid)
    }
    $rules = @($acl.GetAccessRules($true, $true,
        [Security.Principal.SecurityIdentifier]))
    if (-not $acl.AreAccessRulesProtected -or
        -not $actualOwner.Equals($owner) -or
        $rules.Count -ne 3) {
        throw "$Label does not have the exact protected owner/DACL."
    }
    foreach ($rule in $rules) {
        if ($rule.IsInherited -or
            $rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
            $rule.FileSystemRights -ne [Security.AccessControl.FileSystemRights]::FullControl -or
            $rule.InheritanceFlags -ne [Security.AccessControl.InheritanceFlags]::None -or
            $rule.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None -or
            -not $expected.Remove($rule.IdentityReference.Value)) {
            throw "$Label does not have the exact protected owner/DACL."
        }
    }
    if ($expected.Count -ne 0) {
        throw "$Label does not have the exact protected owner/DACL."
    }
}

function Initialize-ProtectedRunsRoot([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        Assert-NonReparseDirectory $Path 'Physical E2E runs root'
    } else {
        [IO.Directory]::CreateDirectory($Path) | Out-Null
        Assert-NonReparseDirectory $Path 'Physical E2E runs root'
    }
    Set-ProtectedRunItem $Path
    Assert-ExactProtectedRunDirectory $Path 'Physical E2E runs root'
}

function Set-ProtectedRunTree([string]$Path) {
    Set-ProtectedRunItem $Path
    foreach ($item in Get-ChildItem -Force -Recurse -LiteralPath $Path) {
        Set-ProtectedRunItem $item.FullName
    }
}

function Assert-SanitizedState([object]$State) {
    $json = $State | ConvertTo-Json -Depth 6 -Compress
    # PayloadMatrix is a closed phase identifier, not user material. Remove only that
    # exact phase property from the broad content-leak heuristic; every other occurrence
    # of "payload" remains forbidden.
    $inspectionJson = $json -creplace `
        [regex]::Escape('"phase":"PayloadMatrix"'), `
        '"phase":"MatrixPhase"'
    foreach ($forbidden in @('sessionId', 'holder', 'credential', 'capability', 'privateKey', 'seed', 'payload', 'message')) {
        if ($inspectionJson -match [regex]::Escape($forbidden)) { throw 'Run state attempted to contain secret or message material.' }
    }
}

function Assert-DockerHealthy {
    $authority = Get-ChaosExecutionAuthority
    # Status is the supported read-only operation; no raw compose lifecycle command is used here.
    $statusResult = Invoke-BoundedProcess $authority.PowerShell @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $authority.Launcher,
        '-Action', 'Status') 180
    $status = @($statusResult.Output -split "`r?`n")
    if ($statusResult.ExitCode -ne 0 -or
        @($status | Where-Object { [string]$_ -match '(?i)unhealthy|exited|dead' }).Count -ne 0) {
        throw 'Survival Docker environment is not healthy.'
    }
}

function Get-ExactChaosJson([object[]]$Output, [string]$Schema) {
    $prefix = '{"schema":"' + $Schema + '"'
    $matches = @($Output | ForEach-Object { [string]$_ } |
        Where-Object { $_.StartsWith($prefix, [StringComparison]::Ordinal) })
    if ($matches.Count -ne 1) {
        throw "Supported chaos command did not emit one exact $Schema result."
    }
    return ($matches[0] | ConvertFrom-Json)
}

function Invoke-ChaosCommand([string[]]$Arguments, [string]$Schema) {
    $authority = Get-ChaosExecutionAuthority
    $allArguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', $authority.Launcher) + $Arguments
    $result = Invoke-BoundedProcess $authority.PowerShell $allArguments 180
    if ($result.ExitCode -ne 0) { throw 'Supported HTTPS chaos command failed.' }
    return Get-ExactChaosJson @($result.Output -split "`r?`n") $Schema
}

function Assert-ExactJsonProperties([object]$Value, [string[]]$Expected, [string]$Label) {
    $actual = @($Value.PSObject.Properties.Name)
    if ($actual.Count -ne $Expected.Count -or
        @(Compare-Object -ReferenceObject $Expected -DifferenceObject $actual).Count -ne 0) {
        throw "$Label did not contain the exact closed property set."
    }
}

function Assert-ChaosOffBaseline([object]$Status) {
    Assert-ExactJsonProperties $Status @(
        'schema', 'mode', 'running', 'operation', 'fault', 'armed', 'consumed',
        'requestCount', 'operationAttemptCount', 'operationUpstreamDispatchCount',
        'operationUpstreamSuccessCount', 'injectedFaultCount',
        'postDurableResponseDropCount', 'postDurableAckResponseDropCount',
        'preDispatchOutageCount', 'faultWindowStartedUnixMilliseconds',
        'faultWindowDeadlineUnixMilliseconds', 'expiresInSeconds',
        'identifiersIncluded', 'payloadInspected') 'HTTPS chaos status'
    if ($Status.schema -cne 'deep-survival-resend-chaos-status.v2' -or
        $Status.mode -cne 'development-only' -or $Status.running -ne $false -or
        $Status.armed -ne $false -or $Status.consumed -ne $false -or
        $null -ne $Status.operation -or $null -ne $Status.fault -or
        $Status.requestCount -ne 0 -or
        $Status.operationAttemptCount -ne 0 -or
        $Status.operationUpstreamDispatchCount -ne 0 -or
        $Status.operationUpstreamSuccessCount -ne 0 -or
        $Status.injectedFaultCount -ne 0 -or
        $Status.postDurableResponseDropCount -ne 0 -or
        $Status.preDispatchOutageCount -ne 0 -or
        $Status.postDurableAckResponseDropCount -ne 0 -or
        $Status.faultWindowStartedUnixMilliseconds -ne 0 -or
        $Status.faultWindowDeadlineUnixMilliseconds -ne 0 -or
        $Status.expiresInSeconds -ne 0 -or
        $Status.identifiersIncluded -ne $false -or
        $Status.payloadInspected -ne $false) {
        throw 'HTTPS chaos did not return to the exact safe off baseline.'
    }
}

function Assert-ChaosEnd([object]$Result) {
    Assert-ExactJsonProperties $Result @(
        'schema', 'status', 'running', 'armed', 'protectedTokenDeleted') 'HTTPS chaos end'
    if ($Result.schema -cne 'deep-survival-resend-chaos-end.v2' -or
        $Result.status -cne 'ok' -or $Result.running -ne $false -or
        $Result.armed -ne $false -or $Result.protectedTokenDeleted -ne $true) {
        throw 'HTTPS chaos cleanup result is invalid.'
    }
}

function Assert-VerifiedChaosEvidence([string]$Artifacts, [string]$CurrentPhase) {
    $stem = "chaos-$($CurrentPhase.ToLowerInvariant())-status.v2.json"
    $path = Join-Path $Artifacts $stem
    $digestPath = "$path.sha256"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
        -not (Test-Path -LiteralPath $digestPath -PathType Leaf)) {
        throw 'Physical chaos did not produce its status and SHA-256 evidence pair.'
    }
    $digest = (Get-Content -Raw -LiteralPath $digestPath).Trim()
    if ($digest -cnotmatch '^[a-f0-9]{64}$' -or
        (Get-Sha256 $path) -cne $digest) {
        throw 'Physical chaos status evidence SHA-256 verification failed.'
    }
    $status = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
    Assert-ExactJsonProperties $status @(
        'schema', 'mode', 'running', 'operation', 'fault', 'armed', 'consumed',
        'requestCount', 'operationAttemptCount', 'operationUpstreamDispatchCount',
        'operationUpstreamSuccessCount', 'injectedFaultCount',
        'postDurableResponseDropCount', 'postDurableAckResponseDropCount',
        'preDispatchOutageCount', 'faultWindowStartedUnixMilliseconds',
        'faultWindowDeadlineUnixMilliseconds', 'expiresInSeconds',
        'identifiersIncluded', 'payloadInspected') 'Physical chaos status evidence'
    if ($status.schema -cne 'deep-survival-resend-chaos-status.v2' -or
        $status.running -ne $true -or $status.armed -ne $false -or
        $status.consumed -ne $true -or $status.injectedFaultCount -ne 1 -or
        $status.identifiersIncluded -ne $false -or $status.payloadInspected -ne $false) {
        throw 'Physical chaos status evidence is not one exact consumed safe v2 fault.'
    }
}

$bootstrap = Assert-AbsoluteExisting $MailboxBootstrapRoot 'Mailbox bootstrap root' -Directory
if ($Phase -ceq 'NegativeRuntime' -and
    $bootstrap -cne [IO.Path]::GetFullPath(
        'C:\Work\DeepSession\secrets\mailbox-bootstrap')) {
    throw 'NegativeRuntime requires the canonical protected mailbox bootstrap root.'
}
$androidRuntime = Assert-AbsoluteExisting (Join-Path $bootstrap 'runtime\android') 'Android runtime root' -Directory
$windowsRuntime = Assert-AbsoluteExisting (Join-Path $bootstrap 'runtime\windows') 'Windows runtime root' -Directory
$windowsAppData = Assert-AbsoluteExisting (Join-Path $bootstrap 'windows') 'Windows app data root' -Directory
$windowsLiveRuntime = Assert-AbsoluteExisting (Join-Path $windowsAppData 'mailbox-runtime-v1') 'Live Windows runtime' -Directory
$windowsLiveRuntimeHashBefore = Get-TreeSha256 $windowsLiveRuntime
$windowsIssuedRuntimeHash = Get-TreeSha256 $windowsRuntime
if ($windowsLiveRuntimeHashBefore -cne $windowsIssuedRuntimeHash) {
    throw 'Live Windows mailbox runtime does not match the currently issued runtime. Publish it before physical E2E.'
}
$policy = Assert-AbsoluteExisting $policyPath 'Approved Android policy'
if ($MrXPublicKeySha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Mr. X public key hash must be exactly lowercase SHA-256.' }
$runtimeEnvironment = Assert-AbsoluteExisting (Join-Path $repoRoot 'eng\survival.dev.env') 'MAU2 runtime environment'
$runtimeEnvironmentText = Get-Content -Raw -LiteralPath $runtimeEnvironment
if ($runtimeEnvironmentText -cnotmatch '(?m)^DEEP_TRANSPORT_PROTOCOL=authenticated-mau2$' -or
    $runtimeEnvironmentText -cnotmatch '(?m)^DEEP_TRANSPORT_OWNERSHIP=official-managed$') {
    throw 'The physical lane requires the checked-in authenticated MAU2 official-managed runtime profile.'
}
$chaosPhase = $Phase -cin @(
    'PrivacyFallback', 'ManualResendAfterRestart', 'AutomaticRetryAfterRestart', 'AckCrashWindow')
$chaosLauncher = $null
$chaosDependencyTreeSha256 = $null
$chaosExecutionSnapshotSha256 = $null
$chaosOrigin = $null
$chaosSecretDirectory = $null
$uatCaCertificate = $null
$sourceChaosAuthority = Assert-ChaosDependencyAuthority
$chaosDependencyTreeSha256 = $sourceChaosAuthority.DependencyTreeSha256
if ($chaosPhase) {
    $originMatches = [regex]::Matches(
        $runtimeEnvironmentText,
        '(?m)^XNODE_URLS=[0-9a-f]{64}\|(?<origin>https://(?<host>[0-9]{1,3}(?:\.[0-9]{1,3}){3}):41801);')
    $address = $null
    if ($originMatches.Count -ne 1 -or
        -not [Net.IPAddress]::TryParse(
            $originMatches[0].Groups['host'].Value, [ref]$address) -or
        $address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or
        $address.Equals([Net.IPAddress]::Any)) {
        throw 'Physical HTTPS chaos could not bind the exact :41801 profile origin.'
    }
    $chaosOrigin = $originMatches[0].Groups['origin'].Value
    $chaosSecretDirectory = Assert-AbsoluteExisting `
        'C:\Work\DeepSession\secrets\survival-uat-tls' 'UAT TLS secret directory' -Directory
    $uatCaCertificate = Assert-AbsoluteExisting `
        (Join-Path $chaosSecretDirectory 'ca.crt') 'UAT TLS CA certificate'
    $env:SURVIVAL_UAT_TLS_SECRET_DIR = $chaosSecretDirectory
}
$sourceCommit = @(& git -C $repoRoot rev-parse HEAD)
if ($LASTEXITCODE -ne 0 -or $sourceCommit.Count -ne 1 -or $sourceCommit[0].Trim() -cnotmatch '^[0-9a-f]{40}$') {
    throw 'The MAUI checkout must resolve to one canonical commit.'
}
$sourceCommit = $sourceCommit[0].Trim()
$worktreeState = @(& git -C $repoRoot status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or $worktreeState.Count -ne 0) {
    throw 'Physical commit-bound evidence requires a clean MAUI worktree, including untracked files.'
}
$policyObject = Get-Content -Raw -LiteralPath $policy | ConvertFrom-Json
if ($policyObject.sourceCommitSha -notmatch '^[0-9a-f]{40}$' -or
    $policyObject.sourceCommitSha -cne $sourceCommit -or
    $policyObject.application.packageId -cne $androidPackage -or
    $policyObject.signature.publicKeySha256 -cne $MrXPublicKeySha256) {
    throw 'Approved lab policy does not bind this exact DEV lane.'
}
$approvedApk = Resolve-PolicyPinnedFile $policyObject.application.apkRelativePath `
    $policyObject.application.apkSha256 'Approved Android APK'
$approvedAdb = Resolve-PolicyPinnedFile $policyObject.tools.adb.relativePath `
    $policyObject.tools.adb.sha256 'Approved ADB'
$approvedAapt = Resolve-PolicyPinnedFile $policyObject.tools.aapt.relativePath `
    $policyObject.tools.aapt.sha256 'Approved AAPT'
$approvedApksigner = Resolve-PolicyPinnedFile $policyObject.tools.apksigner.relativePath `
    $policyObject.tools.apksigner.sha256 'Approved APK signer'
$installedWindowsUat = $null
if ([string]::IsNullOrWhiteSpace($WindowsUatApprovalTuplePath)) {
    $approvedWindowsExe = Resolve-PolicyPinnedFile `
        $policyObject.crossPlatform.windowsExecutableRelativePath `
        $policyObject.crossPlatform.windowsExecutableSha256 `
        'Approved Windows executable'
    $approvedWindowsOutputDirectory = Resolve-PolicyPinnedDirectory `
        $policyObject.crossPlatform.windowsOutputDirectoryRelativePath `
        $policyObject.crossPlatform.windowsOutputTreeSha256 `
        'Approved Windows output directory'
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
            (Split-Path -Parent $approvedWindowsExe),
            $approvedWindowsOutputDirectory)) {
        throw 'Approved Windows executable must be an immediate child of the signed output tree.'
    }
} else {
    if ($null -eq $policyObject.crossPlatform.windowsUatApprovalRelativePath -or
        $null -eq $policyObject.crossPlatform.windowsUatApprovalSha256) {
        throw 'Signed Android lab policy does not bind a Windows UAT approval tuple.'
    }
    $approvedWindowsUatTuple = Resolve-PolicyPinnedFile `
        $policyObject.crossPlatform.windowsUatApprovalRelativePath `
        $policyObject.crossPlatform.windowsUatApprovalSha256 `
        'Approved Windows UAT tuple'
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
            [IO.Path]::GetFullPath($WindowsUatApprovalTuplePath),
            $approvedWindowsUatTuple)) {
        throw 'Caller-selected Windows UAT tuple differs from the signed policy binding.'
    }
    $installedWindowsUat = Resolve-InstalledWindowsUatPackage $approvedWindowsUatTuple
    $approvedWindowsExe = $installedWindowsUat.Executable
    $approvedWindowsOutputDirectory = $installedWindowsUat.InstallRoot
}
$adb = $approvedAdb
$policySha256 = Get-Sha256 $policy
$releaseInvocationId = [Guid]::NewGuid().ToString('N')

$runId = [Guid]::NewGuid().ToString('N')
$e2eRunsRoot = Join-Path $bootstrap 'e2e-runs'
Initialize-ProtectedRunsRoot $e2eRunsRoot
$runRoot = Join-Path $e2eRunsRoot $runId
[IO.Directory]::CreateDirectory($runRoot) | Out-Null
Set-ProtectedRunTree $runRoot
$script:chaosExecutionAuthority = New-ChaosDependencySnapshot `
    $sourceChaosAuthority (Join-Path $runRoot 'chaos-authority')
$chaosLauncher = $script:chaosExecutionAuthority.Launcher
$chaosManifestPath = $script:chaosExecutionAuthority.Manifest
$chaosExecutionSnapshotSha256 = $script:chaosExecutionAuthority.SnapshotSha256
$env:DEEP_PHYSICAL_E2E_DOCKER_PATH = $script:chaosExecutionAuthority.Docker
$env:DEEP_PHYSICAL_E2E_DOCKER_SHA256 = $script:chaosExecutionAuthority.DockerSha256
$env:DEEP_PHYSICAL_E2E_DOCKER_COMPOSE_PATH = $script:chaosExecutionAuthority.DockerCompose
$env:DEEP_PHYSICAL_E2E_DOCKER_COMPOSE_SHA256 = $script:chaosExecutionAuthority.DockerComposeSha256
$env:DEEP_PHYSICAL_E2E_HAPROXY_CONFIG_PATH = $script:chaosExecutionAuthority.HAProxyConfig
$env:DEEP_PHYSICAL_E2E_DEVOPS_RUNTIME_ROOT = $sourceChaosAuthority.DevOpsRoot
if ($chaosPhase) {
    $devOpsRoot = $script:chaosExecutionAuthority.DevOpsRoot
    Assert-ChaosOffBaseline (Invoke-ChaosCommand @('-Action', 'ChaosStatus') `
        'deep-survival-resend-chaos-status.v2')
}
$runStatePath = Join-Path $runRoot 'run-state.json'
$artifacts = Join-Path $runRoot 'artifacts'
[IO.Directory]::CreateDirectory($artifacts) | Out-Null
$genericFixture = Join-Path $runRoot "payload-$runId-generic.txt"
$documentFixture = Join-Path $runRoot "payload-$runId-document.pdf"
$imageFixture = Join-Path $runRoot "payload-$runId-image.png"
[IO.File]::WriteAllBytes(
    $genericFixture,
    [Text.UTF8Encoding]::new($false).GetBytes("deep-payload-generic-v1`n0123456789abcdef`n"))
[IO.File]::WriteAllBytes(
    $documentFixture,
    [Text.ASCIIEncoding]::new().GetBytes("%PDF-1.4`n% Deep deterministic physical UAT document v1`n%%EOF`n"))
[IO.File]::WriteAllBytes(
    $imageFixture,
    [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII='))
Set-ProtectedRunTree $runRoot

$productionBefore = Get-PackageSnapshot $productionPackage
$failures = [Collections.Generic.List[Exception]]::new()
$chaosCleanupRequired = $false
try {
    Invoke-AdbQuiet @('start-server') | Out-Null
    $devices = @(& $adb devices | Select-Object -Skip 1 | Where-Object { $_ -ceq "$AndroidSerial`tdevice" })
    if ($devices.Count -ne 1) { throw 'The exact Wi-Fi Android device is not attached.' }
    Get-PackageSnapshot $androidPackage | Out-Null
    Assert-DockerHealthy

    $state = [ordered]@{
        schema = 'deep.mau2-physical-run-state.v1'
        runId = $runId
        phase = $Phase
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        status = 'prepared'
        sourceCommit = $sourceCommit
        policySha256 = $policySha256
        runtimeEnvironmentSha256 = Get-Sha256 $runtimeEnvironment
        chaosDependencyManifestSha256 = $chaosManifestSha256
        chaosDependencyTreeSha256 = $chaosDependencyTreeSha256
        chaosExecutionSnapshotSha256 = $chaosExecutionSnapshotSha256
        transportProtocol = 'authenticated-mau2'
        transportOwnership = 'official-managed'
        androidRuntimeTreeSha256 = (Get-Sha256 (Join-Path $androidRuntime 'activation.v1.json'))
        windowsRuntimeTreeSha256 = (Get-Sha256 (Join-Path $windowsRuntime 'activation.v1.json'))
        windowsRootPresent = $true
        androidE2eLocalResetAuthorized = [bool]$ResetAndroidE2eLocalState
        windowsUatLocalResetAuthorized = [bool]$ResetWindowsUatLocalState
        dockerHealthy = $true
        productionPackageUntouched = $true
        storage = [ordered]@{ replication = 'shared-dev-storage-non-replicated'; before = $null; after = $null }
    }
    Assert-SanitizedState $state
    [IO.File]::WriteAllText(
        $runStatePath,
        ($state | ConvertTo-Json -Depth 6),
        [Text.UTF8Encoding]::new($false))
    Set-ProtectedRunTree $runRoot

    if ($Execute) {
        $env:DEEP_STRICT_CROSS_PLATFORM_UI = '1'
        $env:DEEP_STRICT_WINDOWS_UI = '1'
        $env:DEEP_MAU2_E2E_PHASE = $Phase
        if ($ResetWindowsUatLocalState) {
            $env:DEEP_MAU2_E2E_UAT_RESET_BINDING =
                "${policySha256}:$releaseInvocationId"
        } else {
            $env:DEEP_MAU2_E2E_UAT_RESET_BINDING = $null
        }
        if ($ResetAndroidE2eLocalState) {
            $env:DEEP_MAU2_E2E_ANDROID_RESET_BINDING =
                "android-e2e-local-reset-v1:${policySha256}:$releaseInvocationId"
        } else {
            $env:DEEP_MAU2_E2E_ANDROID_RESET_BINDING = $null
        }
        $env:DEEP_MAU2_E2E_RUN_STATE = $runStatePath
        $env:DEEP_MAU2_E2E_RUNS_ROOT = (Join-Path $bootstrap 'e2e-runs')
        $env:DEEP_E2E_ANDROID_SERIAL = $AndroidSerial
        $env:DEEP_E2E_ADB = $approvedAdb
        $env:DEEP_E2E_ANDROID_APK = $approvedApk
        $env:DEEP_E2E_AAPT = $approvedAapt
        $env:DEEP_E2E_APKSIGNER = $approvedApksigner
        $env:DEEP_E2E_GENERIC_FIXTURE = $genericFixture
        $env:DEEP_E2E_DOCUMENT_FIXTURE = $documentFixture
        $env:DEEP_E2E_IMAGE_FIXTURE = $imageFixture
        $env:DEEP_E2E_ANDROID_POLICY = $policy
        $env:DEEP_E2E_REPOSITORY_ROOT = $repoRoot
        $env:DEEP_MR_X_PUBLIC_KEY_SHA256 = $MrXPublicKeySha256
        $env:DEEP_E2E_ARTIFACTS = $artifacts
        $env:DEEP_E2E_ANDROID_SELECTORS_JSON = New-CanonicalAndroidSelectorsJson
        $env:DEEP_E2E_ANDROID_PICKER_FILE_ID = $AndroidPickerFileId
        $env:DEEP_E2E_ANDROID_PICKER_CONFIRM_ID = $AndroidPickerConfirmId
        $env:DEEP_MAUI_EXE = $approvedWindowsExe
        if ($null -ne $installedWindowsUat) {
            $env:DEEP_E2E_WINDOWS_UAT_APPROVAL = $installedWindowsUat.Approval
            $env:DEEP_E2E_WINDOWS_UAT_INSTALL_ROOT = $installedWindowsUat.InstallRoot
        } else {
            $env:DEEP_E2E_WINDOWS_UAT_APPROVAL = $null
            $env:DEEP_E2E_WINDOWS_UAT_INSTALL_ROOT = $null
        }
        $env:DEEP_E2E_APPDATA_ROOT = $windowsAppData
        $env:DEEP_E2E_BOOTSTRAP = 'live'
        $env:DEEP_RELEASE_INVOCATION_ID = $releaseInvocationId
        $env:DEEP_TRANSPORT_PROTOCOL = 'authenticated-mau2'
        $env:DEEP_TRANSPORT_OWNERSHIP = 'official-managed'
        $env:DEEP_STORAGE_URL = $null
        if ($chaosPhase) {
            $env:DEEP_E2E_CHAOS_DEVOPS_ROOT = $devOpsRoot
            $env:DEEP_E2E_CHAOS_MANIFEST = $chaosManifestPath
            $env:DEEP_E2E_CHAOS_SNAPSHOT_SHA256 = $chaosExecutionSnapshotSha256
            $env:DEEP_E2E_CHAOS_HTTPS_ORIGIN = $chaosOrigin
            $env:DEEP_E2E_UAT_CA_CERTIFICATE = $uatCaCertificate
            $env:SURVIVAL_UAT_TLS_SECRET_DIR = $chaosSecretDirectory
        }
        $negativeGenerator = Join-Path $devOpsRoot 'scripts\survival-dev-mailbox-negative-runtime.ps1'
        $negativePrepared = $false
        if ($Phase -ceq 'NegativeRuntime') {
            Assert-AbsoluteExisting $negativeGenerator 'Negative runtime generator' | Out-Null
            & powershell -NoProfile -ExecutionPolicy Bypass -File $negativeGenerator `
                -Action Generate -RunRoot $runRoot
            if ($LASTEXITCODE -ne 0) { throw 'Negative runtime fixture generation failed.' }
            $negativePrepared = $true
        }
        # The test itself rechecks its policy/tool/APK pins. This wrapper never emits
        # their paths, holders, sessions, message markers, or native/container logs.
        try {
            $trxPath = Join-Path $artifacts 'physical-phase.trx'
            if ($chaosPhase) { $chaosCleanupRequired = $true }
            $authority = Get-ChaosExecutionAuthority
            $testArguments = @(
                'test',
                (Join-Path $repoRoot 'tests\Deep.Client.Maui.UiTests\Deep.Client.Maui.UiTests.csproj'),
                '--no-restore',
                '--results-directory', $artifacts,
                '--logger', 'trx;LogFileName=physical-phase.trx',
                '--filter', 'FullyQualifiedName=Deep.Client.Maui.UiTests.StrictCrossPlatformUiTests.Physical_android_and_windows_exchange_persist_and_decrypt_an_attachment')
            $testResult = Invoke-BoundedProcess $authority.DotNet $testArguments 1800 $repoRoot
            if ($testResult.ExitCode -ne 0) { throw 'Physical MAU2 UI phase failed.' }
            Assert-ExactPhysicalTestResult $trxPath
            if ($chaosPhase) {
                Assert-VerifiedChaosEvidence $artifacts $Phase
            }
        } finally {
            if ($negativePrepared) {
                & powershell -NoProfile -ExecutionPolicy Bypass -File $negativeGenerator `
                    -Action Cleanup -RunRoot $runRoot | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Negative runtime fixture cleanup failed.' }
            }
        }
    }
} catch {
    $failures.Add($_.Exception)
} finally {
    if ($chaosCleanupRequired) {
        $endComplete = $false
        foreach ($attempt in 1..3) {
            try {
                Assert-ChaosEnd (Invoke-ChaosCommand @('-Action', 'ChaosEnd') `
                    'deep-survival-resend-chaos-end.v2')
                $endComplete = $true
                break
            } catch {
                $failures.Add($_.Exception)
            }
        }
        try {
            Assert-ChaosOffBaseline (Invoke-ChaosCommand @('-Action', 'ChaosStatus') `
                'deep-survival-resend-chaos-status.v2')
        } catch {
            $failures.Add($_.Exception)
        }
        if (-not $endComplete) {
            $failures.Add([InvalidOperationException]::new(
                'HTTPS chaos cleanup exhausted its bounded retries.'))
        }
    }
    try {
        $productionAfter = Get-PackageSnapshot $productionPackage
        if ($productionBefore -cne $productionAfter) {
            throw 'Production Android package changed during MAU2 E2E.'
        }
    } catch {
        $failures.Add($_.Exception)
    }
    try {
        if ((Get-TreeSha256 $windowsLiveRuntime) -cne $windowsLiveRuntimeHashBefore) {
            throw 'Canonical live Windows runtime changed during MAU2 E2E.'
        }
    } catch {
        $failures.Add($_.Exception)
    }
    try {
        Close-ChaosDependencySnapshot $script:chaosExecutionAuthority
    } catch {
        $failures.Add($_.Exception)
    }
    $env:DEEP_MAU2_E2E_UAT_RESET_BINDING = $null
    $env:DEEP_MAU2_E2E_ANDROID_RESET_BINDING = $null
    $env:DEEP_E2E_UAT_CA_CERTIFICATE = $null
    $env:DEEP_E2E_WINDOWS_UAT_APPROVAL = $null
    $env:DEEP_E2E_WINDOWS_UAT_INSTALL_ROOT = $null
    $env:DEEP_PHYSICAL_E2E_HAPROXY_CONFIG_PATH = $null
}

if ($failures.Count -ne 0) {
    throw [AggregateException]::new(
        'Physical MAU2 phase failed with audited cleanup results.', $failures)
}

if ($Execute) {
    Write-Output "Physical MAU2 phase '$Phase' completed without skipped tests. Sanitized protected run state: $runId"
} else {
    Write-Output "Physical MAU2 phase '$Phase' preflight completed; UI test was not executed. Sanitized protected run state: $runId"
}
