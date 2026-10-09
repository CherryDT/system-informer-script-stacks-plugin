[CmdletBinding()]
param([ValidateSet('Release','Debug')][string]$Configuration='Release', [switch]$SkipSdk, [switch]$SkipHelper,
      [string]$BuildRoot="$env:LOCALAPPDATA\ScriptStacks\build")
$ErrorActionPreference='Stop'
$Root=Split-Path $PSScriptRoot -Parent
$VsWhere="${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$Vs=& $VsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $Vs) { throw 'Install Visual Studio 2022 with Desktop development with C++.' }
if (-not $SkipSdk -or -not(Test-Path "$Root\.deps\sdk\include\phdk.h")) { & "$PSScriptRoot\prepare-sdk.ps1" -Root $Root -VisualStudio $Vs }
# MSBuild normalizes dependency paths to lowercase. A case-sensitive checkout
# breaks its tracking even on NTFS, so stage with Windows tools in LocalAppData.
$Stage=Join-Path $BuildRoot 'source'
$Binary=Join-Path $BuildRoot 'native'
foreach($Directory in @('plugin','vendor','.deps/sdk')) {
    $Target=Join-Path $Stage $Directory
    New-Item -ItemType Directory -Force $Target | Out-Null
    & robocopy.exe (Join-Path $Root $Directory) $Target /MIR /IS /IT /NFL /NDL /NJH /NJS /NP
    if ($LASTEXITCODE -gt 7) { throw "Could not stage $Directory" }
}
Copy-Item "$Root\CMakeLists.txt" "$Stage\CMakeLists.txt" -Force
$CMake="$Vs\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
& $CMake -S $Stage -B $Binary -G 'Visual Studio 17 2022' -A x64
if ($LASTEXITCODE) { throw 'CMake configuration failed.' }
& $CMake --build $Binary --config $Configuration --parallel
if ($LASTEXITCODE) { throw 'Plugin build failed.' }
New-Item -ItemType Directory -Force "$Root\dist" | Out-Null
foreach($Name in @('ScriptStacks.dll','ScriptStacks.pdb')) { Copy-Item "$Stage\dist\$Configuration\$Name" "$Root\dist\$Name" -Force }
if (-not $SkipHelper) {
    $Dotnet="$env:ProgramFiles\dotnet\dotnet.exe"
    if (-not(Test-Path $Dotnet)) { throw 'Install the .NET 9 SDK to build the self-contained helper.' }
    & $Dotnet publish "$Root\helper\ScriptStacks.Host.csproj" -c $Configuration -r win-x64 --self-contained true -o "$Root\dist\helpers\x64"
    if ($LASTEXITCODE) { throw 'Helper build failed.' }
}
# Distribute the licenses from the exact runtime package resolved by restore.
$Assets=Get-Content "$Root\helper\obj\project.assets.json" -Raw | ConvertFrom-Json
$Runtime=$Assets.project.frameworks.PSObject.Properties.Value.downloadDependencies | Where-Object name -eq 'Microsoft.NETCore.App.Runtime.win-x64' | Select-Object -First 1
if (-not $Runtime) { throw 'Cannot identify the bundled runtime license package.' }
$Cache=$Assets.packageFolders.PSObject.Properties.Name | Select-Object -First 1
$Version=($Runtime.version -split ',')[0].Trim('[] ')
$RuntimeDirectory=Join-Path $Cache ('microsoft.netcore.app.runtime.win-x64/' + $Version)
foreach($Name in @('LICENSE.TXT','THIRD-PARTY-NOTICES.TXT')) {
    Copy-Item (Join-Path $RuntimeDirectory $Name) "$Root\dist\helpers\x64\$Name" -Force
}
Write-Host 'Build complete. Run scripts/package.ps1 to create the installer and portable ZIP.'
