param(
    [string]$Destination = (Join-Path $PSScriptRoot '../.tools/cad-python')
)

$ErrorActionPreference = 'Stop'
$runtimePath = [System.IO.Path]::GetFullPath($Destination)
$cachePath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../.tools/cad-download'))
New-Item -ItemType Directory -Path $runtimePath, $cachePath -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Net.Http

function Get-VerifiedArchive([string]$Url, [string]$Name, [string]$Sha256) {
    $archivePath = Join-Path $cachePath $Name
    if (!(Test-Path -LiteralPath $archivePath) -or (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $Sha256) {
        Write-Host "Downloading $Name..."
        $client = [System.Net.Http.HttpClient]::new()
        $client.Timeout = [TimeSpan]::FromMinutes(10)
        try {
            $response = $client.GetAsync($Url, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            $response.EnsureSuccessStatusCode() | Out-Null
            $file = [System.IO.File]::Create($archivePath)
            try { $response.Content.CopyToAsync($file).GetAwaiter().GetResult() } finally { $file.Dispose() }
        } finally { $client.Dispose() }
    }
    if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $Sha256) { throw "SHA256 mismatch: $Name" }
    return $archivePath
}

function Expand-Package([string]$Archive, [string]$Target) {
    $package = [System.IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        foreach ($entry in $package.Entries) {
            $targetFile = [System.IO.Path]::GetFullPath((Join-Path $Target $entry.FullName))
            if (!$targetFile.StartsWith($Target.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Archive entry escapes runtime directory: $($entry.FullName)"
            }
            if ($entry.Name -eq '') { New-Item -ItemType Directory -Path $targetFile -Force | Out-Null; continue }
            New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($targetFile)) -Force | Out-Null
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $targetFile, $true)
        }
    } finally { $package.Dispose() }
}

$pythonZip = Get-VerifiedArchive 'https://www.python.org/ftp/python/3.11.9/python-3.11.9-embed-amd64.zip' 'python-3.11.9-embed-amd64.zip' '009D6BF7E3B2DDCA3D784FA09F90FE54336D5B60F0E0F305C37F400BF83CFD3B'
$ocpWheel = Get-VerifiedArchive 'https://files.pythonhosted.org/packages/22/a0/d3b112d998f3265fdea61e8f6f241e1b8dca653fcc7e444b5f1369524037/cadquery_ocp-7.7.2.2b2-cp311-cp311-win_amd64.whl' 'cadquery_ocp-7.7.2.2b2-cp311-cp311-win_amd64.whl' '7606E20DA8486C99A9A97F588DD3BA096704F21A4DB42425ABD863D078859C60'
# OCP's native visualization DLL links VTK 9.2, even though this worker uses no VTK rendering.
$vtkWheel = Get-VerifiedArchive 'https://files.pythonhosted.org/packages/1c/a8/11eeb04d8f287cdd6ef7778774e9009afad2d7f406f4a5617a19106a0ea9/vtk-9.2.6-cp311-cp311-win_amd64.whl' 'vtk-9.2.6-cp311-cp311-win_amd64.whl' '6C3CA0663F251FBD6E26D93294801CEEE6C3CC329F6070DCCDE3B68046AB9EE7'

Write-Host 'Installing isolated CAD runtime...'
Expand-Package $pythonZip $runtimePath
$packagesPath = Join-Path $runtimePath 'Lib/site-packages'
New-Item -ItemType Directory -Path $packagesPath -Force | Out-Null
Expand-Package $ocpWheel $packagesPath
Expand-Package $vtkWheel $packagesPath
Set-Content -LiteralPath (Join-Path $runtimePath 'python311._pth') -Value "python311.zip`n.`nLib/site-packages`nimport site" -Encoding ASCII
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'cad_import.py') -Destination (Join-Path $runtimePath 'cad_import.py') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CadRuntime-LICENSES.txt') -Destination $runtimePath -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'cad-licenses') -Destination $runtimePath -Recurse -Force
& (Join-Path $runtimePath 'python.exe') -c 'from OCP.BRepPrimAPI import BRepPrimAPI_MakeBox; assert not BRepPrimAPI_MakeBox(1,2,3).Shape().IsNull(); print(True)'
if ($LASTEXITCODE -ne 0) { throw 'CAD runtime verification failed' }
Write-Host "Installed to $runtimePath. No system Python or pip is needed."
