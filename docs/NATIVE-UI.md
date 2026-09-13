# Native UI and robot rendering update

The workspace now uses native window pixels rather than scaling a 1600×1000 canvas into arbitrary window sizes. Container layouts replace absolute child coordinates. Workpiece and pendant docks are independent, both can be hidden, and the camera reframes the freed viewport. P toggles the pendant. Text is 12–18 px in the docks, values use real font metrics, and the large decorative headline/overlay stack is removed.

## Image clarity

The previous user settings had TAA, DOF, fog, SSR, SSIL and 8× MSAA all enabled, plus automatic sub-native resolution. Revision 3 migrates once to Native 100%, TAA/DOF/fog/SSR/SSIL off and MSAA 2×. Exposure, bloom and welding preferences remain. Display shows exact internal render dimensions and scale; the footer repeats them. Auto and Manual remain explicit options for high-resolution displays.

The UI does not inherit 3D scaling. Native container tests verify no parent scale, aligned bounds and readable fonts at 1280×720,1440×900 and1920×1080. Hiding the pendant stops motion; reopening it cannot resume movement.

## Robot and GPU work

All 514,833 original CAD triangles remain unchanged. Exact position+normal indexing reduces vertex entries from 1,544,499 to326,788, a78.8% reduction. The depth/shadow mesh shares positions separately (267,840 entries). Collision checking continues to use the complete unchanged original triangle lists. Tests compare every source vertex and normal, winding, GPU arrays, collision arrays and FK.

The new dielectric coating shader uses subtle footprint-filtered roughness and geometric specular antialiasing. It does not add albedo speckles or displace CAD geometry. Fullscreen close-up inspection confirmed that tiny marks at the covers are genuine fastener holes and panel boundaries, not a replacement procedural model. Two lights cast shadows instead of five; fill lighting stays active, and shadow bias was adjusted. The 1024px reflection detail and bounded atlas allocation are retained.

This improves real-time physically based visualization; it is not a path-traced photograph or a guarantee of exact real-world appearance. The source model has no measured paint BRDF, calibrated camera or full production-cell scan.

## Measurements and checks

`native-ui-performance.json` records short GPU measurements on the same RTX3060 Laptop computer, VSync disabled for benchmarking. Window native:159.8FPS; fullscreen1920×1080 native:106.9FPS. These represent the tested studio scene, not guaranteed performance for every CAD model or long welding sequence. Normal interactive FPS can be capped by display VSync.

New suites passed:49complete mesh-preservation checks,45pendant layout checks,78workspace layout/dock checks. Updated scene69, viewport86, resolution71, gizmo45 and real STEP welding workflow28 checks passed. The view cube, part manipulation, planner and existing stop behavior remain functional.
