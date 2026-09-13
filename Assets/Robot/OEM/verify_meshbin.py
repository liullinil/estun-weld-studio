import struct
from pathlib import Path
p=Path('Assets/Robot/OEM/S20-180-Pro.meshbin');b=p.read_bytes()
print('size',len(b),'header',b[:8])
o=8
for i in range(struct.unpack_from('<i',b,4)[0]):
 link,*rgba,n=struct.unpack_from('<i4fi',b,o);o+=24
 bounds=[[1e9]*3,[-1e9]*3]
 positive=negative=zero=0
 for vi in range(0,n,3):
  data=struct.unpack_from('<18f',b,o+vi*24)
  a=data[:3];bn=data[6:9];c=data[12:15];normal=data[3:6]
  ab=[bn[k]-a[k] for k in range(3)];ac=[c[k]-a[k] for k in range(3)]
  cross=[ab[1]*ac[2]-ab[2]*ac[1],ab[2]*ac[0]-ab[0]*ac[2],ab[0]*ac[1]-ab[1]*ac[0]]
  dot=sum(cross[k]*normal[k] for k in range(3))
  positive+=dot>1e-15;negative+=dot< -1e-15;zero+=abs(dot)<=1e-15
  for v in [a,bn,c]:
   for k in range(3):bounds[0][k]=min(bounds[0][k],v[k]);bounds[1][k]=max(bounds[1][k],v[k])
 print('group',i,'link',link,'color',rgba,'tri',n//3,'bounds',bounds,'cw,ccw,deg',negative,positive,zero)
 o+=n*24
print('end',o,'match',o==len(b))
