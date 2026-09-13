[CmdletBinding()]
param([switch]$SkipCad)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$toolDirectory=Join-Path $projectRoot '.tools'
New-Item -ItemType Directory -Force $toolDirectory | Out-Null
function Download-File([string]$Url,[string]$Destination) {
    if(Test-Path -LiteralPath $Destination){return}
    Write-Host "Downloading $([IO.Path]::GetFileName($Destination))"
    & curl.exe -L --fail --silent --show-error $Url -o $Destination
    if($LASTEXITCODE -ne 0){throw "Download failed: $Url"}
}
$godot=Join-Path $toolDirectory 'godot/Godot_v4.5.1-stable_mono_win64/Godot_v4.5.1-stable_mono_win64_console.exe'
if(!(Test-Path -LiteralPath $godot)){
    $archive=Join-Path $toolDirectory 'godot-mono.zip'
    Download-File 'https://github.com/godotengine/godot/releases/download/4.5.1-stable/Godot_v4.5.1-stable_mono_win64.zip' $archive
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $toolDirectory 'godot') -Force
}
$dotnet=Join-Path $toolDirectory 'dotnet/dotnet.exe'
if(!(Test-Path -LiteralPath $dotnet)){
    $archive=Join-Path $toolDirectory 'dotnet-sdk.zip'
    Download-File 'https://builds.dotnet.microsoft.com/dotnet/Sdk/8.0.425/dotnet-sdk-8.0.425-win-x64.zip' $archive
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $toolDirectory 'dotnet') -Force
}
if(!$SkipCad){& (Join-Path $PSScriptRoot 'SetupCad.ps1')}
$env:DOTNET_ROOT=Join-Path $toolDirectory 'dotnet'
$env:PATH=$env:DOTNET_ROOT+';'+$env:PATH
& $dotnet build (Join-Path $projectRoot 'EstunStudio.csproj')
if($LASTEXITCODE -ne 0){throw 'C# build failed'}
& $godot --headless --editor --path $projectRoot --import
if($LASTEXITCODE -ne 0){throw 'Godot import failed'}
Write-Host 'Ready. Run Start.cmd or Launch.ps1.'
