using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace EstunStudio;

public partial class WeldChecks : Node
{
    private int _checks;
    private void Require(bool pass, string label)
    { if (!pass) throw new InvalidOperationException(label); _checks++; }

    public override async void _Ready()
    {
        try
        {
            CheckGeometry();
            CheckCapsules();
            CheckPlannerWithoutObstacles();
            var source = ReadSource();
            var capsules = CollisionScene.CreateRobotCapsules(source).Concat(TorchCapsules()).ToArray();
            var plinth = new CylinderMesh { TopRadius = .7f, BottomRadius = .7f, Height = .2f, RadialSegments = 64 }.GetFaces().Select(p => p + new Vector3(0, -.11f, 0)).ToArray();
            var scene = new CollisionScene(Array.Empty<Vector3>(), capsules, staticTriangles: plinth, toolTip: WeldTorch.ToolTransform.Origin);
            GD.Print($"Collision model: {capsules.Length} capsules");
            bool homeClear = scene.Check(RobotController.HomeAngles, out string homeReason);
            GD.Print($"Home collision: {!homeClear} {homeReason}");
            if (!homeClear)
            {
                var transforms = CollisionScene.LinkTransforms(RobotController.HomeAngles, RobotKinematics.StandardRestTransforms());
                for (int i = 0; i < capsules.Length; i++) for (int j = i + 1; j < capsules.Length; j++)
                {
                    var a = capsules[i]; var b = capsules[j]; if (Math.Abs(a.Link - b.Link) <= 1) continue;
                    var pa = transforms[Math.Min(a.Link, 6)]; var pb = transforms[Math.Min(b.Link, 6)];
                    float distance = Mathf.Sqrt(CollisionScene.DistanceSegmentSegmentSquared(pa * a.A, pa * a.B, pb * b.A, pb * b.B));
                    if (distance < a.Radius + b.Radius + .002f) GD.Print($"Overlap {i}:{a.Link} r{a.Radius:0.000} {pa * a.A}..{pa * a.B}, {j}:{b.Link} r{b.Radius:0.000} {pb * b.A}..{pb * b.B} depth {a.Radius + b.Radius - distance:0.000}");
                }
            }
            Require(homeClear, "Source-derived robot capsules must allow native home pose");
            var document = await new CadImportService().ImportAsync(ProjectSettings.GlobalizePath("res://Assets/Samples/WeldingBracket.step"));
            Require(document.TriangleCount > 10 && document.Seams.Count > 0, "Actual STEP imports mesh and BRep seam candidates");
            var placement = new Transform3D(Basis.Identity, new Vector3(.85f, .15f, 0) - new Vector3(document.Bounds.GetCenter().X, document.Bounds.Position.Y, document.Bounds.GetCenter().Z));
            var triangles = document.Indices.Select(i => placement * document.Vertices[i]).ToArray();
            scene = new CollisionScene(triangles, capsules, staticTriangles: plinth, toolTip: WeldTorch.ToolTransform.Origin);
            Require(scene.Check(RobotController.HomeAngles, out homeReason), "Default sample placement leaves home clear: " + homeReason);
            var selected = document.Seams.Where(s => s.Kind == "Part contact").ToArray();
            Require(selected.Length > 0, "Sample has contact seams");
            var request = new WeldPlanRequest { Seams = selected, PartTransform = placement, ToolTransform = Tool, Collision = scene, PartName = document.Name };
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(180));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var program = WeldPlanner.Plan(request, cancellation.Token, (value, message) => GD.Print(message));
            GD.Print($"Real sample plan: {program.Summary}; {watch.Elapsed.TotalSeconds:0.0}s");
            foreach (var seam in program.Seams) GD.Print($"{seam.Id} ({seam.Length:0.000}m): {seam.State}: {seam.Reason}");
            Require(program.ReadyCount > 0, "Actual sample contains at least one reachable collision-free weld");
            float[] previous = request.StartAngles;
            foreach (var move in program.Motions)
            {
                Require(scene.CheckMotion(previous, move.TargetAngles, out string reason), "Every emitted segment independently passes collision validation: " + reason);
                Require(RobotKinematics.Forward(move.TargetAngles, request.JointRest, request.ToolTransform).Origin.DistanceTo(move.Tcp.Origin) < .0003f, "Emitted robot FK matches requested TCP");
                previous = move.TargetAngles;
            }
            GD.Print($"PASS: {_checks} welding geometry / actual STEP / collision / trajectory checks");
            GetTree().Quit(0);
        }
        catch (Exception ex) { GD.PrintErr("FAIL: " + ex); GetTree().Quit(1); }
    }

    private static Transform3D Tool => WeldTorch.ToolTransform;
    private static IEnumerable<RobotCapsule> TorchCapsules() => WeldTorch.CollisionVolumes();

    private void CheckGeometry()
    {
        Vector3 a = new(-1, 0, -1), b = new(1, 0, -1), c = new(0, 0, 1);
        Require(TriangleBvh.DistanceSegmentTriangleSquared(new Vector3(0, -1, 0), new Vector3(0, 1, 0), a, b, c) < 1e-9f, "Segment crossing triangle is detected");
        Require(Math.Abs(TriangleBvh.DistanceSegmentTriangleSquared(new Vector3(0, .2f, 0), new Vector3(0, .3f, 0), a, b, c) - .04f) < .000001f, "Parallel gap has correct squared distance");
        Require(Math.Abs(CollisionScene.DistanceSegmentSegmentSquared(Vector3.Zero, Vector3.Right, Vector3.Up, Vector3.Up + Vector3.Right) - 1) < .00001f, "Parallel capsule axes distance");
        var bvh = new TriangleBvh(new[] { a, b, c });
        Require(bvh.IntersectsCapsule(new Vector3(0, .005f, 0), new Vector3(0, .1f, 0), .01f), "Capsule touching face detected");
        Require(!bvh.IntersectsCapsule(new Vector3(0, .1f, 0), new Vector3(0, .2f, 0), .01f), "Separated capsule remains clear");
        var mesh = new BoxMesh { Size = Vector3.One };
        var box = new TriangleBvh(mesh.GetFaces());
        Require(box.ContainsPoint(Vector3.Zero), "Closed solid detects fully enclosed capsule centre");
        Require(!box.ContainsPoint(Vector3.One), "Closed solid excludes outside point");
        Require(box.IntersectsCapsule(Vector3.Zero, Vector3.Up * .1f, .01f), "Capsule completely inside a solid is collision");
        Require(!box.IntersectsCapsule(Vector3.One * 2, Vector3.One * 3, .02f), "Capsule clear of closed solid");
        var overlap = new TriangleBvh(mesh.GetFaces().Concat(mesh.GetFaces().Select(v => v + Vector3.Right * .2f)).ToArray());
        Require(overlap.ContainsPoint(Vector3.Zero), "Overlapping STEP solids are treated as solid union, not parity void");
        var obb = new OrientedBounds(new Aabb(Vector3.One * -.5f, Vector3.One), new Transform3D(new Basis(Vector3.Up, .3f), Vector3.Zero));
        Require(obb.IntersectsCapsule(Vector3.Zero, Vector3.Up * .2f, .01f), "Torch capsule completely inside robot box rejected");
        Require(!obb.IntersectsCapsule(Vector3.One * 2, Vector3.One * 3, .01f), "Torch capsule outside oriented box clear");
        Require(obb.IntersectsTriangle(Vector3.Zero, Vector3.Up, Vector3.Right, 0), "Triangle crosses oriented box");
        Require(!obb.IntersectsTriangle(Vector3.One * 2, Vector3.One * 2 + Vector3.Up, Vector3.One * 2 + Vector3.Right, 0), "Triangle far from oriented box clear");
        Basis orientation = WeldPlanner.TorchBasis(Vector3.Right, new Vector3(0, 1, 1), 90);
        Require(orientation.Y.DistanceTo(new Vector3(0, 1, 1).Normalized()) < .0001f, "Torch wrist roll preserves work angle");
        Require(Math.Abs(orientation.Determinant() - 1) < .0001f, "Torch target orientation is proper right-handed basis");
        var sampled = WeldPlanner.Resample(new[] { Vector3.Zero, Vector3.Right * .027f }, .008f);
        Require(sampled.Length == 5 && sampled[^1].DistanceTo(Vector3.Right * .027f) < .000001f, "Seam samples include endpoints at bounded spacing");
    }

    private void CheckCapsules()
    {
        var rest = RobotKinematics.StandardRestTransforms();
        var collision = new CollisionScene(Array.Empty<Vector3>(), new[] { new RobotCapsule(1, Vector3.Zero, Vector3.Up * .1f, .05f), new RobotCapsule(3, Vector3.Zero, Vector3.Up * .1f, .05f) }, Enumerable.Repeat(Transform3D.Identity, 6).ToArray());
        Require(!collision.Check(new float[6], out string reason) && reason.Contains("Self"), "Non-adjacent links self collision is rejected");
        collision = new CollisionScene(Array.Empty<Vector3>(), new[] { new RobotCapsule(1, Vector3.Zero, Vector3.Up * .1f, .05f), new RobotCapsule(2, Vector3.Zero, Vector3.Up * .1f, .05f) }, Enumerable.Repeat(Transform3D.Identity, 6).ToArray());
        Require(collision.Check(new float[6], out _), "Adjacent bearing geometry is exempt from self collision");
        collision = new CollisionScene(Array.Empty<Vector3>(), new[] { new RobotCapsule(3, new Vector3(0, -.4f, 0), new Vector3(0, -.3f, 0), .02f) }, Enumerable.Repeat(Transform3D.Identity, 6).ToArray());
        Require(!collision.Check(new float[6], out reason) && reason.Contains("floor"), "Robot through floor is rejected");
        var invalid = new float[6]; invalid[2] = 161;
        Require(!new CollisionScene(Array.Empty<Vector3>(), Array.Empty<RobotCapsule>()).Check(invalid, out _), "Planner enforces joint limits independently");
    }

    private void CheckPlannerWithoutObstacles()
    {
        Transform3D tcp = RobotKinematics.Forward(RobotController.HomeAngles, RobotKinematics.StandardRestTransforms(), Tool);
        var seam = new CadSeam { Id = "Known reachable", Points = new[] { tcp.Origin - tcp.Basis.Y * .003f, tcp.Origin - tcp.Basis.Y * .003f + tcp.Basis.X * .024f }, NormalA = (tcp.Basis.Y + tcp.Basis.Z).Normalized(), NormalB = (tcp.Basis.Y - tcp.Basis.Z).Normalized() };
        var request = new WeldPlanRequest { Seams = new[] { seam }, ToolTransform = Tool };
        WeldProgram result = WeldPlanner.Plan(request);
        Require(result.ReadyCount == 1 && result.Motions.Any(m => m.ArcOn), "Known reachable oriented seam emits welding trajectory");
        Require(result.Motions[0].ArcOn == false && result.Motions[^1].ArcOn == false, "Transfers and final retraction keep arc off");
        Require(result.ToJson().Contains("Known reachable") && result.ToCsv().Contains("WELD"), "Neutral program exports real trajectory");
        seam.Points = new[] { Vector3.One * 8, Vector3.One * 8 + Vector3.Right * .1f };
        result = WeldPlanner.Plan(request);
        Require(result.ReadyCount == 0 && result.BlockedCount == 1 && result.Motions.Count == 0, "Unreachable seam never emits executable movement");
        seam.Deleted = true; result = WeldPlanner.Plan(request);
        Require(result.Seams[0].State == WeldSeamState.Deleted && result.Motions.Count == 0, "Operator-deleted seam cannot enter program");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        bool threw = false; try { WeldPlanner.Plan(request, cancellation.Token); } catch (OperationCanceledException) { threw = true; }
        Require(threw, "Planning cancellation is honored");
    }

    private static Dictionary<int, Vector3[]> ReadSource()
    {
        using var reader = new BinaryReader(File.OpenRead(ProjectSettings.GlobalizePath(RobotModel.MeshPath)));
        reader.ReadBytes(4); int groups = reader.ReadInt32(); var list = new Dictionary<int, List<Vector3>>();
        for (int group = 0; group < groups; group++)
        {
            int link = reader.ReadInt32(); reader.ReadBytes(16); int count = reader.ReadInt32();
            if (!list.TryGetValue(link, out var vertices)) list[link] = vertices = new List<Vector3>();
            for (int i = 0; i < count; i++) { vertices.Add(new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())); reader.ReadBytes(12); }
        }
        return list.ToDictionary(p => p.Key, p => p.Value.ToArray());
    }
}
