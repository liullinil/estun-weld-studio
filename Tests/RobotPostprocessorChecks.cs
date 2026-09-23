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
            var linearized = RobotPostprocessor.Export(arc, request, options: new RobotPostprocessorOptions { LinearizeArcs = true, TorchDigitalOutput = 12 });
            Require(linearized.CircularMoves == 0 && !linearized.Text.Contains("movC("), "Arcs-as-lines export never emits movC");
            Require(linearized.LinearMoves > 1 && linearized.Text.Contains("setDO(12,1)") && linearized.Text.TrimEnd().EndsWith("setDO(12,0)"), "Linearized arc uses configured torch output for on and final off");
            Require(!linearized.Text.Contains("setDO(1,"), "Configured torch output replaces every DO1 write");
            CheckPlayback(linearized, request);
            CheckWeaving(line, request, home);
            CheckWarningPlayback(line, request, home);

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
            bool invalidIo = false;
            try { RobotPostprocessor.Export(line, request, options: new RobotPostprocessorOptions { TorchDigitalOutput = -1 }); } catch (ArgumentException) { invalidIo = true; }
            Require(invalidIo, "Negative torch output is rejected");
            if (OS.GetCmdlineUserArgs().Contains("--bracket")) await CheckActualBracket();
            GD.Print($"PASS: {_checks} controller postprocessor checks; straight {line.Motions.Count}->{lineResult.Primitives.Length}, arc {arc.Motions.Count}->{arcResult.Primitives.Length}");
            GetTree().Quit(0);
        }
        catch (Exception exception) { GD.PrintErr("FAIL: " + exception); GetTree().Quit(1); }
    }

    private void CheckWeaving(WeldProgram line, WeldPlanRequest request, Transform3D home)
    {
        var options = new WeavingOptions { Enabled = true, AmplitudeMm = 2, FrequencyHz = 2, FadeMm = 2 };
        Require(ReferenceEquals(line, WeavePlanner.Apply(line, request, new WeavingOptions())), "Disabled weaving preserves source program exactly");
        WeldProgram woven = WeavePlanner.Apply(line, request, options);
        Require(woven.Motions.Count > line.Motions.Count && woven.Motions[0] == line.Motions[0], "Weaving densely samples welds and retains the original transfer");
        Require(woven.Motions[^1].TargetAngles.SequenceEqual(line.Motions[^1].TargetAngles), "Weave fades to the exact original end joints for a validated retraction");
        Require(Math.Abs(woven.DurationSeconds - line.DurationSeconds) < .0001f, "Nominal weave frequency retains source timing");
        float maxOffset = woven.Motions.Max(m => Math.Abs((m.Tcp.Origin - home.Origin).Dot(home.Basis.Z)));
        Require(maxOffset > .0018f && maxOffset < .00203f, "Displayed/simulated weave reaches its configured one-sided amplitude");
        var exported = RobotPostprocessor.Export(woven, request, options: new RobotPostprocessorOptions { Weaving = options, PositionToleranceMetres = .000025f });
        Require(exported.Text.Contains("Explicit sinusoidal weave") && !exported.Text.Contains("movLW(") && !exported.Text.Contains("movCW("), "Weave uses explicit validated motion without inventing undocumented controller waveform semantics");
        Require(exported.LinearMoves > 0 && exported.Primitives.Count(p => p.ArcOn) > 50, "Export retains woven samples rather than compressing back to the seam centreline");
        CheckPlayback(exported, request);
        foreach (RobotProgramPrimitive primitive in exported.Primitives.Where(p => p.ArcOn))
            Require(primitive.EndJoints.SequenceEqual(woven.Motions[primitive.SourceEnd].TargetAngles), "Exported weave endpoint matches the actual simulated joint target");

        var rotated = WeavePlanner.Apply(line, request, new WeavingOptions { Enabled = true, AmplitudeMm = 2, FrequencyHz = 2, AngleDegrees = 90 });
        Require(rotated.Motions.Max(m => Math.Abs((m.Tcp.Origin - home.Origin).Dot(home.Basis.Y))) > .0018f, "Weave plane angle rotates the actual TCP displacement");
        Vector3 blocker = woven.Motions.OrderByDescending(m => Math.Abs((m.Tcp.Origin - home.Origin).Dot(home.Basis.Z))).First().Tcp.Origin;
        Vector3[] obstacle = new BoxMesh { Size = Vector3.One * .0003f }.GetFaces().Select(p => p + blocker).ToArray();
        var blockedRequest = new WeldPlanRequest { ToolTransform = request.ToolTransform,
            Collision = new CollisionScene(obstacle, new[] { new RobotCapsule(7, request.ToolTransform.Origin, request.ToolTransform.Origin, .000025f) }, floorY: -100, margin: 0) };
        bool blocked = false;
        try { WeavePlanner.Apply(line, blockedRequest, options); } catch (InvalidOperationException error) { blocked = error.Message.Contains("collision"); }
        Require(blocked, "Obstacle inside weave envelope rejects generation rather than exporting an unchecked weave");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool didCancel = false;
        try { WeavePlanner.Apply(line, request, options, cancelled.Token); } catch (OperationCanceledException) { didCancel = true; }
        Require(didCancel, "Weave calculation supports cancellation");
    }

    private void CheckWarningPlayback(WeldProgram line, WeldPlanRequest request, Transform3D home)
    {
        var warned = new WeldProgram { StartAngles = line.StartAngles, ToolTransform = line.ToolTransform };
        warned.Motions.AddRange(line.Motions);
        warned.Seams.Add(new PlannedSeam { Id = "test", State = WeldSeamState.Warning, HasCollision = true,
            Partial = true, Length = .12f, ProcessedLength = .06f, Reason = "Torch / workpiece collision; partial seam",
            WarningReasons = new[] { "Torch / workpiece collision", "partial seam" } });
        var exported = RobotPostprocessor.Export(warned, request);
        Require(exported.JointMoves == line.Motions.Count && exported.LinearMoves == 0 && exported.CircularMoves == 0,
            "Warning path keeps source joint geometry instead of fitting a different Cartesian trajectory");
        Require(exported.PlaybackMotions.SequenceEqual(warned.Motions), "Warning playback retains every original source motion");
        Require(exported.Text.Contains("-- WARNING: Torch / workpiece collision") && exported.Text.Contains("Partial seam: 50%") && exported.Text.Contains("not collision-free"),
            "Lua identifies collision components and partial coverage without claiming collision-free validation");

        Vector3 centre = home.Origin + home.Basis.X * .03f;
        Vector3[] obstacle = new BoxMesh { Size = Vector3.One * .003f }.GetFaces().Select(p => p + centre).ToArray();
        var colliding = new WeldPlanRequest { ToolTransform = request.ToolTransform, AllowWarningPaths = true,
            Collision = new CollisionScene(obstacle, new[] { new RobotCapsule(7, request.ToolTransform.Origin, request.ToolTransform.Origin, .000025f) }, floorY: -100, margin: 0) };
        var frames = WebBridge.BuildCollisionTimeline(warned, exported.PlaybackMotions, colliding);
        Require(frames.Length == exported.PlaybackMotions.Length && frames.All(f => f[0].T == 0), "Collision frames cover every emitted move from its initial pose");
        Require(frames.SelectMany(f => f).Any(f => f.Links.Contains(7) && f.Reasons.Any(r => r.Contains("workpiece"))), "Precomputed playback identifies the torch and workpiece contact");
        Require(frames[^1][^1].Links.Length == 0, "Playback collision schedule clears the robot after leaving the obstacle");
        Require(frames.SelectMany(f => f).Count() < 2 * exported.PlaybackMotions.Length, "Collision schedule compresses unchanged contact states");
        float[] from = line.StartAngles;
        for (int index = 0; index < exported.PlaybackMotions.Length; index++)
        {
            foreach (var frame in frames[index])
            {
                float[] q = Enumerable.Range(0, 6).Select(axis => Mathf.Lerp(from[axis], exported.PlaybackMotions[index].TargetAngles[axis], frame.T)).ToArray();
                colliding.Collision.CheckDetailed(q, out _, out CollisionContact[] contacts);
                Require(frame.Links.SequenceEqual(contacts.SelectMany(c => c.Links).Distinct().OrderBy(x => x)), "Playback contact changes match the detailed native check at their exact pose");
            }
            from = exported.PlaybackMotions[index].TargetAngles;
        }
        var weaving = new WeavingOptions { Enabled = true, AmplitudeMm = 2, FrequencyHz = 2, FadeMm = 2 };
        WeldProgram woven = WeavePlanner.Apply(warned, colliding, weaving);
        Require(woven.Motions.Count > warned.Motions.Count && woven.Seams[0].State == WeldSeamState.Warning && woven.Seams[0].HasCollision,
            "Web warning mode retains woven collision paths for simulation");
        Require(warned.Seams[0].WarningReasons.Length == 2, "Weaving warning annotations do not mutate the input program");

        var fast = new WeavingOptions { Enabled = true, AmplitudeMm = 10, FrequencyHz = 5, FadeMm = 2 };
        var fastSource = new WeldProgram { StartAngles = warned.StartAngles, ToolTransform = warned.ToolTransform };
        fastSource.Seams.AddRange(warned.Seams);
        fastSource.Motions.AddRange(warned.Motions.Select(m => new WeldMotion { TargetAngles = m.TargetAngles, Tcp = m.Tcp,
            WeldPoint = m.WeldPoint, WorldApproach = m.WorldApproach, ArcOn = m.ArcOn, SeamId = m.SeamId, DurationSeconds = .0002f }));
        WeldProgram partial = WeavePlanner.Apply(fastSource, colliding, fast);
        Require(partial.Seams[0].Partial && partial.Seams[0].ProcessedLength < warned.Seams[0].ProcessedLength,
            "Infeasible weave retains only its feasible prefix and reports reduced coverage");
        Require(!partial.Motions[^1].ArcOn && partial.Motions[^1].TargetAngles.SequenceEqual(warned.Motions[^1].TargetAngles),
            "Truncated weaving reaches the original run end with the torch off before continuing the job");
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
        var collision = new CollisionScene(doc.Indices.Select(i => placement * doc.Vertices[i]).ToArray(), capsules, staticTriangles: plinth, toolTip: WeldTorch.ToolTransform.Origin);
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
