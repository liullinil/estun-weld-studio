# ENCY HYPER - ESTUN

Published at **https://178.105.241.117/hyper/**. The existing SwissCAM at the origin root is independent and remains unchanged.

Source baseline: `liullinil/estun-weld-studio`, `main`, `e783203fb4ec32d592d9d4922d891032fe162100` (latest GitHub main on 2026-09-16).

The browser uses Three.js/WebGL 2, original ENCY S20-180 Pro CAD, native joint frames and limits, the mounted MIG/MAG torch and the original studio HDR. A lossless indexed mesh retains all **514,833 triangles**, every source position and normal; rendering vertices fall from 1,544,499 to 326,788. No geometry is decimated. The workspace header contains only the ENCY HYPER - ESTUN brand. TCP, Trace and all display controls are SwissCAM-style circular icons on the left of the viewport; exposure and resolution open compact controls beside their icons. Only the seam list scrolls. The pendant and preparation panel fit the available height, and the selected seam uses a bright cyan five-pixel line with a wider halo, whether selected in the list or in the viewport.

## Workflow

The sample loads automatically. Import STEP/STP, position the workpiece with Move / Rotate or millimetre/degree fields, review angle-filtered seams and exclude unwanted candidates. **Generate program** displays percentage, current operation and elapsed time, with a separate **Cancel** control. It runs the native C# planner, then fits and independently validates controller motion primitives. The generated trajectory appears immediately: dashed amber transfers and solid green welding moves.

**Simulate** becomes available only after a program is ready. **Pause** holds the pose and extinguishes the arc; press it again to resume. **Stop** discards the program and requires regeneration. During playback and pause the pendant displays **AUTO**, blocks physical jog keys and Go Home, and still allows speed and JOINT/WORLD/TOOL display changes. It returns to **MANUAL** on completion/stop. The red stop button latches on first press and releases on the second, without restarting motion. Go Home sits beside the speed slider. Drives, recording, Run and Reset controls have been removed. The ± keys sit on the physical housing beside the LCD, not within it. Space stops; Escape latches stop; losing focus pauses automatic playback and stops manual motion.

**Export** appears after successful generation. Lua output follows the user-supplied `commands.zip` **CODROID Lua** reference, using `movJ`, `movL`, `movC` and torch output **`setDO(1,1)` / `setDO(1,0)`**. Targets use the documented joint representation `jp`; tool and coordinate-system IDs default to 1. Linear/circular compression retains approximately constant orientation, fits within 0.5 mm, solves the same IK branch and revalidates collision samples. Unsupported compression retains original joint moves. Browser playback and JSON/CSV use the revalidated samples for the exported primitives. This is a heuristic program optimization, not a proof of globally optimal machining time; exact stops are retained because unvalidated blending would change the trajectory.

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
- Current Docker image `estun-hyper:20260916-workflow`; container `hyper-app-v3`.
- Older frontend-only releases remain at `/srv/hyper/releases/20260916-view-cube` and `/srv/hyper/releases/20260916-workspace` for rollback; the current complete release supersedes them.
- Internal port 18740, Docker network `swisscam-studio_default`; no additional public port.
- Current complete release: `/srv/hyper/releases/20260916-workflow`. Caddy `handle_path /hyper/*` forwards to `hyper-app-v3:18740`; the existing fallback still forwards to `web:80`.
- Native Godot bridge stays on container loopback 18741. Node gateway handles uploads, API proxy and static assets.
- Container limits: 2,300 MB memory, 1.5 CPU, 256 processes; restart unless stopped.
- `/srv/hyper/Caddyfile.before-hyper` is the original HTTPS routing backup. `/srv/hyper/root-before.html` and `root-after.html` record unchanged root content.

Package using `python Web/deploy/package.py` after a successful frontend build, then build the included Dockerfile on the target Linux host. It installs pinned Godot 4.5.1 Mono, .NET 8 and Python 3.11/OpenCascade 7.7.2.2b2. Credentials are not part of this project.

Verification on the Linux server: STEP sample imports as 3 solids / 36 triangles / 36 geometric candidates; default filter has 11 seams. The planner preserves **5 ready / 6 blocked / 152 source moves**, compressed to **91 joint + 5 linear commands**, with **160 playback samples**. A synthetic curved seam verifies circular output (41 source movements → 1 joint + 1 circular command). Native collision tests pass **336 checks**, postprocessor tests **396**, CAD tests **9**, and browser mathematical/state tests **16**. Visual browser interaction QA was not performed in this delivery.

Measured server sample calculation: **36.46 seconds before → 15.04 seconds after**, including 0.21 seconds postprocessing. Collision checks retain the 2 mm margin and maximum summed joint step of 0.032°; exact movement/status fingerprints and 4,096 collision-result fingerprints agree before/after. Planner allocation falls from approximately 1.075 GB to 38.2 MB. STEP geometry optimizations preserve full JSON outputs; three representative local models improve by 2.29×, 5.93× and 3.76×. A persistent single CAD worker avoids repeated runtime startup; identical uploads use a bounded content-hash cache. The deployed warm sample import measured 0.089 seconds. Import jobs expose progress, cancellation and recovery after invalid files.

Original robot CAD attribution and redistribution caveats remain in the parent `THIRD_PARTY.md` and `Assets/Robot/REFERENCE.md`. Studio HDR source is Poly Haven (`studio_small_09`). Inter font is under SIL OFL. Browser dependencies retain their upstream licenses.
