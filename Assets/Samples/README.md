# Welding bracket sample

`WeldingBracket.step` is an original, generated STEP assembly in millimetres. It contains three separate closed solids: a 250 × 190 × 10 mm base plate, a 210 × 8 × 115 mm vertical web and an 8 × 66 × 80 mm transverse rib. The overall height is 125 mm. This sample can be distributed with ESTUN Studio.

OpenCascade identifies the part contact curves and also the ordinary angular edges of each solid. All are candidates: geometry alone cannot determine design intent. The **85–95°** range includes the 90° contacts as well as exterior plate edges. Prefer contact seams and delete the exterior edges that do not require welding. A web seam passing behind the transverse rib is intentionally obstructed, so the offline planner can demonstrate collision rejection.

The import worker converts STEP units to metres and maps the CAD Z axis to the studio's Y axis. Position the bracket with the translate/rotate gizmo or the transform fields. The stored STEP source remains unchanged.

To regenerate the sample from the original OpenCascade construction:

```powershell
.tools/cad-python/python.exe tools/cad_import.py --make-sample --output Assets/Samples/WeldingBracket.step
```
