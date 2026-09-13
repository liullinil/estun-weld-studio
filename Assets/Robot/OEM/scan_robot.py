import struct,math
b=open('Assets/Robot/OEM/Estun-S20-180-Eco.uncompressed','rb').read()
for i in range(2454*3):
 xyz=struct.unpack_from('<3f',b,32040+i*14)
 if not all(math.isfinite(v) and abs(v)<10000 for v in xyz):
  print('first invalid',i,32040+i*14, xyz, b[32040+(i-5)*14:32040+(i+5)*14].hex());break
 else:
  if i<5:print(i,xyz)
