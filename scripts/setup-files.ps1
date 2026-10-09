# Adapted from WSL Tools for System Informer; MIT, Copyright (c) 2026 David Trapp / Trapp Innovations.
# Internal file phase; only this process may request elevation for plugin files.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('Install', 'Uninstall')] [string] $Mode,
    [Parameter(Mandatory)] [string] $SystemInformerDirectory,
    [string] $DistributionDirectory
)
$ErrorActionPreference = 'Stop'
try {
    . (Join-Path $PSScriptRoot 'setup-common.ps1')
    $SystemInformerDirectory = [IO.Path]::GetFullPath($SystemInformerDirectory)
    if (-not [IO.File]::Exists((Join-Path $SystemInformerDirectory 'SystemInformer.exe'))) {
        throw 'The destination does not contain SystemInformer.exe.'
    }
    Assert-SystemInformerClosed $SystemInformerDirectory
    $Plugins = Join-Path $SystemInformerDirectory 'plugins'
    $Helpers = Join-Path $Plugins 'ScriptStacks/helpers'
    if ($Mode -eq 'Install') {
        foreach ($Required in @('ScriptStacks.dll', 'ScriptStacks/helpers/x64/ScriptStacks.Host.exe')) {
            if (-not [IO.File]::Exists((Join-Path $DistributionDirectory $Required))) { throw "Missing $Required" }
        }
        [void] [IO.Directory]::CreateDirectory($Plugins)
        [IO.File]::Copy((Join-Path $DistributionDirectory 'ScriptStacks.dll'), (Join-Path $Plugins 'ScriptStacks.dll'), $true)
        $SourceRoot = Join-Path $DistributionDirectory 'ScriptStacks/helpers'
        foreach ($File in [IO.Directory]::GetFiles($SourceRoot, '*', [IO.SearchOption]::AllDirectories)) {
            $Relative = $File.Substring($SourceRoot.Length).TrimStart([char[]]'\/')
            $Target = Join-Path $Helpers $Relative
            [void] [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Target))
            [IO.File]::Copy($File, $Target, $true)
        }
    } else {
        $Plugin = Join-Path $Plugins 'ScriptStacks.dll'
        if ([IO.File]::Exists($Plugin)) { [IO.File]::Delete($Plugin) }
        # Remove only this product's helper payload; retain sibling plugins and private downloads.
        if ([IO.Directory]::Exists($Helpers)) { [IO.Directory]::Delete($Helpers, $true) }
        $ProductDirectory = Join-Path $Plugins 'ScriptStacks'
        if ([IO.Directory]::Exists($ProductDirectory) -and [IO.Directory]::GetFileSystemEntries($ProductDirectory).Length -eq 0) {
            [IO.Directory]::Delete($ProductDirectory)
        }
    }
    exit 0
} catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
