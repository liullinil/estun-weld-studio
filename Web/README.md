# ESTUN Weld Studio · Browser edition

Published at **https://178.105.241.117/hyper/**. The existing SwissCAM at the origin root is independent and remains unchanged.

Source baseline: `liullinil/estun-weld-studio`, `main`, `e783203fb4ec32d592d9d4922d891032fe162100` (latest GitHub main on 2026-09-16).

The browser uses Three.js/WebGL 2, original ENCY S20-180 Pro CAD, native joint frames and limits, the mounted MIG/MAG torch and the original studio HDR. A lossless indexed mesh retains all **514,833 triangles**, every source position and normal; rendering vertices fall from 1,544,499 to 326,788. No geometry is decimated. The UI follows the desktop workpiece panel and physical teach pendant.

## Workflow

The sample loads automatically. Import STEP/STP, position the workpiece with Move / Rotate or millimetre/degree fields, review angle-filtered seams and exclude unwanted candidates. **Generate program** runs the original C# `WeldPlanner` and `CollisionScene` on the server. Enable **DRIVES**, then **SIMULATE**. JSON and CSV exports preserve the desktop trajectory format and validation metadata.

Pendant controls support JOINT/WORLD/TOOL jog, home, taught points, speed override, stop and latched emergency stop. Display preferences and explicitly saved taught points are stored in browser local storage. Space stops motion, Escape latches E-stop, focus loss stops motion. Workpiece changes invalidate planning. Manual motion changes require replanning.

Rendering includes ACES, original HDR reflections, two shadow-casting studio lights, ambient occlusion, bloom, arc light, sparks, smoke and cooling weld beads. Display settings adjust resolution, exposure and effects. The view cube is ported from SwissCAM: transparent 104-pixel control, orientation synchronized with the camera, all six faces, twelve edges and eight corners, hover highlighting, and drag-to-orbit. Focus it for F/B/T/D/L/R face shortcuts, arrow-key rotation and Home/Enter isometric. Space and Escape retain the robot's stop controls. Hideable panels, cinema, image capture and fullscreen are available.

The browser rendering engine differs from Godot Forward+, so the lighting and postprocessing are not pixel-identical. Web import is limited to **64 MB**, and tessellated output to 60 MB. CAD import and planning require the server. The native planner accepts one active job at a time and has a 10-minute timeout. Uploaded STEP files and intermediate geometry are temporary and removed after import.

This remains an offline simulator. Sampled native collision planning is heuristic; it does not prove continuous collision freedom. Taught/manual jog motions are not collision-validated. The application has no hardware communication. Exported trajectories require calibrated cell data and a controller postprocessor before physical use.

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
- Docker image `estun-hyper:20260916-final2`; container `hyper-app-v2`.
- View-cube frontend update: `/srv/hyper/releases/20260916-view-cube`, reproducible image `estun-hyper:20260916-view-cube`. The running container received hashed assets followed by an atomic HTML replacement, preserving active planner jobs; use this newer image when recreating it.
- Internal port 18740, Docker network `swisscam-studio_default`; no additional public port.
- Caddy `handle_path /hyper/*` forwards to `hyper-app-v2:18740`. Existing fallback still forwards to `web:80`.
- Native Godot bridge stays on container loopback 18741. Node gateway handles uploads, API proxy and static assets.
- Container limits: 2,300 MB memory, 1.5 CPU, 256 processes; restart unless stopped.
- `/srv/hyper/Caddyfile.before-hyper` is the original HTTPS routing backup. `/srv/hyper/root-before.html` and `root-after.html` record unchanged root content.

Package using `python Web/deploy/package.py` after a successful frontend build, then build the included Dockerfile on the target Linux host. It installs pinned Godot 4.5.1 Mono, .NET 8 and Python 3.11/OpenCascade 7.7.2.2b2. Credentials are not part of this project.

Verification on the Linux server: STEP sample imports as 3 solids / 36 triangles / 36 geometric candidates; default filter has 11 seams; original planner returns **5 ready / 6 blocked / 152 moves / 24.643908 seconds**. Native Windows validation passes **336 checks**. The browser-side mathematical tests pass; visual browser interaction QA was not performed in this delivery.

Original robot CAD attribution and redistribution caveats remain in the parent `THIRD_PARTY.md` and `Assets/Robot/REFERENCE.md`. Studio HDR source is Poly Haven (`studio_small_09`). Inter font is under SIL OFL. Browser dependencies retain their upstream licenses.
