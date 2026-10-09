# Adapted from WSL Tools for System Informer; MIT, Copyright (c) 2026 David Trapp / Trapp Innovations.
# Installer support; keep settings operations in the original Windows user's context.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-SetupProgress([string] $Text, $ProgressQueue) {
    if ($ProgressQueue -is [string]) {
        # Inno Setup reads the worker log as UTF-16, including its BOM.
        # The wizard may briefly hold the file while refreshing its memo.
        for ($Attempt = 0; ; ++$Attempt) {
            try {
                [IO.File]::AppendAllText($ProgressQueue, $Text + [Environment]::NewLine, [Text.Encoding]::Unicode)
                break
            } catch [IO.IOException] {
                if ($Attempt -ge 4) { throw }
                Start-Sleep -Milliseconds 20
            }
        }
    }
    elseif ($null -ne $ProgressQueue) { $ProgressQueue.Enqueue($Text) }
    else { Write-Host $Text }
}

function Test-SetupCancelled($Cancellation) {
    $Cancelled = if ($Cancellation -is [string]) { [IO.File]::Exists($Cancellation) }
                 else { $null -ne $Cancellation -and $Cancellation.IsSet }
    if ($Cancelled) { throw 'Cancelled. Completed changes have been kept.' }
}

function ConvertTo-NativeArgument([string] $Value) {
    # Keep simple command-line arguments unquoted.
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }
    # Windows PowerShell 5.1 lacks ProcessStartInfo.ArgumentList. Apply the
    # CommandLineToArgvW quoting rules, including backslashes before quotes.
    return '"' + [regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}

function Invoke-WindowsFileAction([string] $Mode, [string] $Directory, [string] $DistributionDirectory, $ProgressQueue) {
    $Directory = [IO.Path]::GetFullPath($Directory)
    if (-not (Test-Path -LiteralPath (Join-Path $Directory 'SystemInformer.exe'))) {
        throw "Not a System Informer installation: $Directory"
    }
    # The child repeats the check, since a process could start after this one.
    Assert-SystemInformerClosed $Directory
    $Arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
        (Join-Path $PSScriptRoot 'setup-files.ps1'), '-Mode', $Mode,
        '-SystemInformerDirectory', $Directory)
    if ($Mode -eq 'Install') { $Arguments += @('-DistributionDirectory', $DistributionDirectory) }
    $Start = New-Object Diagnostics.ProcessStartInfo
    $Start.FileName = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $Start.Arguments = ($Arguments | ForEach-Object { ConvertTo-NativeArgument $_ }) -join ' '
    $Start.UseShellExecute = $true
    $Start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    # Probe only our destination directory. Keep the wizard in the original user context.
    $ProbeDirectory = Join-Path $Directory 'plugins'
    if (-not [IO.Directory]::Exists($ProbeDirectory)) { $ProbeDirectory = $Directory }
    $Probe = Join-Path $ProbeDirectory ('.script-stacks-access-' + [guid]::NewGuid().ToString('N'))
    try { $Stream = [IO.File]::Open($Probe, 'CreateNew', 'Write', 'None'); $Stream.Dispose(); [IO.File]::Delete($Probe) }
    catch [UnauthorizedAccessException] {
        $Start.Verb = 'runas'
        Write-SetupProgress 'Windows administrator permission is needed to change the plugin files.' $ProgressQueue
    }
    $Process = [Diagnostics.Process]::Start($Start)
    try {
        # This finite file-copy phase is not cancelled halfway through. It runs
        # on the wizard worker, so a UAC prompt never blocks window repainting.
        $Process.WaitForExit()
        if ($Process.ExitCode -ne 0) {
            throw "Windows file operation failed (code $($Process.ExitCode)). Close System Informer and retry; check access to the selected folder and release files."
        }
    } finally { $Process.Dispose() }
    Write-SetupProgress "Windows plugin files: $($Mode.ToLowerInvariant()) completed." $ProgressQueue
}

function Assert-SystemInformerClosed([string] $Directory) {
    if (-not ('ScriptStacksSetup.NativeProcess' -as [type])) {
        # QueryLimitedInformation works across elevation without requesting the
        # memory-reading access used by Process.MainModule.
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
namespace ScriptStacksSetup {
    public static class NativeProcess {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        public static string Path(int pid) {
            IntPtr handle = OpenProcess(0x1000, false, pid);
            if (handle == IntPtr.Zero) throw new Win32Exception();
            try {
                uint size = 32768;
                var path = new StringBuilder((int)size);
                if (!QueryFullProcessImageName(handle, 0, path, ref size)) throw new Win32Exception();
                return path.ToString();
            } finally { CloseHandle(handle); }
        }
    }
}
'@
    }
    $Expected = [IO.Path]::GetFullPath((Join-Path $Directory 'SystemInformer.exe'))
    foreach ($Process in @(Get-Process -Name SystemInformer -ErrorAction SilentlyContinue)) {
        try { $Path = [ScriptStacksSetup.NativeProcess]::Path($Process.Id) }
        catch { throw 'A System Informer instance cannot be inspected. Close it before installing or uninstalling this plugin.' }
        if (-not $Path) { throw 'Close System Informer before installing or uninstalling this plugin.' }
        if ([string]::Equals($Path, $Expected, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Close System Informer in the selected folder, then retry. The installer will not close it for you.'
        }
    }
}
