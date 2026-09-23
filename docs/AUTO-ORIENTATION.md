# Automatic torch orientation

The browser and Windows desktop share the same native planner. Imported face
normals are geometric hints: their signs no longer force the torch into a part.

For each seam the planner tries both travel directions, four signed face-bisector
fields, wrist rolls, work-angle offsets up to 30 degrees and travel lean up to
12 degrees. It first tries nominal approaches, then coarse angular alternatives
and intermediate angles. The chosen face signs and angles follow the full seam;
isolated near-antipodal imported normal flips are unwrapped without changing the
source CAD. Rotated/translated placement and varying face normals are preserved.

Rigid torch collision prefilters reject impossible orientations before inverse
kinematics. Accepted paths still require continuous joint solutions and full
sampled checks for welding, approach, transfer and retract. The torch tip is also
checked for solid containment at every sampled joint pose, including between
clear endpoints. The existing 2 mm body margin and maximum summed joint step of
0.032 degrees are unchanged; the process tip uses containment without adding the
body margin to the specified arc gap.

Complete collision-free paths are preferred. If none is found, existing explicit
warning/partial previews remain available. An already colliding starting pose
can retain a warned entry while a corrected welding pass and retract are clear.
No warning path is labelled collision-free. This finite search is not a proof
that every feasible orientation will be found.

JSON and controller metadata record the selected strategy, attempted orientation
count and whether the approach was adjusted. Lua comments identify adjusted tool
orientation. Weaving preserves the same metadata.

Regression scenes cover inverted individual/both face normals, smoothly varying
normals, isolated sign flips, opposing sheet normals, placed CAD frames, an
overhead fixture blocking nominal choices, colliding entry and cancellation.
Detailed collision tests also check a tip-only obstacle between two clear poses.

Validated on 2026-09-17: 653 orientation assertions, 2,152 detailed collision
assertions, 37 desktop workflow assertions and 336 welding/STEP/path assertions.
The strict sample retains 5 ready / 6 blocked seams and 152 source motions; the
focused local calculation took 7.5 seconds (hardware-dependent).

The complete Windows export passed all 12 configured suites (5,134 assertions),
and the browser passed 28 tests. The exported portable executable passed a
headless launch smoke check. Server API verification on the unchanged bracket
retained 5 ready / 6 warning / 0 blocked seams and 244 controller commands in
15.1 seconds. Separate API cases invert either or both normals of S002: each
returns one complete Ready seam with a corrected bisector, 100% coverage and
the automatic-orientation Lua comment (1.23–1.30 seconds per case).

Published browser container: `hyper-app-v12`, image
`estun-hyper:20260917-auto-orientation-v12`. Windows ZIP size: 203,858,842 bytes;
SHA-256: `12a6044ccd8478cea107aea1e05c324e597f53a444a0178e9b13de3f140e6e8c`.
The origin SwissCAM HTML SHA-256 remained
`d476840e3bacae1b7018fc004998f84a5c7b556678e1e3a0b3642263148d588d`.
