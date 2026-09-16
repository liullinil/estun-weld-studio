using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace EstunStudio;

public partial class RobotPostprocessorChecks : Node
{
    private int _checks;
    private void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); _checks++; }
    private readonly Transform3D[] _rest = RobotKinematics.StandardRestTransforms();
    private WeldPlanRequest Request => new() { ToolTransform = WeldTorch.ToolTransform };

    public override async void _Ready()
    {
        try
        {
            WeldPlanRequest request = Request;
            Transform3D home = RobotKinematics.Forward(request.StartAngles, _rest, request.ToolTransform);
            WeldProgram line = MakeProgram(request, t => new Transform3D(home.Basis, home.Origin + home.Basis.X * .06f * t), 20);
            RobotPostprocessorResult lineResult = RobotPostprocessor.Export(line, request);
            Require(lineResult.LinearMoves == 1, "A straight constant-orientation weld compresses to one movL");
            Require(lineResult.CircularMoves == 0 && lineResult.JointMoves == 1, "Straight weld retains the original joint transfer");
            Require(lineResult.Primitives.Length < line.Motions.Count / 2, "Controller program has substantially fewer movements");
            Require(lineResult.Text.Contains("setDO(1,1)\nmovL(") || lineResult.Text.Contains("setDO(1,1)\r\nmovL("), "DO1 starts immediately before welding");
            Require(lineResult.Text.StartsWith("-- ENCY HYPER - ESTUN") && lineResult.Text.TrimEnd().EndsWith("setDO(1,0)"), "Program identifies dialect and ends with torch off");
            Require(lineResult.Text.Contains("getJoint()") && lineResult.Text.Contains("stopProject()"), "Start guard prevents an unvalidated move from arbitrary starting joints");
            Require(lineResult.Text.Contains("{jp={") && !lineResult.Text.Contains("{cp={"), "Documented joint-form Cartesian targets avoid inferred Euler convention");
            CheckPlayback(lineResult, request);

            const float radius = .035f, sweep = 1.4f;
            Transform3D Arc(float t) => new(home.Basis, home.Origin + home.Basis.X * radius * Mathf.Sin(sweep * t) + home.Basis.Z * radius * (1 - Mathf.Cos(sweep * t)));
            WeldProgram arc = MakeProgram(request, Arc, 40);
            RobotPostprocessorResult arcResult = RobotPostprocessor.Export(arc, request);
            Require(arcResult.CircularMoves == 1 && arcResult.LinearMoves == 0, "A constant-orientation circular weld compresses to movC");
            Require(arcResult.Text.Contains("movC({jp={") && arcResult.Primitives.Single(p => p.Command == "movC").ViaJoints?.Length == 6, "movC has an actual validated intermediate joint target");
            CheckPlayback(arcResult, request);

            // A changing-orientation arc is not silently assigned undocumented controller orientation behavior.
            WeldProgram rotating = MakeProgram(request, t => new Transform3D(home.Basis * new Basis(Vector3.Up, t * .15f), Arc(t).Origin), 40);
            RobotPostprocessorResult rotatingResult = RobotPostprocessor.Export(rotating, request);
            Require(rotatingResult.CircularMoves == 0, "Unsupported changing-orientation arc remains sampled");
            Require(rotatingResult.JointMoves > 1, "Source joint geometry is retained when Cartesian equivalence cannot be established");

            // An obstacle only 0.14 mm wide is deliberately placed inside the fitting tolerance.
            // Merely fitting a source path would cut through it; independent revalidation must split or retain the original.
            Vector3 blocker = home.Origin + home.Basis.X * .03f;
            var box = new BoxMesh { Size = Vector3.One * .00014f };
            var obstacle = box.GetFaces().Select(p => blocker + p).ToArray();
            var capsule = new RobotCapsule(7, request.ToolTransform.Origin, request.ToolTransform.Origin, .000025f);
            var obstacleRequest = new WeldPlanRequest
            {
                ToolTransform = request.ToolTransform,
                Collision = new CollisionScene(obstacle, new[] { capsule }, floorY: -100, margin: 0)
            };
            WeldProgram detour = MakeProgram(obstacleRequest, t => new Transform3D(home.Basis,
                home.Origin + home.Basis.X * .06f * t + home.Basis.Y * .00045f * Mathf.Sin(Mathf.Pi * t)), 40);
            RobotPostprocessorResult detourResult = RobotPostprocessor.Export(detour, obstacleRequest);
            Require(!detourResult.Primitives.Any(p => p.Command == "movL" && p.SourceStart == 1 && p.SourceEnd == 40),
                "Fitted line crossing a sub-tolerance obstacle is rejected by independent collision validation");
            CheckPlayback(detourResult, obstacleRequest);

            WeldMotion original = line.Motions[1];
            line.Motions[1] = new WeldMotion { TargetAngles = original.TargetAngles, Tcp = original.Tcp, DurationSeconds = original.DurationSeconds, ArcOn = true, SeamId = "test\nsetDO(99,1)" };
            string injection = RobotPostprocessor.Export(line, request).Text;
            Require(!injection.Contains("\nsetDO(99,1)"), "Imported seam names cannot inject executable Lua statements");
            line.Motions[1] = original;

            var changedContext = new WeldPlanRequest { StartAngles = new float[6], ToolTransform = request.ToolTransform };
            bool mismatch = false;
            try { RobotPostprocessor.Export(line, changedContext); } catch (ArgumentException) { mismatch = true; }
            Require(mismatch, "Mismatched planning context is rejected");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool didCancel = false;
            try { RobotPostprocessor.Export(line, request, cancelled.Token); } catch (OperationCanceledException) { didCancel = true; }
            Require(didCancel, "Postprocessing respects cancellation");
            var configured = RobotPostprocessor.Export(line, request, options: new RobotPostprocessorOptions { Tool = 3, CoordinateSystem = 2 });
            Require(configured.Text.Contains("coor=2,tool=3"), "Configured tool and frame are explicitly emitted");
            if (OS.GetCmdlineUserArgs().Contains("--bracket")) await CheckActualBracket();
            GD.Print($"PASS: {_checks} controller postprocessor checks; straight {line.Motions.Count}->{lineResult.Primitives.Length}, arc {arc.Motions.Count}->{arcResult.Primitives.Length}");
            GetTree().Quit(0);
        }
        catch (Exception exception) { GD.PrintErr("FAIL: " + exception); GetTree().Quit(1); }
    }

    private async System.Threading.Tasks.Task CheckActualBracket()
    {
        using var reader = new BinaryReader(File.OpenRead(ProjectSettings.GlobalizePath(RobotModel.MeshPath)));
        reader.ReadBytes(4); int groups = reader.ReadInt32(); var links = new Dictionary<int, List<Vector3>>();
        for (int group = 0; group < groups; group++)
        {
            int link = reader.ReadInt32(); reader.ReadBytes(16); int count = reader.ReadInt32();
            if (!links.TryGetValue(link, out var vertices)) links[link] = vertices = new List<Vector3>();
            for (int i = 0; i < count; i++) { vertices.Add(new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())); reader.ReadBytes(12); }
        }
        var capsules = CollisionScene.CreateRobotCapsules(links.ToDictionary(p => p.Key, p => p.Value.ToArray())).Concat(WeldTorch.CollisionVolumes()).ToArray();
        var plinth = new CylinderMesh { TopRadius = .7f, BottomRadius = .7f, Height = .2f, RadialSegments = 64 }.GetFaces().Select(p => p + new Vector3(0, -.11f, 0)).ToArray();
        var doc = await new CadImportService().ImportAsync(ProjectSettings.GlobalizePath("res://Assets/Samples/WeldingBracket.step"));
        var placement = new Transform3D(Basis.Identity, new Vector3(.85f, .15f, 0) - new Vector3(doc.Bounds.GetCenter().X, doc.Bounds.Position.Y, doc.Bounds.GetCenter().Z));
        var collision = new CollisionScene(doc.Indices.Select(i => placement * doc.Vertices[i]).ToArray(), capsules, staticTriangles: plinth);
        var request = new WeldPlanRequest { Seams = doc.Seams.Where(s => s.Kind == "Part contact").ToArray(), PartTransform = placement, ToolTransform = WeldTorch.ToolTransform, Collision = collision, PartName = doc.Name };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var program = WeldPlanner.Plan(request, cancellation.Token, (value, text) => GD.Print(text));
        double planSeconds = watch.Elapsed.TotalSeconds; watch.Restart();
        var exported = RobotPostprocessor.Export(program, request, cancellation.Token);
        GD.Print($"BRACKET: plan {planSeconds:0.000}s, export {watch.Elapsed.TotalSeconds:0.000}s; {program.ReadyCount} ready, {program.BlockedCount} blocked; {program.Motions.Count} source -> {exported.Primitives.Length} commands, J={exported.JointMoves} L={exported.LinearMoves} C={exported.CircularMoves}; {exported.PlaybackMotions.Length} playback samples");
        Require(program.ReadyCount > 0 && exported.LinearMoves > 0 && exported.Primitives.Length < program.Motions.Count, "Actual STEP bracket compresses real welding runs into validated linear commands");
        string artifact = ProjectSettings.GlobalizePath("res://artifacts/robot-postprocessor-bracket.lua");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!); File.WriteAllText(artifact, exported.Text);
    }

    private WeldProgram MakeProgram(WeldPlanRequest request, Func<float, Transform3D> path, int steps)
    {
        var program = new WeldProgram { StartAngles = request.StartAngles, ToolTransform = request.ToolTransform };
        float[] previous = request.StartAngles;
        program.Motions.Add(new WeldMotion { TargetAngles = previous, Tcp = path(0), DurationSeconds = .04f, SeamId = "test" });
        for (int i = 1; i <= steps; i++)
        {
            Transform3D pose = path(i / (float)steps);
            var solved = RobotKinematics.Solve(pose, previous, _rest, request.ToolTransform, .000003f, .00002f);
            Require(solved.Success, "Synthetic test path has an actual IK solution");
            Require(request.Collision.CheckMotion(previous, solved.AnglesDegrees, out _), "Synthetic source passes sampled collision checks");
            program.Motions.Add(new WeldMotion { TargetAngles = solved.AnglesDegrees, Tcp = pose, DurationSeconds = .08f, ArcOn = true, SeamId = "test" });
            previous = solved.AnglesDegrees;
        }
        return program;
    }

    private void CheckPlayback(RobotPostprocessorResult result, WeldPlanRequest request)
    {
        float[] previous = request.StartAngles;
        foreach (WeldMotion motion in result.PlaybackMotions)
        {
            Require(request.Collision.CheckMotion(previous, motion.TargetAngles, out _), "Every playback interpolation remains collision checked");
            Transform3D fk = RobotKinematics.Forward(motion.TargetAngles, _rest, request.ToolTransform);
            Require(fk.Origin.DistanceTo(motion.Tcp.Origin) < .000001f, "Playback TCP is actual forward kinematics");
            previous = motion.TargetAngles;
        }
        Require(result.PlaybackMotions.Last().TargetAngles.SequenceEqual(result.Primitives.Last().EndJoints), "Playback ends at exactly the exported target");
    }
}
