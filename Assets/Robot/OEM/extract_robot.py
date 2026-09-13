import struct,math,json
from pathlib import Path
b=Path('Assets/Robot/OEM/Estun-S20-180-Eco.uncompressed').read_bytes()
o=32000
u64=lambda off:struct.unpack_from('<Q',b,off)[0]
links=u64(o);o+=8
all_links=[]
for li in range(links):
 
 while u64(o)==0: print('skip zero separator',o);o+=8
 groups=u64(o);o+=8
 print('link',li,'groups',groups,'offset',o)
 if groups>10000:raise ValueError('bad group count')
 out=[]
 for gi in range(groups):
  tris=u64(o);o+=8
  rgba=struct.unpack_from('<4f',b,o);o+=16
  if tris>1000000 or not all(0<=x<=1 for x in rgba):raise ValueError((li,gi,o,tris,rgba))
  verts=[struct.unpack_from('<3f',b,o+i*14) for i in range(tris*3)]
  if not all(all(math.isfinite(x) and abs(x)<100000 for x in v) for v in verts):raise ValueError((li,gi,o,'bad verts'))
  o+=tris*3*14
  out.append({'color':rgba,'vertices':verts})
 all_links.append(out)
 flat=[v for g in out for v in g['vertices']]
 print('triangles',len(flat)//3,'bbox',[(min(v[a] for v in flat),max(v[a] for v in flat)) for a in range(3)])
print('end',o,len(b),'remaining',len(b)-o,b[o:o+100].hex())
Path('Assets/Robot/OEM/source_meshes.json').write_text(json.dumps(all_links,separators=(',',':')))

