"""Package frontend and the exact desktop planning kernel, never credentials."""
from pathlib import Path
import shutil
import tarfile
import json

root = Path(__file__).resolve().parents[2]
web = root / 'Web'
stage = root / 'artifacts' / 'hyper-build'
stage.mkdir(parents=True, exist_ok=True)
native = stage / 'native'
native.mkdir(exist_ok=True)
for name in ('Scripts', 'Shaders', 'Scenes'):
    shutil.copytree(root / name, native / name, dirs_exist_ok=True)
for name in ('project.godot', 'EstunStudio.csproj'):
    shutil.copy2(root / name, native / name)
for relative in ('Assets/Robot/OEM/S20-180-Pro.meshbin', 'Assets/icon.svg', 'Assets/Fonts/Inter-Regular.ttf', 'Assets/Fonts/Inter-SemiBold.ttf', 'Assets/Textures/studio_small_09_2k.hdr', 'Assets/Samples/WeldingBracket.step', 'Tests/WebBridge.tscn', 'tools/cad_import.py'):
    target=native / relative
    target.parent.mkdir(parents=True,exist_ok=True)
    shutil.copy2(root / relative,target)
for name in ('dist','server'):
    shutil.copytree(web / name,stage / 'web' / name,dirs_exist_ok=True)
for name in ('package.json','package-lock.json'):
    shutil.copy2(web / name,stage / 'web' / name)
for name in ('Dockerfile','entrypoint.sh'):
    (stage / name).write_text((web / 'deploy' / name).read_text(),encoding='utf-8',newline='\n')
archive=root / 'artifacts' / 'hyper-build.tar.gz'
with tarfile.open(archive,'w:gz',compresslevel=4) as tar:
    for item in stage.iterdir(): tar.add(item,arcname=item.name)
print(json.dumps({'archive':str(archive),'bytes':archive.stat().st_size}))
