"""Native OpenCascade integration checks. Run with .tools/cad-python/python.exe."""
import importlib.util
import json
import math
import pathlib
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("cad_import", ROOT / "tools/cad_import.py")
cad = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cad)


class CadImportChecks(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory(prefix="estun-cad-test-")
        cls.directory = pathlib.Path(cls.temp.name)
        cad.import_step(ROOT / "Assets/Samples/WeldingBracket.step", cls.directory / "bracket.json")
        cls.bracket = json.loads((cls.directory / "bracket.json").read_text())

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    def test_closed_solid_mesh_and_scale(self):
        d = self.bracket
        self.assertEqual((d["solidCount"], d["faceCount"]), (3, 18))
        self.assertEqual(len(d["indices"]) // 3, 36)
        points = list(zip(*[iter(d["vertices"])] * 3))
        extent = [max(p[i] for p in points) - min(p[i] for p in points) for i in range(3)]
        for actual, expected in zip(extent, [.25, .125, .19]):
            self.assertAlmostEqual(actual, expected, places=7)
        self.assertTrue(all(math.isfinite(v) for v in d["vertices"]))
        self.assertEqual(len(d["vertices"]), len(d["normals"]))

    def test_real_contact_seams_and_angle_filter(self):
        seams = self.bracket["seams"]
        contacts = [s for s in seams if s["kind"] == "Part contact"]
        self.assertGreaterEqual(len(contacts), 8)
        self.assertEqual(len(seams), len({s["id"] for s in seams}))
        self.assertTrue(all(85 <= s["minAngleDegrees"] <= s["maxAngleDegrees"] <= 95 for s in seams))
        self.assertFalse(any(40 <= s["angleDegrees"] <= 50 for s in seams))
        self.assertTrue(all(len(s["points"]) == len(s["normalsA"]) == len(s["normalsB"]) for s in seams))
        self.assertEqual(self.bracket["warnings"], [])

    def test_clockwise_godot_winding_and_outward_normals(self):
        d = self.bracket
        points = list(zip(*[iter(d["vertices"])] * 3))
        normals = list(zip(*[iter(d["normals"])] * 3))
        for a, b, c in zip(*[iter(d["indices"])] * 3):
            e = [points[b][i] - points[a][i] for i in range(3)]
            f = [points[c][i] - points[a][i] for i in range(3)]
            cross = [e[1]*f[2]-e[2]*f[1], e[2]*f[0]-e[0]*f[2], e[0]*f[1]-e[1]*f[0]]
            self.assertLess(cad.dot(cross, normals[a]), 0)
            self.assertAlmostEqual(cad.dot(normals[a], normals[a]), 1., places=6)

    def test_curved_seams_retain_circle_shape(self):
        from OCP.BRepPrimAPI import BRepPrimAPI_MakeCylinder
        source = self.directory / "round.step"
        writer = cad.STEPControl_Writer()
        writer.Transfer(BRepPrimAPI_MakeCylinder(50, 75).Shape(), cad.STEPControl_AsIs)
        writer.Write(str(source))
        cad.import_step(source, self.directory / "round.json")
        d = json.loads((self.directory / "round.json").read_text())
        self.assertEqual(len(d["seams"]), 2)
        for seam in d["seams"]:
            self.assertGreater(len(seam["points"]), 100)
            self.assertAlmostEqual(seam["angleDegrees"], 90., places=5)
            for x, y, z in zip(*[iter(seam["points"])] * 3):
                self.assertAlmostEqual(math.hypot(x, z), .05, places=6)

    def test_non_right_angle_edges_are_not_misclassified_as_ninety(self):
        from OCP.BRepBuilderAPI import BRepBuilderAPI_MakePolygon, BRepBuilderAPI_MakeFace
        from OCP.BRepPrimAPI import BRepPrimAPI_MakePrism
        from OCP.gp import gp_Vec
        polygon = BRepBuilderAPI_MakePolygon(cad.gp_Pnt(0, 0, 0), cad.gp_Pnt(60, 0, 0), cad.gp_Pnt(0, 60, 0), True)
        prism = BRepPrimAPI_MakePrism(BRepBuilderAPI_MakeFace(polygon.Wire()).Face(), gp_Vec(0, 0, 100)).Shape()
        source = self.directory / "wedge.step"
        writer = cad.STEPControl_Writer()
        writer.Transfer(prism, cad.STEPControl_AsIs)
        writer.Write(str(source))
        cad.import_step(source, self.directory / "wedge.json")
        d = json.loads((self.directory / "wedge.json").read_text())
        angles = [round(s["angleDegrees"]) for s in d["seams"]]
        self.assertIn(135, angles)
        self.assertIn(90, angles)
        self.assertEqual(angles.count(135), 2)

    def test_step_inch_units_and_located_shape(self):
        from OCP.BRepPrimAPI import BRepPrimAPI_MakeBox
        from OCP.gp import gp_Trsf, gp_Vec
        source = self.directory / "inch.step"
        transform = gp_Trsf()
        transform.SetTranslation(gp_Vec(30, 40, 50))
        shape = BRepPrimAPI_MakeBox(25.4, 50.8, 76.2).Shape().Moved(cad.TopLoc_Location(transform))
        cad.Interface_Static.SetCVal_s("write.step.unit", "INCH")
        try:
            writer = cad.STEPControl_Writer()
            writer.Transfer(shape, cad.STEPControl_AsIs)
            writer.Write(str(source))
        finally:
            cad.Interface_Static.SetCVal_s("write.step.unit", "MM")
        cad.import_step(source, self.directory / "inch.json")
        d = json.loads((self.directory / "inch.json").read_text())
        self.assertIn("inch", d["sourceUnit"].lower())
        points = list(zip(*[iter(d["vertices"])] * 3))
        extent = [max(p[i] for p in points) - min(p[i] for p in points) for i in range(3)]
        for actual, expected in zip(extent, [.0254, .0762, .0508]):
            self.assertAlmostEqual(actual, expected, places=7)
        self.assertAlmostEqual(min(p[0] for p in points), .03, places=7)
        self.assertAlmostEqual(min(p[1] for p in points), .05, places=7)

    def test_reject_malformed_step_and_wrong_extension(self):
        source = self.directory / "bad.step"
        source.write_text("not a STEP model")
        with self.assertRaises(ValueError):
            cad.import_step(source, self.directory / "bad.json")
        with self.assertRaises(ValueError):
            cad.import_step(self.directory / "fake.stl", self.directory / "bad.json")

    def test_contact_broad_phase_preserves_tolerance_and_located_bounds(self):
        from OCP.BRepPrimAPI import BRepPrimAPI_MakeBox
        from OCP.gp import gp_Trsf, gp_Vec
        transform = gp_Trsf()
        transform.SetTranslation(gp_Vec(100, 200, 300))
        shape = BRepPrimAPI_MakeBox(10, 20, 30).Shape().Moved(cad.TopLoc_Location(transform))
        box = cad.bounds(shape)
        self.assertTrue(cad.bounds_contains_point(box, cad.gp_Pnt(110.019, 210, 310)))
        self.assertFalse(cad.bounds_contains_point(box, cad.gp_Pnt(110.021, 210, 310)))
        self.assertTrue(cad.bounds_overlap(box, (110.019, 200, 300, 120, 220, 330)))
        self.assertFalse(cad.bounds_overlap(box, (110.021, 200, 300, 120, 220, 330)))
        self.assertTrue(cad.bounds_overlap(None, box))

    def test_daemon_recovers_from_failed_import_and_reuses_worker(self):
        source = self.directory / "daemon-invalid.step"
        source.write_text("not STEP geometry")
        inputs = [source, ROOT / "Assets/Samples/WeldingBracket.step", ROOT / "Assets/Samples/WeldingBracket.step"]
        requests = [{"id": i, "input": str(item), "output": str(self.directory / f"daemon-{i}.json")} for i, item in enumerate(inputs)]
        proc = subprocess.run([sys.executable, str(ROOT / "tools/cad_import.py"), "--daemon"], input="".join(json.dumps(r) + "\n" for r in requests), text=True, encoding="utf-8", capture_output=True, timeout=45, check=True)
        self.assertIn("CAD_READY", proc.stdout.splitlines())
        responses = [json.loads(line[len("CAD_RESULT "):]) for line in proc.stdout.splitlines() if line.startswith("CAD_RESULT ")]
        self.assertEqual([r["id"] for r in responses], [0, 1, 2])
        self.assertEqual([r["ok"] for r in responses], [False, True, True])
        self.assertIn("Invalid or unsupported STEP", responses[0]["error"])
        for i in (1, 2):
            self.assertEqual(json.loads((self.directory / f"daemon-{i}.json").read_text()), self.bracket)

    def test_iges_solid_millimetres_and_inch_units(self):
        from OCP.IGESControl import IGESControl_Writer
        from OCP.BRepPrimAPI import BRepPrimAPI_MakeBox
        from OCP.gp import gp_Trsf, gp_Vec
        transform = gp_Trsf()
        transform.SetTranslation(gp_Vec(30, 40, 50))
        shape = BRepPrimAPI_MakeBox(25.4, 50.8, 76.2).Shape().Moved(cad.TopLoc_Location(transform))
        for unit,extension in [("MM", ".iges"), ("INCH", ".igs")]:
            source = self.directory / ("box-" + unit + extension)
            writer = IGESControl_Writer(unit, 1)
            self.assertTrue(writer.AddShape(shape))
            self.assertTrue(writer.Write(str(source)))
            output = self.directory / (unit + "-iges.json")
            cad.import_step(source, output)
            data = json.loads(output.read_text())
            self.assertEqual(data["solidCount"], 1)
            self.assertEqual(data["faceCount"], 6)
            self.assertEqual(len(data["seams"]), 12)
            points = list(zip(*[iter(data["vertices"])] * 3))
            for axis,expected in enumerate([.0254,.0762,.0508]):
                self.assertAlmostEqual(max(p[axis] for p in points)-min(p[axis] for p in points),expected,places=6)
            self.assertAlmostEqual(min(p[0] for p in points), .03, places=6)
            self.assertAlmostEqual(min(p[1] for p in points), .05, places=6)

    def test_iges_curved_surfaces_and_malformed_input(self):
        from OCP.IGESControl import IGESControl_Writer
        from OCP.BRepPrimAPI import BRepPrimAPI_MakeCylinder
        source = self.directory / "cylinder.iges"
        writer = IGESControl_Writer("MM", 1)
        writer.AddShape(BRepPrimAPI_MakeCylinder(50, 75).Shape())
        writer.Write(str(source))
        cad.import_step(source,self.directory / "cylinder-iges.json")
        data=json.loads((self.directory / "cylinder-iges.json").read_text())
        self.assertGreater(len(data["indices"]), 100)
        self.assertTrue(any(len(s["points"])>100 for s in data["seams"]))
        bad=self.directory / "bad.igs"
        bad.write_text("invalid IGES")
        with self.assertRaises(ValueError):cad.import_step(bad,self.directory / "bad-iges.json")

    def test_iges_independent_surfaces_are_sewn_and_oriented_without_losing_faces(self):
        from OCP.IGESControl import IGESControl_Writer
        from OCP.BRepPrimAPI import BRepPrimAPI_MakeBox
        source=self.directory / "independent-patches.igs"
        writer=IGESControl_Writer("MM",0)
        shape=BRepPrimAPI_MakeBox(40,50,60).Shape()
        for face in cad.shapes(shape,cad.TopAbs_FACE):writer.AddShape(face)
        self.assertTrue(writer.Write(str(source)))
        destination=self.directory / "independent-patches.json"
        cad.import_step(source,destination)
        data=json.loads(destination.read_text())
        self.assertEqual(data['faceCount'],6)
        self.assertEqual(data['solidCount'],1)
        self.assertEqual(data['healing']['sourceFaces'],data['healing']['retainedFaces'])
        self.assertEqual(data['healing']['freeEdges'],0)
        self.assertEqual(len(data['seams']),12)
        vertices=list(zip(*[iter(data['vertices'])]*3));normals=list(zip(*[iter(data['normals'])]*3))
        center=[(max(p[i] for p in vertices)+min(p[i] for p in vertices))*.5 for i in range(3)]
        for point,normal in zip(vertices,normals):
            self.assertGreater(cad.dot([point[i]-center[i] for i in range(3)],normal),0)

    def test_iges_open_surface_is_preserved_and_not_filled(self):
        from OCP.IGESControl import IGESControl_Writer
        from OCP.BRepPrimAPI import BRepPrimAPI_MakeBox
        faces=cad.shapes(BRepPrimAPI_MakeBox(40,50,60).Shape(),cad.TopAbs_FACE)
        source=self.directory / 'open-surface.iges';writer=IGESControl_Writer('MM',0)
        for face in faces[:5]:writer.AddShape(face)
        writer.Write(str(source));destination=self.directory/'open-surface.json';cad.import_step(source,destination)
        data=json.loads(destination.read_text());self.assertEqual(data['faceCount'],5);self.assertEqual(data['solidCount'],0)
        self.assertGreater(data['healing']['freeEdges'],0);self.assertTrue(data['warnings'])

    def test_existing_iges_solid_assembly_stays_separate(self):
        from OCP.IGESControl import IGESControl_Writer
        reader=cad.STEPControl_Reader();reader.ReadFile(str(ROOT/'Assets/Samples/WeldingBracket.step'));reader.TransferRoots()
        source=self.directory/'iges-assembly.igs';writer=IGESControl_Writer('MM',1);writer.AddShape(reader.OneShape());writer.Write(str(source))
        destination=self.directory/'iges-assembly.json';cad.import_step(source,destination);data=json.loads(destination.read_text())
        self.assertEqual(data['solidCount'],3);self.assertEqual(data['faceCount'],18);self.assertNotIn('healing',data)
        self.assertGreaterEqual(len([s for s in data['seams'] if s['kind']=='Part contact']),8)


if __name__ == "__main__":
    unittest.main(verbosity=2)
