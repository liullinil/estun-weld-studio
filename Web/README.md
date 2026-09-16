# ENCY HYPER - ESTUN

Published at **https://178.105.241.117/hyper/**. The existing SwissCAM at the origin root is independent and remains unchanged.

Source baseline: `liullinil/estun-weld-studio`, `main`, `e783203fb4ec32d592d9d4922d891032fe162100` (latest GitHub main on 2026-09-16).

The browser uses Three.js/WebGL 2, original ENCY S20-180 Pro CAD, native joint frames and limits, the mounted MIG/MAG torch and the original studio HDR. A lossless indexed mesh retains all **514,833 triangles**, every source position and normal; rendering vertices fall from 1,544,499 to 326,788. No geometry is decimated. The workspace header contains only the ENCY HYPER - ESTUN brand. TCP, Trace and all display controls are SwissCAM-style circular icons on the left of the viewport; exposure and resolution open compact controls beside their icons. Only the seam list scrolls. The pendant and preparation panel fit the available height, and the selected seam uses a bright cyan five-pixel line with a wider halo, whether selected in the list or in the viewport.

## Workflow

The sample loads automatically with no seams selected for processing. Angle and length filters only identify potential seams; Concave/contact is off by default. Drag the two rays on the 90-degree protractor or enter exact angle values. Length has minimum/maximum sliders and exact millimetre inputs. Candidates outside either range are absent from the list, viewport, propagation graph and work order. Click a candidate in the scene or list to add it; click again to remove it. The nearby Propagation control applies to one line, direct connected neighbors, or the full endpoint-connected contour (including branches). Connections use a 0.02 mm endpoint tolerance; midpoint crossings are not connections. Changing scope recalculates that click from its initial snapshot; removal recalls the scope used when each line was added. Candidates cannot be deleted. Narrowing filters prunes the work order; widening does not automatically restore selection.

The protractor starts at zero on the left, with its fixed right-angle profile opening to the right. Only a left-button press on a round handle starts dragging; pointer capture keeps the drag active outside the instrument and release ends it anywhere. Propagation closes on any left click outside its popup. SelectAll / unSelectAll affect the current candidate set. All application UI, hints, warnings and Lua comments are English.

Open Part placement for Move/Rotate and numeric transforms. **Generate program** is available only for an explicit nonempty work order and displays percentage, current operation and elapsed time, with a separate **Cancel** control. It runs the native C# planner, then fits and independently validates controller motion primitives. The generated trajectory appears immediately: dashed amber transfers and solid green welding moves.

Web planning first searches for the existing collision-free complete route. If that fails, reachable colliding motion remains executable as a red warning; a partially reachable seam keeps its longest continuous reachable portion. Completely unreachable seams remain blocked. Each seam exposes its reason, collision identities and coverage. Disable warnings removes warning seams from the work order with one click and invalidates the program for replanning. Warning metadata remains visible until geometry/placement or a new calculation changes it. Lua warning paths retain the exact reviewed joint interpolation and include explicit warning comments; they are not labelled collision-free. The native desktop planner keeps strict rejection by default.

**Simulate** becomes available only after a program is ready. **Pause** holds the pose and extinguishes the arc; press it again to resume. **Stop** discards the program and requires regeneration. During playback and pause the pendant displays **AUTO**, blocks jog keys and Go Home, and still allows speed and JOINT/WORLD/TOOL display changes. It returns to **MANUAL** on completion/stop. The red stop button latches on first press and releases on the second, without restarting motion. Go Home sits beside the speed slider. Drives, recording, Run and Reset controls have been removed. The ± keys are inside the LCD beside their readouts. Space stops; Escape latches stop; losing focus pauses automatic playback and stops manual motion.

Seam candidates, selection outlines and calculated path overlays are hidden during simulation and pause. Simulation starts with old deposited beads cleared. Collision frames for warning motion are precomputed against the final playback path and show involved links and contact names synchronously, without server round-trip delay.

**Export** appears after successful generation. Lua output follows the user-supplied `commands.zip` **CODROID Lua** reference, using `movJ`, `movL`, `movC` and torch output **`setDO(1,1)` / `setDO(1,0)`**. Targets use the documented joint representation `jp`; tool and coordinate-system IDs default to 1. Linear/circular compression retains approximately constant orientation, fits within 0.5 mm, solves the same IK branch and revalidates collision samples. Unsupported compression retains original joint moves. Browser playback and JSON/CSV use the revalidated samples for the exported primitives. This is a heuristic program optimization, not a proof of globally optimal machining time; exact stops are retained because unvalidated blending would change the trajectory.

The export dialog contains only Lua options and Save. It defaults to retaining arcs and torch digital output 1. It can linearize arcs and choose output 0–65535; it reruns only postprocessing, without complete replanning. The displayed and simulated path updates to the export variant.

Weaving is off by default, with detailed defaults: one-sided amplitude 1 mm, frequency 2 Hz, plane rotation 0°, endpoint fade 2 mm. Interactive illustrations explain amplitude, frequency, endpoint fade and plane rotation. Enabling it produces an actual sinusoidal IK/collision-checked path for playback and export. In web warning mode collisions remain explicit warnings and infeasible continuation keeps the reachable prefix; subsequent travel is torch-off. Export uses explicit samples because the supplied `movLW/movCW` reference does not specify waveform/type/frame semantics sufficiently to reproduce them exactly.

The circular collision icon on the left enables/disables manual-pose red highlighting of implicated robot links and torch. Native checks include the workpiece, floor, plinth and nonadjacent self-collisions; same/adjacent links are excluded. It polls changed poses at a minimum 60 ms interval with a single request in flight, reusing a bounded server collision scene. During simulation warning contacts are always shown from the precomputed contact schedule. Base/A2 slab overlaps now receive a precise source-mesh BVH narrow phase, rejecting the reported false positive at A1=26°, A2=120° while retaining actual mesh intersections and the 2 mm margin.

Rendering includes ACES, original HDR reflections, two shadow-casting studio lights, ambient occlusion, bloom, arc light, sparks, smoke and cooling weld beads. Display icons adjust resolution, exposure and effects. The view cube is ported from SwissCAM: transparent 104-pixel control, orientation synchronized with the camera, all six faces, twelve edges and eight corners, hover highlighting, and drag-to-orbit. Focus it for F/B/T/D/L/R face shortcuts, arrow-key rotation and Home/Enter isometric. Space and Escape retain the robot's stop controls. F fits the scene, P toggles the pendant and Tab toggles cinema; the former top and bottom button bars have been removed.

The browser rendering engine differs from Godot Forward+, so the lighting and postprocessing are not pixel-identical. Web import is limited to **64 MB**, and tessellated output to 60 MB. CAD import and planning require the server. The native planner accepts one active job at a time and has a 10-minute timeout. Uploaded STEP files and intermediate geometry are temporary and removed after import.

This remains an offline simulator. Sampled native collision planning does not prove continuous collision freedom. Manual jog and Go Home are not collision-validated. The application has no hardware communication. Lua requires a controller implementing the supplied CODROID command dialect, matching robot joint zeros/directions and calibrated tool/base data; it is not an assertion that an unconfigured ESTUN controller accepts that language. Export includes a starting-joint guard, calibration metadata and torch-off initialization/finalization.

## Local development

From the repository root, build the C# project with the bundled .NET SDK and start the optional bridge scene:

```powershell
.\.tools\dotnet\dotnet.exe build EstunStudio.csproj
$env:DOTNET_ROOT = (Resolve-Path .tools/dotnet).Path
& .\.tools\godot\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64_console.exe --headless --path . res://Tests/WebBridge.tscn
```

In a separate terminal, from `Web`:

```powershell
npm ci
npm run build
npm start
```

Open `http://127.0.0.1:18740/hyper/`. For development use `npm run dev` with the gateway running; Vite uses port 5178 and proxies `/hyper/api` to the gateway.

`deploy/prepare_assets.py` regenerates lossless indexed browser geometry. `npm test` checks byte-for-byte source geometry preservation, FK, low-speed IK, sample candidates and all 152 native trajectory targets. Native `Tests/WeldChecks.tscn` checks all generated motion segments against original collision geometry.

## Server layout

- `/srv/hyper/releases/20260916` — packaged native core, frontend and Docker build.
- Current Docker image `estun-hyper:20260916-warning`; container `hyper-app-v6`.
- Older frontend-only releases remain at `/srv/hyper/releases/20260916-view-cube` and `/srv/hyper/releases/20260916-workspace` for rollback; the current complete release supersedes them.
- Internal port 18740, Docker network `swisscam-studio_default`; no additional public port.
- Current complete release: `/srv/hyper/releases/20260916-warning`. Caddy `handle_path /hyper/*` forwards to `hyper-app-v6:18740`; the existing fallback still forwards to `web:80`.
- Native Godot bridge stays on container loopback 18741. Node gateway handles uploads, API proxy and static assets.
- Container limits: 2,300 MB memory, 1.5 CPU, 256 processes; restart unless stopped.
- `/srv/hyper/Caddyfile.before-hyper` is the original HTTPS routing backup. `/srv/hyper/root-before.html` and `root-after.html` record unchanged root content.

Package using `python Web/deploy/package.py` after a successful frontend build, then build the included Dockerfile on the target Linux host. It installs pinned Godot 4.5.1 Mono, .NET 8 and Python 3.11/OpenCascade 7.7.2.2b2. Credentials are not part of this project.

Verification on the Linux server: STEP sample imports as 3 solids / 36 triangles / 36 potential candidates; the default Concave-off filter leaves 36 potential candidates and zero selected. Explicitly selecting the original eleven contact seams preserves **5 ready / 6 blocked / 152 source moves**, compressed to **91 joint + 5 linear commands**, with **160 playback samples**. A synthetic curved seam verifies circular output (41 source movements → 1 joint + 1 circular command). Native collision tests pass **336 checks**, weaving/export tests **1,625**, detailed-collision tests **213**, CAD tests **9**, and browser mathematical/state tests **23**. Visual browser interaction QA was not performed in this delivery.

Measured strict sample calculation: **36.46 seconds before → 15.04 seconds after**, including 0.21 seconds postprocessing. Collision checks retain the 2 mm margin and maximum summed joint step of 0.032°; exact movement/status fingerprints and 4,096 collision-result fingerprints agree before/after. Planner allocation falls from approximately 1.075 GB to 38.2 MB. STEP geometry optimizations preserve full JSON outputs; three representative local models improve by 2.29×, 5.93× and 3.76×. A persistent single CAD worker avoids repeated runtime startup; identical uploads use a bounded content-hash cache. The deployed warm sample import measured 0.089 seconds. Import jobs expose progress, cancellation and recovery after invalid files.

Warning-mode server verification: original contact-seam sample now produces **5 ready / 6 warning / 0 blocked**, **244 commands**, **308 playback samples** and **346 compressed contact frames** in **22.8 seconds**. Reported A1=26/A2=120 pose is clear. The new checks include **33 warning-planner**, **2,047 detailed-collision**, **1,658 exporter/weaving**, and **26 frontend math/state/drag** assertions. Original **336 strict collision/planner** checks still pass unchanged. Warning-mode timing includes reachable fallback search and collision timeline construction.

Original robot CAD attribution and redistribution caveats remain in the parent `THIRD_PARTY.md` and `Assets/Robot/REFERENCE.md`. Studio HDR source is Poly Haven (`studio_small_09`). Inter font is under SIL OFL. Browser dependencies retain their upstream licenses.
