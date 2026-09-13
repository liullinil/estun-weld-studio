from pathlib import Path
import struct, collections, json
import numpy as np

ROOT=Path(__file__).resolve().parents[1]
INPUT=ROOT/'Assets/Robot/OEM/ency/Images'
OUT=ROOT/'artifacts'
OUT.mkdir(exist_ok=True)
SIZES={0:4,1:0,2:12,3:12,4:12,5:1,6:64,7:128,8:48,9:72,10:104}

def parse(path):
    data=path.read_bytes(); pos=105; mode=-1
    groups=collections.defaultdict(list); vertices=[]; normals=[]
    color=(1.,1.,1.,1.); normal=(0.,0.,1.); matrix=np.eye(4); counts=collections.Counter()
    def flush():
        if mode not in (4,5,6) or len(vertices)<3:return
        n=len(vertices)
        if mode==4:idx=np.arange(n//3*3).reshape(-1,3)
        elif mode==5:idx=np.array([(i,i+1,i+2) if i%2==0 else (i+1,i,i+2) for i in range(n-2)])
        else:idx=np.array([(0,i,i+1) for i in range(1,n-1)])
        v=np.array(vertices,dtype=np.float32)[idx]
        nr=np.array(normals,dtype=np.float32)[idx]
        good=np.linalg.norm(np.cross(v[:,1]-v[:,0],v[:,2]-v[:,0]),axis=1)>1e-8
        if good.any():groups[color].append(np.concatenate([v[good],nr[good]],axis=2))
    while pos<len(data):
        op=data[pos];counts[op]+=1
        if op not in SIZES:raise ValueError(f'{path.name}: unknown opcode{op} at{pos}')
        p=pos+1
        if op==0:
            flush();mode=struct.unpack_from('<I',data,p)[0];vertices=[];normals=[]
        elif op==1:
            flush();mode=-1;vertices=[];normals=[]
        elif op==2:normal=struct.unpack_from('<3f',data,p)
        elif op==3:
            if mode>=0:vertices.append(struct.unpack_from('<3f',data,p));normals.append(normal)
        elif op==4:color=struct.unpack_from('<3f',data,p)+(1.,)
        elif op==7:matrix=np.array(struct.unpack_from('<16d',data,p)).reshape(4,4).T
        pos+=1+SIZES[op]
    flush()
    combined={c:np.concatenate(g) for c,g in groups.items() if g}
    payload={'matrix':matrix,'colors':np.array(list(combined))}
    for i,(c,g) in enumerate(combined.items()):payload[f'group{i}']=g
    name=path.stem.replace('S20-180 Pro - ','').replace(' ','').lower()
    np.savez_compressed(OUT/f'ency-{name}.npz',**payload)
    allv=np.concatenate(list(combined.values()))[:,:,:3].reshape(-1,3)
    report={'name':name,'triangles':sum(len(g) for g in combined.values()),'colors':list(combined),'bounds':[allv.min(0).tolist(),allv.max(0).tolist()],'matrix':matrix.tolist(),'commands':dict(counts)}
    print(json.dumps(report),flush=True)
    return report

reports=[parse(p) for p in sorted(INPUT.glob('*.osd'))]
(OUT/'ency-mesh-report.json').write_text(json.dumps(reports,indent=2))
