# Prepare the portable ZIP and Inno Setup package from existing build output.
[CmdletBinding()]
param(
    [switch] $SkipInstaller,
    [string] $InnoCompiler
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Root = Split-Path $PSScriptRoot -Parent
$Dist = Join-Path $Root 'dist'
foreach ($Relative in @('ScriptStacks.dll', 'helpers/x64/ScriptStacks.Host.exe')) {
    if (-not [IO.File]::Exists((Join-Path $Dist $Relative))) {
        throw "Missing dist/$Relative. Run scripts/build.ps1 first."
    }
}
foreach ($Relative in @('README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'vendor/wsl-tools.LICENSE.MIT')) {
    if (-not [IO.File]::Exists((Join-Path $Root $Relative))) { throw "Missing package document: $Relative" }
}
# Fresh local staging avoids duplicate file identifiers on SSHFS checkouts.
$Package = Join-Path "$env:LOCALAPPDATA/SystemInformerScriptStacks/packages" ([guid]::NewGuid().ToString())
$TemporaryArchive = "$Package.zip"
[void] [IO.Directory]::CreateDirectory($Package)
function Copy-PackageFile([string] $Source, [string] $Relative) {
    $Destination = Join-Path $Package $Relative
    [void] [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Destination))
    [IO.File]::Copy($Source, $Destination, $true)
}
function Copy-PackageTree([string] $Source, [string] $Relative) {
    if (-not [IO.Directory]::Exists($Source)) { return }
    foreach ($File in [IO.Directory]::GetFiles($Source, '*', [IO.SearchOption]::AllDirectories)) {
        $Tail = $File.Substring($Source.Length).TrimStart([char[]]'\/')
        Copy-PackageFile $File (Join-Path $Relative $Tail)
    }
}
try {
    Copy-PackageFile (Join-Path $Dist 'ScriptStacks.dll') 'ScriptStacks.dll'
    if ([IO.File]::Exists((Join-Path $Dist 'ScriptStacks.pdb'))) {
        Copy-PackageFile (Join-Path $Dist 'ScriptStacks.pdb') 'ScriptStacks.pdb'
    }
    # Include the complete published directory, including native libraries and runtime files.
    # x86 is optional; x64 is required above.
    foreach ($Architecture in @('x64', 'x86')) {
        Copy-PackageTree (Join-Path $Dist "helpers/$Architecture") "ScriptStacks/helpers/$Architecture"
    }
    foreach ($File in @('README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md')) {
        Copy-PackageFile (Join-Path $Root $File) $File
    }
    Copy-PackageFile (Join-Path $Root 'vendor/wsl-tools.LICENSE.MIT') 'licenses/wsl-tools.LICENSE.MIT'
    foreach ($File in [IO.Directory]::GetFiles((Join-Path $Root 'vendor'), '*LICENSE*')) {
        Copy-PackageFile $File (Join-Path 'licenses' ([IO.Path]::GetFileName($File)))
    }
    $SdkLicense = Join-Path $Root '.deps/sdk/LICENSE.txt'
    if ([IO.File]::Exists($SdkLicense)) { Copy-PackageFile $SdkLicense 'licenses/SystemInformer.LICENSE.txt' }
    Copy-PackageTree (Join-Path $Dist 'licenses') 'licenses'
    Copy-PackageTree (Join-Path $Dist 'relink') 'relink'
    # The repair/uninstall worker is included in both distributions.
    foreach ($File in @('setup-common.ps1', 'setup-files.ps1', 'setup-worker.ps1', 'setup-settings.ps1')) {
        Copy-PackageFile (Join-Path $PSScriptRoot $File) $File
    }
    $Hashes = foreach ($File in [IO.Directory]::GetFiles($Package, '*', [IO.SearchOption]::AllDirectories) | Sort-Object) {
        $Relative = $File.Substring($Package.Length + 1).Replace('\', '/')
        $Hash = (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash.ToLowerInvariant()
        "$Hash  $Relative"
    }
    [IO.File]::WriteAllLines((Join-Path $Package 'SHA256SUMS.txt'), [string[]]$Hashes, [Text.Encoding]::ASCII)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($Package, $TemporaryArchive)
    $Archive = Join-Path $Dist 'ScriptStacks-0.1.0-windows-x64.zip'
    [IO.File]::Copy($TemporaryArchive, $Archive, $true)
    Write-Host "Packaged $Archive"
    if (-not $SkipInstaller) {
        & (Join-Path $PSScriptRoot 'build-installer.ps1') -PackageDirectory $Package -OutputDirectory $Dist -InnoCompiler $InnoCompiler
    }
} finally {
    # Only the unique paths created by this invocation are removed.
    if ([IO.File]::Exists($TemporaryArchive)) { [IO.File]::Delete($TemporaryArchive) }
    if ([IO.Directory]::Exists($Package)) { [IO.Directory]::Delete($Package, $true) }
}
