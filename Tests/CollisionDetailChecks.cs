using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace EstunStudio;

public partial class CollisionDetailChecks : Node
{
    private int _checks;
    private void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); _checks++; }

    public override void _Ready()
    {
        try
        {
            float[] home = RobotController.HomeAngles;
            Transform3D[] rest = RobotKinematics.StandardRestTransforms();
            Transform3D[] transforms = CollisionScene.LinkTransforms(home, rest);
            Vector3 centre = new(.5f, .5f, .5f);
            RobotCapsule Capsule(int link)
            {
                Vector3 local = transforms[Math.Min(link, 6)].AffineInverse() * centre;
                return new RobotCapsule(link, local, local, .02f);
            }
            var adjacent = new CollisionScene(Array.Empty<Vector3>(), new[] { Capsule(3), Capsule(4) }, floorY: -100);
            Require(adjacent.CheckDetailed(home, out _, out var empty) && empty.Length == 0, "Overlapping adjacent links remain excluded");
            var torchFlange = new CollisionScene(Array.Empty<Vector3>(), new[] { Capsule(6), Capsule(7) }, floorY: -100);
            Require(torchFlange.CheckDetailed(home, out _, out empty) && empty.Length == 0, "Torch and adjacent flange remain excluded");
            var separatedLinks = new CollisionScene(Array.Empty<Vector3>(), new[] { Capsule(2), Capsule(4) }, floorY: -100);
            Require(!separatedLinks.CheckDetailed(home, out var reason, out var self) && self.Length == 1, "Nonadjacent overlapping links collide");
            Require(self[0].Kind == "self" && self[0].Links.SequenceEqual(new[] { 2, 4 }) && reason == self[0].Reason, "Self collision provides both exact link IDs and original reason");

            Vector3[] obstacle = new BoxMesh { Size = Vector3.One * .015f }.GetFaces().Select(p => p + centre).ToArray();
            var scene = new CollisionScene(obstacle, new[] { Capsule(2), Capsule(4), Capsule(7) }, floorY: .5f, staticTriangles: obstacle);
            Require(!scene.CheckDetailed(home, out reason, out var hits), "Detailed query finds actual obstacles");
            foreach (string kind in new[] { "floor", "workpiece", "fixture", "self" }) Require(hits.Any(h => h.Kind == kind), "Detailed query includes " + kind);
            Require(hits.SelectMany(h => h.Links).Distinct().OrderBy(i => i).SequenceEqual(new[] { 2, 4, 7 }), "Implicated links include torch without unrelated links");
            Require(!scene.Check(home, out string originalReason) && reason == originalReason, "Planner early-exit result and detailed first reason agree");
            float[] invalid = (float[])home.Clone(); invalid[4] = RobotController.JointMaximum[4] + 1;
            Require(!scene.CheckDetailed(invalid, out _, out var limit) && limit[0].Kind == "joint-limit" && limit[0].Links.SequenceEqual(new[] { 5 }), "Out-of-range axis reports exact link");
            var random = new Random(54723);
            var poses = Enumerable.Range(0, 200).Select(_ => Enumerable.Range(0, 6).Select(i => Mathf.Lerp(RobotController.JointMinimum[i], RobotController.JointMaximum[i], (float)random.NextDouble())).ToArray()).ToArray();
            foreach (float[] pose in poses)
            {
                bool clear = scene.Check(pose, out string a);
                bool detailed = scene.CheckDetailed(pose, out string b, out hits);
                Require(clear == detailed && a == b && (hits.Length == 0) == clear, "Detailed and planner collision predicates agree across random poses");
            }
            int failures = 0;
            Parallel.ForEach(poses, pose =>
            {
                bool a = scene.Check(pose, out string reasonA), b = scene.CheckDetailed(pose, out string reasonB, out _);
                if (a != b || reasonA != reasonB) System.Threading.Interlocked.Increment(ref failures);
            });
            Require(failures == 0, "Cached immutable scenes support concurrent pose checks");
            CheckSourceBaseShoulder();
            GD.Print($"PASS: {_checks} detailed collision checks");
            GetTree().Quit(0);
        }
        catch (Exception exception) { GD.PrintErr(exception); GetTree().Quit(1); }
    }

    private void CheckSourceBaseShoulder()
    {
        var sourceLists = new Dictionary<int, List<Vector3>>();
        using var reader = new BinaryReader(File.OpenRead(ProjectSettings.GlobalizePath(RobotModel.MeshPath)));
        reader.ReadBytes(4); int count = reader.ReadInt32();
        for (int group = 0; group < count; group++)
        {
            int link = reader.ReadInt32(); reader.ReadBytes(16); int vertices = reader.ReadInt32();
            if (link != 0 && link != 2) { reader.BaseStream.Seek(vertices * 24L, SeekOrigin.Current); continue; }
            if (!sourceLists.TryGetValue(link, out var list)) sourceLists[link] = list = new();
            for (int i = 0; i < vertices; i++) { list.Add(new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())); reader.ReadBytes(12); }
        }
        var source = sourceLists.ToDictionary(p => p.Key, p => p.Value.ToArray());
        var capsules = CollisionScene.CreateRobotCapsules(source);
        var boundsScene = new CollisionScene(Array.Empty<Vector3>(), capsules, floorY: -100);
        var precise = new CollisionScene(Array.Empty<Vector3>(), capsules, floorY: -100, robotTriangles: source);
        float[] reported = { 26, 120, 0, 0, 90, 0 };
        Require(!boundsScene.Check(reported, out string oldReason) && oldReason.Contains("Base / A2"), "Reported source-CAD pose reproduces the conservative Base/A2 box overlap");
        bool reportedClear = precise.CheckDetailed(reported, out string reason, out var hits);
        GD.Print($"Source Base/A2 reported pose: clear={reportedClear}, {reason}");
        Require(reportedClear && hits.Length == 0, "Actual CAD surfaces at A1=26 A2=120 remain separated");
        int actualContacts = 0, rejectedBoundsContacts = 0;
        var watch = Stopwatch.StartNew();
        for (int a1 = -180; a1 <= 180; a1 += 15)
            for (int a2 = -360; a2 <= 360; a2 += 10)
            {
                float[] pose = { a1, a2, 0, 0, 90, 0 };
                bool coarseClear = boundsScene.Check(pose, out _), clear = precise.Check(pose, out string simpleReason);
                Require(clear == precise.CheckDetailed(pose, out string detailedReason, out var contacts) && simpleReason == detailedReason,
                    "Source mesh narrow phase agrees in planner and detailed modes");
                if (!clear && contacts.Any(c => c.Kind == "self" && c.Links.SequenceEqual(new[] { 0, 2 })))
                {
                    actualContacts++;
                    if (actualContacts == 1) GD.Print($"Source Base/A2 retained collision A1={a1}, A2={a2}: {simpleReason}");
                }
                if (!coarseClear && clear) rejectedBoundsContacts++;
            }
        GD.Print($"Source Base/A2 sweep: {actualContacts} retained contacts; {rejectedBoundsContacts} box false positives rejected; {watch.Elapsed.TotalMilliseconds:0} ms");
        Require(rejectedBoundsContacts > 0, "Source mesh narrow phase rejects conservative slab false positives");
        // The stock base/A2 assembly remains separated throughout this sweep. Put the
        // original housings in genuinely overlapping frames to prove the pair is checked,
        // rather than solving the original false positive by globally ignoring Base/A2.
        var overlappingRest = Enumerable.Repeat(Transform3D.Identity, 6).ToArray();
        var overlapping = new CollisionScene(Array.Empty<Vector3>(), capsules, rest: overlappingRest, floorY: -100, robotTriangles: source);
        Require(!overlapping.CheckDetailed(new float[6], out reason, out hits) && hits.Any(c => c.Kind == "self" && c.Links.SequenceEqual(new[] { 0, 2 })),
            "Genuinely intersecting original Base/A2 source housings still collide");
        Require(!overlapping.Check(new float[6], out string overlapReason) && overlapReason == reason, "Planner detects genuine Base/A2 CAD intersection");
        GD.Print($"Source Base/A2 deliberately intersecting assembly: {reason}");

        var cube = new TriangleBvh(new BoxMesh { Size = Vector3.One }.GetFaces(), collectComponentSamples: true);
        var small = new TriangleBvh(new BoxMesh { Size = Vector3.One * .1f }.GetFaces(), collectComponentSamples: true);
        Require(cube.IntersectsMesh(small, Transform3D.Identity, 0), "Precise mesh collision detects closed-solid containment");
        Require(cube.IntersectsMesh(cube, new Transform3D(new Basis(Vector3.Up, .2f), Vector3.Right * .9f), .002f), "Precise mesh collision detects crossing source surfaces");
        Require(!cube.IntersectsMesh(small, new Transform3D(Basis.Identity, Vector3.Right * .7f), .002f), "Precise mesh collision rejects separated closed meshes");
        Require(cube.IntersectsMesh(small, new Transform3D(Basis.Identity, Vector3.Right * .551f), .002f), "Precise mesh collision preserves the 2 mm clearance margin");
    }
}
