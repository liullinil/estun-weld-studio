@echo off
cd /d "%~dp0"
if exist "Build\ESTUN Studio.exe" (
  start "" "Build\ESTUN Studio.exe"
) else (
  set "DOTNET_ROOT=%~dp0.tools\dotnet"
  start "" "%~dp0.tools\godot\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64.exe" --path "%~dp0."
)
