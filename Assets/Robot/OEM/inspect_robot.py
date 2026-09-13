import struct, json, math
from pathlib import Path
p=Path('Assets/Robot/OEM/Estun-S20-180-Eco.uncompressed')
b=p.read_bytes()
print('V0 f32+f64',struct.unpack_from('<3f3d',b,32040))
for stride in [24,28,36,48,56,72,84]:
 print('stride',stride, [struct.unpack_from('<3f',b,32040+stride*i) for i in range(3)])
print('coords v1',struct.unpack_from('<3f3d',b,32068))
for stride in [84,108,168,216]:
 off=32040+2454*stride
 print('end?',stride,off,b[off:off+96].hex(),struct.unpack_from('<6Q',b,off))
