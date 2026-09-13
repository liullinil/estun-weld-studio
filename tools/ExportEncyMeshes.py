"""Convert the preserved native Pro CAD triangles into the application's ESM1 asset.

Requires ency_parse_osd.py outputs and manufacturer-ency-kinematics.json from
the kinematics mapping step. Matrices map global Godot-mm to joint-local-mm.
"""
from pathlib import Path
import json, struct
import numpy as np

ROOT=Path(__file__).resolve().parents[1]
ART=ROOT/'artifacts'
FRAMES=ROOT/'Assets/Robot/OEM/manufacturer-ency-kinematics.json'
TARGET=ROOT/'Assets/Robot/OEM/S20-180-Pro.meshbin'

def export():
    frames=np.array(json.loads(FRAMES.read_text())['mesh_global_to_local_at_design_mm'],dtype=np.float64)
    assert frames.shape==(7,4,4), frames.shape
    # Right-handed raw CAD(mm) -> right-handed Godot(m), with Y vertical.
    convert=np.array([[0,1,0],[0,0,1],[1,0,0]],dtype=np.float64)
    groups=[];report=[]
    for link,name in enumerate(['base','joint1','joint2','joint3','joint4','joint5','joint6']):
        data=np.load(ART/f'ency-{name}.npz')
        inv=frames[link]
        rotation=inv[:3,:3]@convert
        normal_rotation=np.linalg.inv(rotation).T
        for index,color in enumerate(data['colors']):
            native=data[f'group{index}'].astype(np.float64)
            v=(native[:,:,:3]@rotation.T+inv[:3,3])*.001
            n=native[:,:,3:]@normal_rotation.T
            lengths=np.linalg.norm(n,axis=2,keepdims=True)
            assert lengths.min()>.5 and lengths.max()<1.5
            n/=lengths
            # Godot forward-facing triangles wind clockwise viewed from outside.
            dots=np.einsum('ij,ij->i',np.cross(v[:,1]-v[:,0],v[:,2]-v[:,0]),n.sum(1))
            swap=dots>0
            v[swap]=v[swap][:,[0,2,1]]
            n[swap]=n[swap][:,[0,2,1]]
            packed=np.concatenate([v,n],axis=2).reshape(-1,6).astype('<f4')
            rgba=np.array(color,dtype=np.float32);rgba[:3]*=.6
            groups.append((link,rgba,packed))
            report.append({'link':link,'source':name,'triangles':len(v),'color':rgba.tolist(),'bounds':[v.min((0,1)).tolist(),v.max((0,1)).tolist()]})
    with TARGET.open('wb') as f:
        f.write(b'ESM1');f.write(struct.pack('<i',len(groups)))
        for link,color,packed in groups:
            f.write(struct.pack('<i4fi',link,*color,len(packed)))
            f.write(packed.tobytes())
    summary={'file':str(TARGET),'groups':len(groups),'triangles':sum(g['triangles'] for g in report),'bytes':TARGET.stat().st_size,'parts':report}
    (ART/'ency-export-report.json').write_text(json.dumps(summary,indent=2))
    print(json.dumps(summary,indent=2))

if __name__=='__main__':export()
