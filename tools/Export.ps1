[CmdletBinding()]
param(
    [switch]$SkipChecks,
    [string]$OutputDirectory = 'Build/ENCY-HYPER-Desktop'
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sdkDirectory = Join-Path $projectRoot '.tools\dotnet'
$dotnetExecutable = Join-Path $sdkDirectory 'dotnet.exe'
$godotExecutable = Join-Path $projectRoot '.tools\godot\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64_console.exe'
$templateDirectory = Join-Path $projectRoot '.tools\templates\4.5.1.stable.mono'
$releaseTemplate = Join-Path $templateDirectory 'windows_release_x86_64.exe'
$outputDirectory = [System.IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
$outputExecutable = Join-Path $outputDirectory 'ENCY HYPER - ESTUN.exe'

foreach ($dependency in @($dotnetExecutable, $godotExecutable, $releaseTemplate)) {
    if (-not (Test-Path -LiteralPath $dependency -PathType Leaf)) {
        throw "Required local build dependency is missing: $dependency"
    }
}

# Godot's C# desktop exporter runs dotnet publish --self-contained true.
# Keep the resulting data_EstunStudio_windows_x86_64 folder next to the EXE.
# No machine-wide .NET runtime or Godot installation is required to launch it.
$previousEnvironment = @{}
foreach ($environmentName in @('PATH', 'DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_NOLOGO')) {
    $previousEnvironment[$environmentName] = [Environment]::GetEnvironmentVariable($environmentName, 'Process')
}

Push-Location -LiteralPath $projectRoot
try {
    $env:DOTNET_ROOT = $sdkDirectory
    $env:DOTNET_ROOT_X64 = $sdkDirectory
    $env:PATH = $sdkDirectory + [System.IO.Path]::PathSeparator + $env:PATH
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'

    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    foreach ($ignoredDirectory in @('artifacts', 'Build', 'Releases', '.tools')) {
        $ignorePath = Join-Path $projectRoot ($ignoredDirectory + '/.gdignore')
        if (Test-Path -LiteralPath (Split-Path $ignorePath)) { New-Item -ItemType File -Path $ignorePath -Force | Out-Null }
    }
    # The Godot C# exporter requires a solution, even when dotnet build accepts
    # the project file directly. Generate it for fresh copies of this workspace.
    $solutionPath = Join-Path $projectRoot 'EstunStudio.sln'
    if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
        & $dotnetExecutable new sln --name EstunStudio --output $projectRoot
        if ($LASTEXITCODE -ne 0) { throw "Solution creation failed (exit $LASTEXITCODE)." }
        & $dotnetExecutable sln $solutionPath add (Join-Path $projectRoot 'EstunStudio.csproj')
        if ($LASTEXITCODE -ne 0) { throw "Adding the project to the solution failed (exit $LASTEXITCODE)." }
    }
    Write-Host 'Building C# project...'
    & $dotnetExecutable build (Join-Path $projectRoot 'EstunStudio.csproj') --configuration Debug -p:Optimize=true
    if ($LASTEXITCODE -ne 0) { throw "C# build failed (exit $LASTEXITCODE)." }

    Write-Host 'Importing project assets...'
    & $godotExecutable --headless --editor --path $projectRoot --import --quit
    if ($LASTEXITCODE -ne 0) { throw "Godot asset import failed (exit $LASTEXITCODE)." }

    if (-not $SkipChecks) {
        Write-Host 'Checking robot motion, inverse kinematics, and program persistence...'
        & $godotExecutable --headless --path $projectRoot 'res://Tests/RobotChecks.tscn'
        if ($LASTEXITCODE -ne 0) { throw "Robot checks failed (exit $LASTEXITCODE)." }
        & $godotExecutable --headless --path $projectRoot 'res://Tests/SceneChecks.tscn'
        if ($LASTEXITCODE -ne 0) { throw "Scene integration checks failed (exit $LASTEXITCODE)." }
        foreach($scene in @('RobotMeshChecks','DesktopShellChecks','DesktopWorkflowParityChecks','GizmoChecks','CadImportChecks','WeldChecks','WarningPlannerChecks','OrientationChecks','CollisionDetailChecks','RobotPostprocessorChecks')) {
            & $godotExecutable --headless --path $projectRoot ("res://Tests/"+$scene+'.tscn')
            if($LASTEXITCODE -ne 0){throw "$scene failed (exit $LASTEXITCODE)."}
        }
    }

    Write-Host 'Exporting Windows x64 application with a self-contained .NET runtime...'
    & $godotExecutable --headless --path $projectRoot --export-release 'Windows Desktop' $outputExecutable
    if ($LASTEXITCODE -ne 0) { throw "Godot Windows export failed (exit $LASTEXITCODE)." }
    if (-not (Test-Path -LiteralPath $outputExecutable -PathType Leaf)) {
        throw "Export reported success but the executable is missing: $outputExecutable"
    }

    $runtimeFiles = @(Get-ChildItem -LiteralPath $outputDirectory -Recurse -Filter 'coreclr.dll' -File)
    if ($runtimeFiles.Count -eq 0) {
        throw 'The exported .NET runtime is missing. The build is not self-contained.'
    }
    $assemblyFiles = @(Get-ChildItem -LiteralPath $outputDirectory -Recurse -Filter 'EstunStudio.dll' -File)
    if ($assemblyFiles.Count -eq 0) {
        throw 'The exported application assembly is missing.'
    }
    $cadRuntime=Join-Path $projectRoot '.tools/cad-python'
    if(!(Test-Path -LiteralPath (Join-Path $cadRuntime 'python.exe'))){throw 'CAD runtime missing: run tools/SetupCad.ps1'}
    Write-Host 'Copying self-contained OpenCascade STEP / IGES runtime...'
    & robocopy.exe $cadRuntime (Join-Path $outputDirectory 'CadRuntime') /E /NFL /NDL /NJH /NJS /NP /XD __pycache__
    if($LASTEXITCODE -gt 7){throw 'CAD runtime copy failed'}
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'cad_import.py') -Destination (Join-Path $outputDirectory 'CadRuntime/cad_import.py') -Force
    Write-Host ''
    Write-Host "Portable Windows build ready: $outputExecutable"
    Write-Host 'Copy the complete Build folder when moving the application to another computer.'
}
finally {
    Pop-Location
    foreach ($environmentName in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($environmentName, $previousEnvironment[$environmentName], 'Process')
    }
}
