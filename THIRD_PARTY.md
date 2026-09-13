# Third-party assets and references

## Environment photography

`Assets/Textures/studio_small_09_2k.hdr` — Studio Small 09, Poly Haven.
Source: https://polyhaven.com/a/studio_small_09
Download metadata: https://api.polyhaven.com/files/studio_small_09
License: CC0 1.0, https://polyhaven.com/license

## Typeface

Inter, Rasmus Andersson and contributors. SIL Open Font License 1.1.
Source: https://github.com/google/fonts/tree/main/ofl/inter
License copy: `Assets/Fonts/OFL.txt`.
The font is a variable font; UI weights are selected using Godot FontVariation.

## Robot reference

The active 3D robot model comes from the publicly downloadable ENCY MachineMaker **Estun S20-180 Pro** package, GUID {EB55546E-532E-43EE-84F0-31C344506178}. Source: https://download.encycam.com/MachineMaker/online/index.html?app=Ency&mechanismGuid=%7BEB55546E-532E-43EE-84F0-31C344506178%7D

The seven source OSD link meshes were converted into the compact S20-180-Pro.meshbin used by Godot; source geometric detail and normals are preserved. Original archive and native mechanism metadata remain in the workspace for traceability. ENCY and ESTUN retain their rights to source model data and marks; public download does not imply a new redistribution license. Use within this user-requested local simulator does not transfer ownership or certify calibration.

RoboDK S20-180 Eco was separately inspected: https://robodk.com/robot/Estun/S20-180-Eco . Its asset is retained for development comparison and is excluded from the runtime export.

See Assets/Robot/REFERENCE.md for exact sources and conversion notes. The pendant and articulated-arm application icon are original interface artwork. No KUKA assets are used.

## Runtime

The isolated STEP adapter uses Python 3.11.9 (PSF), cadquery-ocp 7.7.2.2b2 (Apache 2.0 binding), Open CASCADE Technology 7.7.2 (LGPL 2.1 with OCCT exception), and VTK 9.2.6 (BSD). Actual notices and native dependency licenses are under `tools/cad-licenses/`, with an overview in `tools/CadRuntime-LICENSES.txt`. These notices are included inside the portable `CadRuntime` folder. The worker is an original format adapter; all application planning, visualization and control logic is C#.

`Assets/Samples/WeldingBracket.step` is an original demonstration assembly generated for this project. The welding torch and CAD manipulation graphics are original illustrative project geometry, not a manufacturer's calibrated tooling model.

Godot Engine 4.5.1 (.NET), MIT license: https://godotengine.org/license/
The exported engine includes additional third-party components listed at https://godotengine.org/license/#thirdparty.
.NET 8 runtime, MIT license: https://github.com/dotnet/runtime/blob/main/LICENSE.TXT

