param([switch]$Editor, [switch]$Balanced)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$env:DOTNET_ROOT = Join-Path $projectRoot '.tools\dotnet'
$env:PATH = $env:DOTNET_ROOT + ';' + $env:PATH
$godotPath = Join-Path $projectRoot '.tools\godot\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64.exe'
$projectFile = Join-Path $projectRoot 'project.godot'
if (-not (Test-Path -LiteralPath $godotPath)) { throw 'Local Godot .NET editor is missing. See README.md for setup.' }
if ($Editor) {
    Start-Process -FilePath $godotPath -ArgumentList @('--editor', '--path', ('"' + $projectRoot + '"')) -WorkingDirectory $projectRoot
} else {
    $launchArguments = @('--path', ('"' + $projectRoot + '"'))
    if ($Balanced) { $launchArguments += @('--', '--balanced') }
    Start-Process -FilePath $godotPath -ArgumentList $launchArguments -WorkingDirectory $projectRoot
}
