"""Bounded STEP/IGES -> JSON geometry worker. OpenCascade handles BRep; desktop is C#.

CAD input is parsed as data, never executed. Geometry is transferred to millimetres
then converted to metres and Godot Y-up (X,Y,Z -> X,Z,-Y). Face adjacency comes
from the BRep, never guessed from tessellation triangles.
"""
from __future__ import annotations
import argparse
import gc
import itertools
import json
import math
import pathlib
import sys

from OCP.BRep import BRep_Tool, BRep_Builder
from OCP.BRepAdaptor import BRepAdaptor_Curve, BRepAdaptor_Surface
from OCP.BRepAlgoAPI import BRepAlgoAPI_Section
from OCP.BRepBndLib import BRepBndLib
from OCP.BRepBuilderAPI import BRepBuilderAPI_MakeVertex, BRepBuilderAPI_Sewing
from OCP.BRepCheck import BRepCheck_Analyzer
from OCP.ShapeFix import ShapeFix_Solid
from OCP.BRepClass3d import BRepClass3d_SolidClassifier
from OCP.BRepExtrema import BRepExtrema_DistShapeShape
from OCP.BRepLProp import BRepLProp_SLProps
from OCP.BRepMesh import BRepMesh_IncrementalMesh
from OCP.Bnd import Bnd_Box
from OCP.GeomAPI import GeomAPI_ProjectPointOnSurf
from OCP.GeomAbs import GeomAbs_Plane
from OCP.IFSelect import IFSelect_RetDone
from OCP.Interface import Interface_Static
from OCP.STEPControl import STEPControl_Reader, STEPControl_Writer, STEPControl_AsIs
from OCP.IGESControl import IGESControl_Reader
from OCP.TopAbs import TopAbs_EDGE, TopAbs_FACE, TopAbs_SOLID, TopAbs_SHELL, TopAbs_REVERSED, TopAbs_IN
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
IGES_SEWING_TOLERANCE_MM = .001


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


class FaceNormals:
    """Reuse native surface evaluators; a plane's normal is constant everywhere."""
    def __init__(self, face):
        self.adaptor = BRepAdaptor_Surface(face)
        self.props = BRepLProp_SLProps(self.adaptor, 1, 1e-8)
        self.reverse = face.Orientation() == TopAbs_REVERSED
        self.surface = None
        self.constant = None
        if self.adaptor.GetType() == GeomAbs_Plane:
            self.constant = self.at_uv(0., 0.)
        else:
            # BRep_Tool returns the surface in the face's located coordinates.
            self.surface = BRep_Tool.Surface_s(face)

    def at_uv(self, u, v):
        if self.constant is not None:
            return self.constant
        self.props.SetParameters(u, v)
        if not self.props.IsNormalDefined():
            raise ValueError("Surface normal is undefined")
        result = xyz(self.props.Normal(), 1.0)
        return normalized([-x for x in result] if self.reverse else result)

    def at_point(self, point):
        if self.constant is not None:
            return self.constant
        projection = GeomAPI_ProjectPointOnSurf(point, self.surface)
        if projection.NbPoints() == 0:
            raise ValueError("Cannot project seam to face")
        return self.at_uv(*projection.LowerDistanceParameters())


class FaceNormalCache:
    def __init__(self):
        self.faces = TopTools_IndexedMapOfShape()
        self.evaluators = {}

    def __getitem__(self, face):
        # TopoDS Python wrappers compare by object identity; OpenCascade's map
        # correctly matches fresh wrappers of the same located BRep face.
        key = (self.faces.Add(face), face.Orientation())
        if key not in self.evaluators:
            self.evaluators[key] = FaceNormals(face)
        return self.evaluators[key]


def bounds(shape):
    box = Bnd_Box()
    # BRep bounds contain the exact shape and its tolerance; no tessellation
    # quality is changed by using them solely as a conservative broad phase.
    try:
        BRepBndLib.Add_s(shape, box, False)
        return box.Get() if not box.IsVoid() and not box.IsWhole() else None
    except Exception:
        # An unsupported bound must never exclude a possible contact.
        return None


def bounds_overlap(a, b, tolerance=.02):
    return a is None or b is None or all(a[i] <= b[i + 3] + tolerance and b[i] <= a[i + 3] + tolerance for i in range(3))


def bounds_contains_point(box, point, tolerance=.02):
    return box is None or all(box[i] - tolerance <= value <= box[i + 3] + tolerance for i, value in enumerate((point.X(), point.Y(), point.Z())))


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


def candidate(points, face_a, face_b, kind, whole_shape=None, evaluators=None, classifier=None):
    if len(points) < 2:
        return None
    na, nb, angles = [], [], []
    normal_a = evaluators[face_a].at_point if evaluators is not None else FaceNormals(face_a).at_point
    normal_b = evaluators[face_b].at_point if evaluators is not None else FaceNormals(face_b).at_point
    for point in points:
        a, b = normal_a(point), normal_b(point)
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
            if classifier is None:
                classifier = BRepClass3d_SolidClassifier(whole_shape)
            classifier.Perform(probe, 1e-6)
            concave = classifier.State() == TopAbs_IN
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


def heal_iges_surfaces(shape, warnings):
    """Connect independent IGES patches without merging existing solid assemblies.

    Keep surface geometry and every face; use a fixed, capped 1 micron tolerance
    in normalized millimetres. Closed shells acquire consistent outward normals.
    Open shells remain surfaces and are never filled or presented as closed solids.
    """
    if shapes(shape, TopAbs_SOLID):
        return shape, None
    original_faces = shapes(shape, TopAbs_FACE)
    if not original_faces:
        return shape, None
    progress(f"Sewing {len(original_faces):,} IGES faces at {IGES_SEWING_TOLERANCE_MM:g} mm…")
    sewing = BRepBuilderAPI_Sewing(IGES_SEWING_TOLERANCE_MM, True, True, True, False)
    sewing.SetMaxTolerance(IGES_SEWING_TOLERANCE_MM)
    sewing.SetLocalTolerancesMode(False)
    sewing.Add(shape)
    sewing.Perform()
    sewn = sewing.SewedShape()
    if sewn.IsNull() or len(shapes(sewn, TopAbs_FACE)) != len(original_faces) or sewing.NbDeletedFaces() or not BRepCheck_Analyzer(sewn).IsValid():
        warnings.append("IGES surface sewing could not preserve every face; original surfaces retained.")
        return shape, None
    builder = BRep_Builder()
    result = TopoDS_Compound()
    builder.MakeCompound(result)
    covered = TopTools_IndexedMapOfShape()
    closed_solids = 0
    for item in shapes(sewn, TopAbs_SHELL):
        shell = TopoDS.Shell_s(item)
        shell_faces = shapes(shell, TopAbs_FACE)
        output = shell
        if shell.Closed():
            solid = ShapeFix_Solid().SolidFromShell(shell)
            if not solid.IsNull() and BRepCheck_Analyzer(solid).IsValid() and len(shapes(solid, TopAbs_FACE)) == len(shell_faces):
                output = solid
                closed_solids += 1
        builder.Add(result, output)
        for face in shell_faces:
            covered.Add(face)
    for face in shapes(sewn, TopAbs_FACE):
        if not covered.Contains(face):
            builder.Add(result, face)
    if len(shapes(result, TopAbs_FACE)) != len(original_faces):
        warnings.append("IGES shell reconstruction could not preserve every face; sewn surfaces retained.")
        result = sewn
        closed_solids = 0
    metadata = {"sewn": True, "toleranceMm": IGES_SEWING_TOLERANCE_MM,
                "sourceFaces": len(original_faces), "retainedFaces": len(shapes(result, TopAbs_FACE)),
                "closedSolids": closed_solids, "freeEdges": sewing.NbFreeEdges()}
    return result, metadata


def triangulate(shape, faces):
    progress(f"Tessellating {len(faces):,} CAD faces…")
    mesher = BRepMesh_IncrementalMesh(shape, .18, False, .18, True)
    if not mesher.IsDone():
        raise ValueError("OpenCascade tessellation failed")
    vertices, normals, indices = [], [], []
    missing_faces = []
    for face_index, face in enumerate(faces):
        location = TopLoc_Location()
        tri = BRep_Tool.Triangulation_s(face, location)
        if tri is None or tri.NbTriangles() == 0:
            missing_faces.append(face_index + 1)
            continue
        if len(indices) // 3 + tri.NbTriangles() > MAX_TRIANGLES:
            raise ValueError(f"Model exceeds {MAX_TRIANGLES:,} triangles; simplify STEP before import")
        offset = len(vertices) // 3
        transform = location.Transformation()
        local_points = []
        local_normals = []
        reverse = face.Orientation() == TopAbs_REVERSED
        try:
            evaluator = FaceNormals(face)
        except Exception:
            evaluator = None
        for i in range(1, tri.NbNodes() + 1):
            p = tri.Node(i).Transformed(transform)
            v = xyz(p)
            vertices.extend(v)
            local_points.append(v)
            try:
                uv = tri.UVNode(i)
                local_normals.append(evaluator.at_uv(uv.X(), uv.Y()))
            except Exception:
                local_normals.append(None)
        fallback_normals = [[0., 0., 0.] for _ in local_points] if any(n is None for n in local_normals) else None
        for i in range(1, tri.NbTriangles() + 1):
            a, b, c = [x - 1 for x in tri.Triangle(i).Get()]
            if reverse:
                b, c = c, b
            # OpenCascade is CCW; Godot front faces are clockwise.
            indices.extend([offset + a, offset + c, offset + b])
            if fallback_normals is None:
                continue
            v0, v1, v2 = local_points[a], local_points[b], local_points[c]
            e = [v1[j] - v0[j] for j in range(3)]
            f = [v2[j] - v0[j] for j in range(3)]
            n = [e[1] * f[2] - e[2] * f[1], e[2] * f[0] - e[0] * f[2], e[0] * f[1] - e[1] * f[0]]
            for vertex in (a, b, c):
                for j in range(3):
                    fallback_normals[vertex][j] += n[j]
        for i, normal in enumerate(local_normals):
            normals.extend(normal if normal is not None else normalized(fallback_normals[i]))
    if missing_faces:
        raise ValueError(f"OpenCascade could not tessellate {len(missing_faces)} of {len(faces)} CAD faces (face IDs: {missing_faces[:12]}). Import stopped instead of displaying an incomplete model.")
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
    evaluators = FaceNormalCache()
    try:
        classifier = BRepClass3d_SolidClassifier(shape)
    except Exception:
        classifier = None
    for i in range(1, min(mapping.Extent(), MAX_EDGE_CANDIDATES) + 1):
        try:
            edge = TopoDS.Edge_s(mapping.FindKey(i))
            adjacent = []
            for f in mapping.FindFromIndex(i):
                if not any(f.IsSame(other) for other in adjacent):
                    adjacent.append(TopoDS.Face_s(f))
            if len(adjacent) != 2:
                continue
            item = candidate(sample_edge(edge), adjacent[0], adjacent[1], "Topology edge", shape, evaluators, classifier)
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
        solid_data = [(solid, bounds(solid), [(TopoDS.Face_s(face), bounds(face)) for face in shapes(solid, TopAbs_FACE)]) for solid in solids]
        for (solid_a, box_a, faces_a), (solid_b, box_b, faces_b) in itertools.combinations(solid_data, 2):
            if not bounds_overlap(box_a, box_b):
                continue
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
                for edge in shapes(section.Shape(), TopAbs_EDGE):
                    points = sample_edge(TopoDS.Edge_s(edge))
                    if not points:
                        continue
                    midpoint = points[len(points) // 2]
                    probe = BRepBuilderAPI_MakeVertex(midpoint).Vertex()
                    touching = []
                    for part_faces in (faces_a, faces_b):
                        group = []
                        for face, face_box in part_faces:
                            if not bounds_contains_point(face_box, midpoint):
                                continue
                            distance = BRepExtrema_DistShapeShape(probe, face)
                            if distance.IsDone() and distance.Value() <= .02:
                                group.append(face)
                        touching.append(group)
                    for fa, fb in itertools.product(*touching):
                        item = candidate(points, fa, fb, "Part contact", evaluators=evaluators)
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
    # Keep the original entry point for desktop integrations; dispatch by CAD format.
    if source.suffix.lower() not in (".stp", ".step", ".igs", ".iges"):
        raise ValueError("Input must be a STEP or IGES file (.step, .stp, .iges, .igs)")
    if source.stat().st_size > 256 * 1024 * 1024:
        raise ValueError("CAD file exceeds 256 MB import limit")
    is_iges = source.suffix.lower() in (".igs", ".iges")
    format_name = "IGES" if is_iges else "STEP"
    progress(f"Reading {format_name} product geometry…")
    Interface_Static.SetCVal_s("xstep.cascade.unit", "MM")
    reader = IGESControl_Reader() if is_iges else STEPControl_Reader()
    if reader.ReadFile(str(source)) != IFSelect_RetDone:
        raise ValueError(f"Invalid or unsupported {format_name} file")
    if is_iges:
        section = reader.IGESModel().GlobalSection()
        unit = section.UnitName()
        source_unit = unit.ToCString() if unit is not None else "IGES units"
    else:
        length_units, angle_units, solid_angle_units = (TColStd_SequenceOfAsciiString() for _ in range(3))
        reader.FileUnits(length_units, angle_units, solid_angle_units)
        source_unit = ", ".join(length_units.Value(i).ToCString() for i in range(1, length_units.Length() + 1)) or "STEP units"
        reader.SetSystemLengthUnit(1.0)
    if reader.TransferRoots() == 0:
        raise ValueError(f"{format_name} contains no transferable BRep roots")
    shape = reader.OneShape()
    if shape.IsNull():
        raise ValueError(f"{format_name} contains no geometry")
    faces = [TopoDS.Face_s(f) for f in shapes(shape, TopAbs_FACE)]
    solids = shapes(shape, TopAbs_SOLID)
    if len(faces) > MAX_FACES:
        raise ValueError(f"Model exceeds {MAX_FACES:,} faces; import a smaller subassembly")
    warnings = []
    healing = None
    if is_iges:
        shape, healing = heal_iges_surfaces(shape, warnings)
        faces = [TopoDS.Face_s(f) for f in shapes(shape, TopAbs_FACE)]
        solids = shapes(shape, TopAbs_SOLID)
    vertices, normals, indices = triangulate(shape, faces)
    if is_iges and not solids:
        warnings.append("IGES contains surfaces rather than closed solids; solid-contact seam recognition is unavailable for these surfaces.")
    seams = recognize(shape, faces, solids, warnings)
    result = {"schemaVersion": 1, "sourceUnit": source_unit, "solidCount": len(solids), "faceCount": len(faces), "vertices": vertices, "normals": normals, "indices": indices, "seams": seams, "warnings": warnings}
    if healing is not None:
        result["healing"] = healing
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
    parser.add_argument("--output", type=pathlib.Path)
    parser.add_argument("--make-sample", action="store_true")
    parser.add_argument("--daemon", action="store_true", help="Keep OpenCascade loaded; accept stdin JSONL {id,input,output}")
    args = parser.parse_args()
    if args.daemon:
        return serve()
    if args.output is None:
        parser.error("--output is required for import or sample generation")
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


def serve():
    """Private, sequential gateway protocol; native OCP diagnostics may share stdout.

    Only CAD_READY and CAD_RESULT lines are protocol messages. Input/output paths
    are provided by the trusted local gateway, not directly by browser clients.
    Killing this process cancels native operations; a replacement can be warmed
    up immediately without retaining any previous document or request state.
    """
    print("CAD_READY", flush=True)
    for line in sys.stdin:
        if not line.strip():
            continue
        request_id = None
        try:
            request = json.loads(line)
            request_id = request.get("id")
            source = pathlib.Path(request["input"]).resolve(strict=True)
            destination = pathlib.Path(request["output"])
            import_step(source, destination)
            response = {"id": request_id, "ok": True}
        except Exception as exc:
            response = {"id": request_id, "ok": False, "error": f"CAD import failed: {type(exc).__name__}: {exc}"}
        # Native shape references are request-local and drop before the next job.
        gc.collect()
        print("CAD_RESULT " + json.dumps(response, separators=(",", ":")), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
