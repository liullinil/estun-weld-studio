"""Bounded STEP -> JSON geometry worker. OpenCascade handles BRep; the desktop app is C#.

STEP input is parsed as data, never executed. Geometry is transferred to millimetres
then converted to metres and Godot Y-up (X,Y,Z -> X,Z,-Y). Face adjacency comes
from the BRep, never guessed from tessellation triangles.
"""
from __future__ import annotations
import argparse
import itertools
import json
import math
import pathlib
import sys

from OCP.BRep import BRep_Tool, BRep_Builder
from OCP.BRepAdaptor import BRepAdaptor_Curve, BRepAdaptor_Surface
from OCP.BRepAlgoAPI import BRepAlgoAPI_Section
from OCP.BRepBuilderAPI import BRepBuilderAPI_MakeVertex
from OCP.BRepClass3d import BRepClass3d_SolidClassifier
from OCP.BRepExtrema import BRepExtrema_DistShapeShape
from OCP.BRepLProp import BRepLProp_SLProps
from OCP.BRepMesh import BRepMesh_IncrementalMesh
from OCP.GeomAPI import GeomAPI_ProjectPointOnSurf
from OCP.IFSelect import IFSelect_RetDone
from OCP.Interface import Interface_Static
from OCP.STEPControl import STEPControl_Reader, STEPControl_Writer, STEPControl_AsIs
from OCP.TopAbs import TopAbs_EDGE, TopAbs_FACE, TopAbs_SOLID, TopAbs_REVERSED, TopAbs_IN
from OCP.TopExp import TopExp, TopExp_Explorer
from OCP.TopLoc import TopLoc_Location
from OCP.TopTools import TopTools_IndexedDataMapOfShapeListOfShape, TopTools_IndexedMapOfShape
from OCP.TopoDS import TopoDS, TopoDS_Compound
from OCP.TColStd import TColStd_SequenceOfAsciiString
from OCP.gp import gp_Pnt

MAX_FACES = 20000
MAX_TRIANGLES = 750000
MAX_EDGE_CANDIDATES = 20000
MAX_SEAM_SAMPLES = 250000
CONTACT_SOLID_LIMIT = 30
CONTACT_FACE_LIMIT = 2500


def progress(message):
    print("PROGRESS " + message, file=sys.stderr, flush=True)


def xyz(p, scale=0.001):
    return [round(p.X() * scale, 9), round(p.Z() * scale, 9), round(-p.Y() * scale, 9)]


def dot(a, b):
    return sum(x * y for x, y in zip(a, b))


def normalized(a):
    length = math.sqrt(dot(a, a))
    return [v / length for v in a] if length > 1e-12 else [0, 1, 0]


def shapes(shape, kind):
    items = TopTools_IndexedMapOfShape()
    TopExp.MapShapes_s(shape, kind, items)
    return [items.FindKey(i) for i in range(1, items.Extent() + 1)]


def surface_normal(face, u, v):
    props = BRepLProp_SLProps(BRepAdaptor_Surface(face), u, v, 1, 1e-8)
    if not props.IsNormalDefined():
        raise ValueError("Surface normal is undefined")
    result = xyz(props.Normal(), 1.0)
    if face.Orientation() == TopAbs_REVERSED:
        result = [-x for x in result]
    return normalized(result)


def face_normal_at(face, p):
    # BRep_Tool.Surface returns a surface in the face's located coordinates.
    surface = BRep_Tool.Surface_s(face)
    projection = GeomAPI_ProjectPointOnSurf(p, surface)
    if projection.NbPoints() == 0:
        raise ValueError("Cannot project seam to face")
    u, v = projection.LowerDistanceParameters()
    return surface_normal(face, u, v)


def sample_edge(edge):
    curve = BRepAdaptor_Curve(edge)
    first, last = curve.FirstParameter(), curve.LastParameter()
    if not math.isfinite(first) or not math.isfinite(last) or last <= first:
        return []
    # Estimate curve length then sample densely enough for torch tracking, also
    # retaining curved-edge topology (rather than just the end vertices).
    rough = [curve.Value(first + (last - first) * i / 16) for i in range(17)]
    length = sum(rough[i - 1].Distance(rough[i]) for i in range(1, len(rough)))
    if length < .5:
        return []
    count = min(512, max(2, math.ceil(length / 4) + 1))
    return [curve.Value(first + (last - first) * i / (count - 1)) for i in range(count)]


def candidate(points, face_a, face_b, kind, whole_shape=None):
    if len(points) < 2:
        return None
    na, nb, angles = [], [], []
    for point in points:
        a, b = face_normal_at(face_a, point), face_normal_at(face_b, point)
        angle = math.degrees(math.acos(max(-1.0, min(1.0, dot(a, b)))))
        na.extend(a)
        nb.extend(b)
        angles.append(angle)
    if max(angles) < 2 or min(angles) > 178:
        return None
    concave = kind == "Part contact"
    if whole_shape is not None and not concave:
        try:
            mid = len(points) // 2
            a, b = na[mid * 3:mid * 3 + 3], nb[mid * 3:mid * 3 + 3]
            # At an internal edge the mixed quadrant lies inside the material.
            # Inverse Godot conversion: x,-z,y. Probe only 0.05 mm away.
            d = normalized([a[i] - b[i] for i in range(3)])
            p = points[mid]
            probe = gp_Pnt(p.X() + d[0] * .05, p.Y() - d[2] * .05, p.Z() + d[1] * .05)
            concave = BRepClass3d_SolidClassifier(whole_shape, probe, 1e-6).State() == TopAbs_IN
        except Exception:
            pass
    return {
        "id": "", "kind": kind, "points": list(itertools.chain.from_iterable(xyz(p) for p in points)),
        "normalsA": na, "normalsB": nb,
        "angleDegrees": sum(angles) / len(angles),
        "minAngleDegrees": min(angles), "maxAngleDegrees": max(angles), "concave": concave,
    }


def seam_key(seam):
    p = seam["points"]
    a, b = tuple(round(v, 5) for v in p[:3]), tuple(round(v, 5) for v in p[-3:])
    # Mean is independent of curve direction, including even sample counts.
    mean = tuple(round(sum(p[i::3]) / (len(p) // 3), 4) for i in range(3))
    return tuple(sorted([a, b])), mean


def triangulate(shape, faces):
    progress(f"Tessellating {len(faces):,} CAD faces…")
    mesher = BRepMesh_IncrementalMesh(shape, .18, False, .18, True)
    if not mesher.IsDone():
        raise ValueError("OpenCascade tessellation failed")
    vertices, normals, indices = [], [], []
    for face in faces:
        location = TopLoc_Location()
        tri = BRep_Tool.Triangulation_s(face, location)
        if tri is None:
            continue
        if len(indices) // 3 + tri.NbTriangles() > MAX_TRIANGLES:
            raise ValueError(f"Model exceeds {MAX_TRIANGLES:,} triangles; simplify STEP before import")
        offset = len(vertices) // 3
        transform = location.Transformation()
        fallback_normals = [[0., 0., 0.] for _ in range(tri.NbNodes())]
        local_points = []
        reverse = face.Orientation() == TopAbs_REVERSED
        for i in range(1, tri.NbNodes() + 1):
            p = tri.Node(i).Transformed(transform)
            v = xyz(p)
            vertices.extend(v)
            local_points.append(v)
        for i in range(1, tri.NbTriangles() + 1):
            a, b, c = [x - 1 for x in tri.Triangle(i).Get()]
            if reverse:
                b, c = c, b
            # OpenCascade is CCW; Godot front faces are clockwise.
            indices.extend([offset + a, offset + c, offset + b])
            v0, v1, v2 = local_points[a], local_points[b], local_points[c]
            e = [v1[j] - v0[j] for j in range(3)]
            f = [v2[j] - v0[j] for j in range(3)]
            n = [e[1] * f[2] - e[2] * f[1], e[2] * f[0] - e[0] * f[2], e[0] * f[1] - e[1] * f[0]]
            for vertex in (a, b, c):
                for j in range(3):
                    fallback_normals[vertex][j] += n[j]
        for i in range(1, tri.NbNodes() + 1):
            try:
                uv = tri.UVNode(i)
                normal = surface_normal(face, uv.X(), uv.Y())
            except Exception:
                normal = normalized(fallback_normals[i - 1])
            normals.extend(normal)
    if not indices:
        raise ValueError("STEP file contains no tessellatable surfaces")
    return vertices, normals, indices


def recognize(shape, faces, solids, warnings):
    progress("Recognizing seam angles from BRep face adjacency…")
    mapping = TopTools_IndexedDataMapOfShapeListOfShape()
    TopExp.MapShapesAndAncestors_s(shape, TopAbs_EDGE, TopAbs_FACE, mapping)
    if mapping.Extent() > MAX_EDGE_CANDIDATES:
        warnings.append(f"Only the first {MAX_EDGE_CANDIDATES:,} BRep edges were checked; simplify this model for complete recognition.")
    seams = {}
    skipped = 0
    seam_samples = 0
    for i in range(1, min(mapping.Extent(), MAX_EDGE_CANDIDATES) + 1):
        try:
            edge = TopoDS.Edge_s(mapping.FindKey(i))
            adjacent = []
            for f in mapping.FindFromIndex(i):
                if not any(f.IsSame(other) for other in adjacent):
                    adjacent.append(TopoDS.Face_s(f))
            if len(adjacent) != 2:
                continue
            item = candidate(sample_edge(edge), adjacent[0], adjacent[1], "Topology edge", shape)
            if item:
                seams[seam_key(item)] = item
                seam_samples += len(item["points"]) // 3
                if seam_samples > MAX_SEAM_SAMPLES:
                    warnings.append("Seam recognition reached the 250,000 sample limit; import a smaller subassembly for complete recognition.")
                    break
        except Exception:
            skipped += 1
    if 1 < len(solids) <= CONTACT_SOLID_LIMIT and len(faces) <= CONTACT_FACE_LIMIT and seam_samples <= MAX_SEAM_SAMPLES:
        progress(f"Checking contact seams between {len(solids)} solids…")
        for solid_a, solid_b in itertools.combinations(solids, 2):
            try:
                separation = BRepExtrema_DistShapeShape(solid_a, solid_b)
                if not separation.IsDone() or separation.Value() > .02:
                    continue
                section = BRepAlgoAPI_Section(solid_a, solid_b, False)
                section.ComputePCurveOn1(True)
                section.ComputePCurveOn2(True)
                section.Approximation(True)
                section.Build()
                if not section.IsDone():
                    continue
                faces_a = [TopoDS.Face_s(f) for f in shapes(solid_a, TopAbs_FACE)]
                faces_b = [TopoDS.Face_s(f) for f in shapes(solid_b, TopAbs_FACE)]
                for edge in shapes(section.Shape(), TopAbs_EDGE):
                    points = sample_edge(TopoDS.Edge_s(edge))
                    if not points:
                        continue
                    probe = BRepBuilderAPI_MakeVertex(points[len(points) // 2]).Vertex()
                    touching = []
                    for part_faces in (faces_a, faces_b):
                        group = []
                        for face in part_faces:
                            distance = BRepExtrema_DistShapeShape(probe, face)
                            if distance.IsDone() and distance.Value() <= .02:
                                group.append(face)
                        touching.append(group)
                    for fa, fb in itertools.product(*touching):
                        item = candidate(points, fa, fb, "Part contact")
                        if item:
                            seams[seam_key(item)] = item
            except Exception:
                skipped += 1
    elif len(solids) > 1:
        warnings.append("Part contact recognition was skipped for this large assembly; BRep edge candidates are available. Import a smaller welding subassembly to detect part contacts.")
    if skipped:
        warnings.append(f"{skipped} degenerate or unsupported edge/contact operations were skipped.")
    result = sorted(seams.values(), key=lambda s: (s["kind"] != "Part contact", not s["concave"], s["points"]))
    for i, seam in enumerate(result):
        seam["id"] = f"S{i + 1:03d}"
    return result


def import_step(source, destination):
    if source.suffix.lower() not in (".stp", ".step"):
        raise ValueError("Input must be a STEP file (.step or .stp)")
    if source.stat().st_size > 256 * 1024 * 1024:
        raise ValueError("STEP file exceeds 256 MB import limit")
    progress("Reading STEP product geometry…")
    Interface_Static.SetCVal_s("xstep.cascade.unit", "MM")
    reader = STEPControl_Reader()
    if reader.ReadFile(str(source)) != IFSelect_RetDone:
        raise ValueError("Invalid or unsupported STEP file")
    length_units, angle_units, solid_angle_units = (TColStd_SequenceOfAsciiString() for _ in range(3))
    reader.FileUnits(length_units, angle_units, solid_angle_units)
    source_unit = ", ".join(length_units.Value(i).ToCString() for i in range(1, length_units.Length() + 1)) or "STEP units"
    reader.SetSystemLengthUnit(1.0)
    if reader.TransferRoots() == 0:
        raise ValueError("STEP contains no transferable BRep roots")
    shape = reader.OneShape()
    if shape.IsNull():
        raise ValueError("STEP contains no geometry")
    faces = [TopoDS.Face_s(f) for f in shapes(shape, TopAbs_FACE)]
    solids = shapes(shape, TopAbs_SOLID)
    if len(faces) > MAX_FACES:
        raise ValueError(f"Model exceeds {MAX_FACES:,} faces; import a smaller subassembly")
    vertices, normals, indices = triangulate(shape, faces)
    warnings = []
    seams = recognize(shape, faces, solids, warnings)
    result = {"schemaVersion": 1, "sourceUnit": source_unit, "solidCount": len(solids), "faceCount": len(faces), "vertices": vertices, "normals": normals, "indices": indices, "seams": seams, "warnings": warnings}
    progress(f"Imported {len(indices) // 3:,} triangles and {len(seams)} seam candidates")
    destination.write_text(json.dumps(result, separators=(",", ":"), allow_nan=False), encoding="utf-8")


def make_sample(destination):
    from OCP.BRepPrimAPI import BRepPrimAPI_MakeBox
    # Millimetres: horizontal plate, vertical web, plus a short transverse rib.
    # Separate closed solids deliberately retain physically meaningful contact seams.
    builder = BRep_Builder()
    assembly = TopoDS_Compound()
    builder.MakeCompound(assembly)
    for origin, size in [((-125, -95, 0), (250, 190, 10)), ((-105, -4, 10), (210, 8, 115)), ((-4, 4, 10), (8, 66, 80))]:
        builder.Add(assembly, BRepPrimAPI_MakeBox(gp_Pnt(*origin), *size).Shape())
    writer = STEPControl_Writer()
    writer.Transfer(assembly, STEPControl_AsIs)
    if writer.Write(str(destination)) != IFSelect_RetDone:
        raise ValueError("Cannot write sample STEP")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=pathlib.Path)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--make-sample", action="store_true")
    args = parser.parse_args()
    try:
        if args.make_sample:
            make_sample(args.output)
        elif args.input:
            import_step(args.input.resolve(strict=True), args.output)
        else:
            parser.error("--input is required for import")
    except Exception as exc:
        print(f"CAD import failed: {type(exc).__name__}: {exc}", flush=True)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
