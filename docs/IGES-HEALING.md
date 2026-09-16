# IGES surface sewing and face visibility

The importer now sews surface-only IGES before tessellation and edge recognition.
Existing solid assemblies bypass sewing, retaining their separate contact topology.
The fixed working and maximum tolerance is **0.001 mm**, after OpenCascade unit conversion;
local edge tolerances cannot increase it. The operation is rejected if faces disappear,
if OpenCascade reports deleted faces, or if the sewn topology is invalid. Closed shells
become validated outward-oriented solids; open shells stay open, with an import note.

The browser renders CAD surfaces on both sides, matching the desktop. This also makes
genuinely open surfaces inspectable. Tessellation no longer silently skips a face that
has no triangles: it reports the incomplete import with face IDs instead.

Verified against the two reported ENCY models, without modifying their source files:

| Model | Before | After |
| --- | --- | --- |
| `49-1.igs` | 40 disconnected faces, 0 candidates | 40 preserved faces, 1 valid closed solid, 480 candidates; 473 in 85–95° |
| `Part.IGS` | 36 disconnected faces, 0 candidates; several faces reversed physically | 36 preserved faces, 1 valid outward solid, 472 candidates in 85–95° |

All original bounds are preserved. The complete repaired meshes contain 5,656 and
2,748 triangles respectively. Sewing rebuilds edge tessellation, so the triangle count
changes while the original surfaces and face count remain intact. No surface filling,
face-domain unification, or intentional geometry simplification is performed.

The apparent missing faces in `Part.IGS` were not lost by the IGES reader: all 36 had
triangulations. Their inconsistent orientation combined with browser back-face culling
made some top and pocket-floor surfaces disappear. Sewing/orientation plus double-sided
CAD rendering resolves this.

Regression coverage includes 14 OpenCascade tests: existing STEP and IGES solid tests,
surface-patch sewing with outward normals, preservation of open shells, and retaining
three separate solids in an IGES assembly. Actual source-model checks additionally
validate all faces, unchanged bounds and outward solid classification.

Source fingerprints:

- `49-1.igs`: `97427e38fb109803502c360c509fa11980f6a445582a6fe8221e02f0b1e23520`
- `Part.IGS`: `32e66475b2b05a13ecc20570b93c29edbd435c1b2d3bb9aa4a47bd6b0bad6e45`
