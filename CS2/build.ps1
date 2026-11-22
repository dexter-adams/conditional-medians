param(
    # The game folder: the modding toolchain's CSII_INSTALLATIONPATH when the
    # game has set it up, else Steam's default library.
    [string]$Game = $(if ($env:CSII_INSTALLATIONPATH) { $env:CSII_INSTALLATIONPATH } else { 'C:\Program Files (x86)\Steam\steamapps\common\Cities Skylines II' }),
    # node_modules of a Cities: Skylines II UI template, used when CS2\UI has none of its own.
    [string]$UINodeModules = $env:MEDIANS_NODE_MODULES
)
$ErrorActionPreference = 'Stop'
$managed = Join-Path $Game 'Cities2_Data\Managed'
$compiler = Get-ChildItem "$env:ProgramFiles\dotnet\sdk\*\Roslyn\bincore\csc.dll" |
    Sort-Object { [version](($_.Directory.Parent.Parent.Name -split '-')[0]) } -Descending | Select-Object -First 1
if (!$compiler) { throw 'Install a .NET SDK with the Roslyn C# compiler.' }
$names = @('mscorlib', 'netstandard', 'System', 'System.Core', 'Game',
    'Colossal.Core', 'Colossal.IO', 'Colossal.Logging', 'Colossal.Mathematics', 'Colossal.UI.Binding',
    'Colossal.IO.AssetDatabase', 'Unity.Entities', 'Unity.Collections',
    'Unity.Mathematics', 'Unity.Mathematics.Extensions', 'Unity.InputSystem',
    'UnityEngine.CoreModule', 'UnityEngine.PhysicsModule', 'UnityEngine.IMGUIModule', 'UnityEngine.InputLegacyModule')
$references = $names | ForEach-Object { "/reference:$(Join-Path $managed "$_.dll")" }
$output = New-Item -ItemType Directory -Force (Join-Path $PSScriptRoot '..\dist\CS2')
$source = Get-ChildItem (Join-Path $PSScriptRoot 'Source') -Filter '*.cs' | ForEach-Object FullName
& dotnet $compiler.FullName /nologo /noconfig /nostdlib+ /target:library /optimize+ "/out:$($output.FullName)\ConditionalMedians2.dll" $references $source
if ($LASTEXITCODE -ne 0) { throw 'Cities: Skylines II build failed.' }

# The game UI module (the tool's button and panel): ConditionalMedians2.mjs and
# .css, which the game loads from the mod's folder beside the DLL.
$ui = Join-Path $PSScriptRoot 'UI'
$local = Join-Path $ui 'node_modules'
$driveway = Join-Path $PSScriptRoot '..\..\Driveway Entries\DrivewayEntries\UI\node_modules'
$modules = if (Test-Path $local) { $local } elseif ($UINodeModules) { $UINodeModules } elseif (Test-Path $driveway) { (Resolve-Path $driveway).Path } else { $null }
if (!$modules -or !(Test-Path (Join-Path $modules 'webpack-cli\bin\cli.js'))) {
    throw 'The UI module needs its packages: run npm ci in CS2\UI, or pass -UINodeModules with a Cities: Skylines II UI template node_modules folder.'
}
$env:MEDIANS_NODE_MODULES = $modules
Push-Location $ui
try {
    & node (Join-Path $modules 'webpack-cli\bin\cli.js') --config webpack.config.js
    if ($LASTEXITCODE -ne 0) { throw 'UI module build failed.' }
}
finally { Pop-Location }
foreach ($file in 'ConditionalMedians2.mjs', 'ConditionalMedians2.css', 'ConditionalMedians2.mjs.LICENSE.txt') {
    Copy-Item (Join-Path $ui "dist\$file") $output.FullName -Force
}
