"""Losslessly index original CAD positions AND normals; retain every triangle."""
from pathlib import Path
import struct
import json
root=Path(__file__).resolve().parents[2]
source=(root/'Assets/Robot/OEM/S20-180-Pro.meshbin').read_bytes()
groups=struct.unpack_from('<i',source,4)[0]
offset=8
out=bytearray(b'ESI1'+struct.pack('<i',groups))
total=unique=0
for _ in range(groups):
    header=source[offset:offset+20]
    count=struct.unpack_from('<i',source,offset+20)[0]
    offset+=24
    lookup={}
    vertices=bytearray()
    indices=[]
    for i in range(count):
        vertex=source[offset+i*24:offset+(i+1)*24]
        key=lookup.get(vertex)
        if key is None:
            key=len(lookup)
            lookup[vertex]=key
            vertices.extend(vertex)
        indices.append(key)
    offset+=count*24
    out.extend(header+struct.pack('<ii',len(lookup),count)+vertices)
    out.extend(struct.pack('<'+'I'*count,*indices))
    total+=count
    unique+=len(lookup)
assert offset==len(source)
destination=root/'Web/public/assets/robot.meshbin'
destination.write_bytes(out)
print(json.dumps({'triangles':total//3,'sourceVertices':total,'indexedVertices':unique,'bytes':len(out),'reduction':1-unique/total}))
