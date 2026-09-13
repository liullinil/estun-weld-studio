# Welding workspace validation

Validated 2026-09-13 on Windows x64 with Godot 4.5.1 .NET / SDK 8.0.425 and NVIDIA RTX 3060 Laptop GPU.

| Runtime suite | Assertions passed |
| --- | ---: |
| RobotChecks | 69 |
| SceneChecks | 53 |
| ViewportChecks | 74 |
| GizmoChecks | 45 |
| CadImportChecks | 11 |
| WeldChecks | 336 |
| WeldingWorkflowChecks | 25 |
| **Total Godot runtime assertions** | **613** |

Seven additional native OpenCascade tests passed, including STEP unit conversion, curved and angular seams, assembly placement, contact recognition, winding and invalid-input rejection.

The final sample uses the original 514,833-triangle robot and 54 conservative collision volumes, including the complete torch, plus the mounting platform and floor. It produced 5 ready and 6 blocked seams, 152 checked movements and approximately 25 s nominal full-speed motion. The 210 mm seam S003 is ready. Planning took 75.1 s during the final detailed collision suite. Every emitted segment was independently checked again for collision and forward-kinematic agreement.

The shipping workspace was tested end-to-end for import, selection, planning, busy-state locks, wrong-start rejection, enable and E-stop, cancellation, seam deletion and real gizmo dragging that invalidates the program.

The Windows release built without errors or warnings and includes a self-contained .NET runtime and separate isolated CAD runtime. The exported executable imported the sample, generated its checked program and rendered active welding with visible sparks and deposited bead. Screenshots are in this directory.

These checks establish software behavior for the tested cases. They do not prove global route optimality, exact continuous collision freedom for arbitrary geometry, physical welding quality or hardware safety certification. See README for the collision approximation, sampling limits and controller postprocessing requirement.
