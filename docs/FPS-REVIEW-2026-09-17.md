# FPS regression review — 2026-09-17

The most recent automatic-orientation release does not change browser rendering
code. The previously public `hyper-app-v8` and current `hyper-app-v12` serve
byte-identical JS and CSS:

| Asset | Bytes | SHA-256 |
| --- | ---: | --- |
| `index-RtYNuDtd.js` | 680,974 | `d6b5a2338ee94a37d530dd9d808624302aef7ad479e8ebc0c39107178f01828d` |
| `index-DCzG5eYU.css` | 18,406 | `516e614e69e146f00e4c19d2886e8ff3e6157e926f2b138ed8f37f053ccac566` |

Orientation search runs during generation, on the server for the browser and a
worker task for desktop. Playback uses precomputed collision frames. Desktop
manual checks skip unchanged joint poses; browser checks skip unchanged poses
and limit requests to one in flight and at least 60 ms apart. These paths do not
repeat orientation search while orbiting the view.

## Incremental collision CPU measurement

An optional `Tests/CollisionPerformance.tscn` scene compares identical current
code with and without the new TCP containment parameter. It uses the actual
54 robot/torch volumes, source CAD self-collision meshes, plinth and three CAD
files: WeldingBracket.step, 49-1.igs and Part.IGS. Optimized .NET build,
`DOTNET_TieredCompilation=0`, warm-up, alternating variant order, 1,008 checks
per variant in each of 12 scenarios. This is a CPU microbenchmark, not FPS.

Static home checks at normal part placement have median times of approximately
0.058–0.059 ms with either variant. With Part.IGS deliberately centered around
the TCP, median per-check time rises from 0.0674 to 0.0846 ms; p95 from 0.1155 to
0.1494 ms. The largest measured median batch delta was 0.0261 ms per check.
This added cost does not explain a major rendering slowdown on the test CPU.
The diagnostic deliberately repeats static checks; the application normally
skips them when the joints do not change.

Raw results: `artifacts/fps-collision-comparison.json`.

## Earlier changes that can affect performance

Commit `2ba3e65` raises desktop defaults from 2x to 8x MSAA, enables SSIL,
raises shadow atlases from 4096 to 8192, and raises SSAO/SSIL and soft-shadow
quality. It also migrates saved settings to Native resolution and these Ultra
defaults once. These are real GPU workload increases and may substantially
affect a weaker GPU or higher-resolution display. Their exact FPS effect has
not been measured on the user's other computer.

The same commit adds browser hover seam raycasts on pointer movement and part
mesh raycasts on wheel events. Hover checks are skipped during pressed-button
orbit, gizmo drag and automatic playback. Standard Three.js mesh raycasts have
no spatial acceleration in this application.

Warmed installed-Three.js CPU measurements on an i5-11300H, excluding DOM and
GPU costs:

| Input | Hover median / p95 | Part wheel raycast median / p95 |
| --- | ---: | ---: |
| Actual sample: 36 seams, 36 triangles | 0.047 / 0.228 ms | 0.025 / 0.069 ms |
| Actual Part.IGS: 472 seams, 2,748 triangles | 0.123 / 0.259 ms | 0.228 / 0.363 ms |
| Synthetic 10,000 curved polylines | 6.223 / 8.366 ms | — |
| Synthetic 261,120-triangle part | — | 23.442 / 27.403 ms |

The synthetic cases demonstrate scaling; they are not measurements of the
user's CAD model or browser FPS. Reproduction script and raw output:
`artifacts/fps-picking-benchmark.mjs` and `.json`.

Commit `7c5d2a9` renders imported parts double-sided to preserve visible IGES
faces. This can add GPU rasterization/shading work, though opaque DoubleSide
does not by itself double draw calls. A controlled GPU comparison is needed
to quantify this effect. The actual robot mesh remains unchanged.

Browser defaults already include SSAO, bloom, 4-sample postprocessing and
device pixel ratio up to 2. At the same CSS viewport size, DPR 2 has four times
the pixels of DPR 1. Browser saved quality settings are local to each browser
profile, so a different computer may use different effective resolution.

## Diagnostic limitation

The existing browser FPS label uses the simulation timestep capped at 50 ms,
so it overstates FPS below 20. A proper frame-rate comparison must use raw
requestAnimationFrame timestamps. This review does not claim a GPU FPS A/B
measurement and does not change graphics defaults or deploy product changes.
