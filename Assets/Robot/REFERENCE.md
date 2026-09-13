# ESTUN S20-180 Pro: source geometry and conversion

The robot rendered by the application is the **actual segmented S20-180 Pro CAD geometry** published in the ENCY MachineMaker mechanism catalogue. The earlier illustrative procedural robot has been replaced. The application creates no replacement robot shells, decorative joint rings or invented tool. Its TCP is the native robot flange.

## Exact model source

Retrieved 2026-09-13 from the user-supplied public model viewer:

- Viewer: https://download.encycam.com/MachineMaker/online/index.html?app=Ency&mechanismGuid=%7BEB55546E-532E-43EE-84F0-31C344506178%7D
- Mechanism archive: https://webservices.encycam.com/SprutServerApi/DownloadMechanismFile?mechanismguid=%7BEB55546E-532E-43EE-84F0-31C344506178%7D
- Catalogue model identifier: `{EB55546E-532E-43EE-84F0-31C344506178}`.
- Catalogue `mechanism_params.json`: brand **Estun**, model **S20-180 Pro**, 6 axes, reach **1777.5 mm**, payload **20 kg**, flange **ISO 9409-1-50-6-M6**.

`OEM/ency-source.mmm` is the original archive. `OEM/ency/` contains its mechanism XML, metadata, preview and seven OSD geometry streams: base plus joints 1–6. The streams contain **514,833 source triangles** with original vertex normals. The mechanism XML supplies node transforms, visual-model transforms, joint directions and native tool connection.

`OEM/S20-180-Pro.meshbin` is a conversion of those seven source links into the application's runtime format. Mesh contours, source normals and separation into articulated links are retained. Coordinate and unit conversion maps the source CAD into Godot's right-handed Y-up metre coordinates. Triangle winding is changed to Godot's clockwise front-face convention. Surfaces of the same link and color are combined for efficient rendering; this does not simplify their geometry.

Source OSD colors are multiplied by the mechanism's XML `SkinColor` (0.6 grey) as in the source material definition. Godot PBR metallic, roughness and clearcoat values are rendering choices; they do not alter the underlying geometry.

## Cross-check sources

The user also supplied the following resources:

- https://robodk.com/robot/Estun/S20-180-Eco
- https://cdn.robodk.com/downloads-library/library-robots/Estun-S20-180-Eco.robot
- https://en.estun.com/?list_191/1799.html
- Current official Pro page: https://www.estun.com/xzjqr/381.html
- Current official Eco page: https://www.estun.com/xzjqr/382.html

The RoboDK **Eco** file was downloaded and its seven native link meshes and modified-DH calibration decoded for comparison. It is a different catalogue variant, retained only as research material. The application loads the **Pro** mesh asset. `OEM/S20-180-Eco.meshbin`, `source_meshes.json`, `.robot` and `.uncompressed` files are not needed by the running application and are excluded from the packaged build.

Specifications vary among catalogue revisions: the ENCY Pro metadata reports 58 kg and 0.1 mm repeatability, while the current official S-Pro table reports 66.5 kg and ±0.05 mm for S20-180. These figures are not used as simulated dynamics or accuracy claims.

## Scope

The visual geometry comes from the cited public catalogue; it is no longer an artistic approximation. Source-model kinematics are used for the simulated joint chain and numerical TCP control. This remains an offline desktop simulator, without physical-controller communication, collision validation or manufacturer-certified control behavior.

ESTUN names and the source CAD belong to their respective owners. The public model was downloaded for this requested local visualization. No independent ownership of the source CAD is claimed. Source archives and reference files remain in the workspace for provenance; the runtime contains the converted geometry required to display the selected robot.
