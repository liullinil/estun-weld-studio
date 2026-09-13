# Converts verified per-link RoboDK CAD patches into the runtime's compact ESM1 format.
# Retains patch hard edges, smooths coincident vertices only within each original patch.
import json,struct,math,collections
from pathlib import Path
meshes=json.loads(Path('Assets/Robot/OEM/source_meshes.json').read_text())
def cross(a,b):return (a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0])
def normalize(v):
 l=math.sqrt(sum(x*x for x in v));return tuple(x/l for x in v) if l>1e-20 else (0,0,1)
def cv(v):return (v[0],v[2],-v[1])
out={}
for li,groups in enumerate(meshes):
 for group in groups:
  vertices=group['vertices'];acc={};faces=[]
  for i in range(0,len(vertices),3):
   a,b,c=vertices[i:i+3]
   n=cross(tuple(b[k]-a[k] for k in range(3)),tuple(c[k]-a[k] for k in range(3)))
   if sum(x*x for x in n)<1e-18:continue
   faces.append((a,b,c))
   for v in (a,b,c):
    key=tuple(round(x,5) for x in v)
    acc[key]=tuple(acc.get(key,(0,0,0))[k]+n[k] for k in range(3))
  target=out.setdefault((li,tuple(group['color'])),[])
  for face in faces:
   # RoboDK triangles are OpenGL CCW; Godot uses clockwise front faces.
   for v in (face[0],face[2],face[1]):
    key=tuple(round(x,5) for x in v)
    normal=cv(normalize(acc[key]));pos=tuple(x*.001 for x in cv(v))
    target.append((*pos,*normal))
p=Path('Assets/Robot/OEM/S20-180-Eco.meshbin')
with p.open('wb') as f:
 f.write(b'ESM1');f.write(struct.pack('<i',len(out)))
 for (link,rgba),verts in out.items():
  f.write(struct.pack('<i4fi',link,*rgba,len(verts)))
  for v in verts:f.write(struct.pack('<6f',*v))
print(p,len(out),'groups',sum(len(v)//3 for v in out.values()),'triangles',p.stat().st_size,'bytes')
