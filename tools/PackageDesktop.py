"""Package the verified portable Windows desktop, without SDKs or credentials."""
from pathlib import Path
import hashlib,json,shutil,zipfile

root=Path(__file__).resolve().parents[1]
build=root/'Build/ENCY-HYPER-Desktop'
exe=build/'ENCY HYPER - ESTUN.exe'
assert exe.is_file(), 'Export desktop first'
assert list(build.rglob('coreclr.dll')), 'Missing self-contained .NET runtime'
assert (build/'CadRuntime/python.exe').is_file(), 'Missing CAD runtime'
shutil.copy2(root/'THIRD_PARTY.md',build/'THIRD_PARTY.md')
licenses=build/'Licenses';licenses.mkdir(exist_ok=True)
for name in ('Godot-MIT.txt','Godot-Third-Party.txt','DotNet-MIT.txt','Inter-OFL.txt'):
 source=root/'Build/Licenses'/name
 if source.is_file():shutil.copy2(source,licenses/name)
(build/'START-HERE.txt').write_text('''ENCY HYPER - ESTUN / Windows x64

Extract this entire ZIP into a folder, then run "ENCY HYPER - ESTUN.exe".
Keep the adjacent data_EstunStudio_windows_x86_64 and CadRuntime folders.
No installed Godot, .NET or Python is required. STEP and IGES import run locally.

Graphics default to Ultra: native resolution, 8x MSAA, high-quality shadows,
ambient occlusion, indirect lighting, HDR materials and welding effects.
Use the circular display controls to reduce quality on slower GPUs.

Import CAD or use the loaded sample. Set angle/length filters, explicitly select
potential seams, then Generate program. Propagation expands through connected
neighbors one line at a time. Simulation uses AUTO; manual jog is disabled until
playback stops. Pause resumes; Stop requires regeneration. Lua export asks for
arc conversion and torch digital output. All UI text is English.

This is an offline simulator. Warning seams can deliberately include collisions
or partial coverage; review red warnings. There is no robot hardware connection.
The exported Lua follows the supplied CODROID command reference and requires
matching controller, robot, tool and coordinate calibration before physical use.
''',encoding='utf-8')
destination=root/'Releases/ENCY-HYPER-ESTUN-Windows-x64.zip';destination.parent.mkdir(exist_ok=True)
with zipfile.ZipFile(destination,'w',zipfile.ZIP_DEFLATED,compresslevel=3) as archive:
 for source in sorted(build.rglob('*')):
  if source.is_file() and '__pycache__' not in source.parts and source.suffix.lower() not in ('.log','.tmp'):
   archive.write(source,Path('ENCY HYPER - ESTUN')/source.relative_to(build))
digest=hashlib.sha256(destination.read_bytes()).hexdigest()
metadata={'file':destination.name,'bytes':destination.stat().st_size,'sha256':digest,'platform':'Windows x64','graphics':'Ultra / native / 8x MSAA'}
(root/'Releases/desktop-release.json').write_text(json.dumps(metadata,indent=2),encoding='utf-8')
print(json.dumps(metadata))
