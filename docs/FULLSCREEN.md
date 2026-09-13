# Fullscreen performance fix

The regression reproduced when maximizing or entering fullscreen on the development NVIDIA RTX 3060 Laptop (6 GB). Rendering CPU time stayed near 1 ms while GPU time rose from roughly 14 ms to 265–305 ms. Process memory counters showed approximately 4.79 GiB dedicated plus 1.73 GiB shared GPU memory during the slowdown.

The cause was the reflection atlas: the project used 1024-pixel HDR cubemaps while retaining Godot's default capacity of 64 probes. Godot allocates the entire cube array, even though this scene uses one probe. Six RGBA16F faces with mip levels at that size reserve approximately **4 GiB** before frame buffers, shadows, geometry and other resources. A larger viewport pushed the working set into shared memory.

The atlas now has four slots, approximately **256 MiB**, while retaining the same 1024-pixel reflection resolution. No reflection texture detail is removed by this allocation fix.

An additional automatic 3D resolution policy retains approximately the existing 1440×900 window pixel workload on larger displays. It applies spatial FSR only to the 3D buffer. The canvas, fonts, pendant, view cube and CAD manipulation/picking remain at display resolution. Native and Manual options are available under EFFECTS. Automatic scaling responds to resize even when that panel is hidden; it does not change the user's reflection, MSAA, TAA or DOF choices.

The actual render extent is `Viewport.GetVisibleRect().Size * Viewport.GetStretchTransform().Scale`, because the former alone returns logical canvas dimensions under `canvas_items`. This also accounts for letterboxing and content scaling. No global VSync or driver settings are changed by the app.

`fullscreen-performance.json` records a short measurement of the actual shipping scene across windowed, maximized and fullscreen states. The optional `Tests/RenderBenchmark.tscn` runs three seconds of warm-up and four seconds of frame timing per phase, with VSync disabled for the benchmark only. It also measures rendering GPU/CPU times. These measurements describe one computer and scene rather than guaranteed FPS for arbitrary STEP assemblies.

Regression coverage: 71 resolution/memory checks, 86 viewport/effect checks, 45 gizmo checks and 69 shipping-scene checks passed after this change. The new suite asserts both the atlas memory ceiling and 3D pixel budgets through 4K, live resize, UI and camera/picking invariance, mode persistence, and backwards-compatible settings defaults.
