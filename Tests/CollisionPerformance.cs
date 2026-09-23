using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace EstunStudio;

/// <summary>Headless CPU comparison isolating optional per-pose TCP containment; this does not measure rendering FPS.</summary>
public partial class CollisionPerformance : Node
{
    private const int Rounds = 21;
    private const int PosesPerRound = 48;
    private static readonly Transform3D[] Rest = RobotKinematics.StandardRestTransforms();
    private sealed record Measurements(double MedianMilliseconds, double P95Milliseconds,
        double MeanMilliseconds, double BatchMeanMedianMilliseconds, double BatchMeanP95Milliseconds,
        double AllocatedBytesPerCheck, int Checks);

    public override async void _Ready()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            var elapsed = Stopwatch.StartNew();
            var source = ReadRobotTriangles();
            var capsules = CollisionScene.CreateRobotCapsules(source).Concat(WeldTorch.CollisionVolumes()).ToArray();
            var plinth = new CylinderMesh { TopRadius = .7f, BottomRadius = .7f, Height = .2f, RadialSegments = 64 }
                .GetFaces().Select(p => p + new Vector3(0, -.11f, 0)).ToArray();
            string samples = @"C:\Users\Public\Documents\ENCY\Version 3\Models\Milling_3D";
            string[] inputs = (System.Environment.GetEnvironmentVariable("COLLISION_BENCHMARK_INPUTS") ??
                string.Join('|', ProjectSettings.GlobalizePath("res://Assets/Samples/WeldingBracket.step"),
                    Path.Combine(samples, "49-1.igs"), Path.Combine(samples, "Part.IGS"))).Split('|');
            var results = new List<object>();
            foreach (string path in inputs.Where(File.Exists))
            {
                var document = await new CadImportService().ImportAsync(path, ct: timeout.Token);
                Vector3 normalOffset = new Vector3(.85f, .15f, 0) - new Vector3(document.Bounds.GetCenter().X,
                    document.Bounds.Position.Y, document.Bounds.GetCenter().Z);
                Vector3 homeTcp = RobotKinematics.Forward(RobotController.HomeAngles, Rest, WeldTorch.ToolTransform).Origin;
                foreach (bool tcpInBounds in new[] { false, true })
                {
                    Vector3 offset = tcpInBounds ? homeTcp - document.Bounds.GetCenter() : normalOffset;
                    Vector3[] triangles = document.Indices.Select(i => document.Vertices[i] + offset).ToArray();
                    var tipOff = new CollisionScene(triangles, capsules, Rest, staticTriangles: plinth, robotTriangles: source);
                    var tipOn = new CollisionScene(triangles, capsules, Rest, staticTriangles: plinth,
                        robotTriangles: source, toolTip: WeldTorch.ToolTransform.Origin);
                    foreach (bool moving in new[] { false, true })
                    {
                        float[][] poses = MakePoses(moving, tcpInBounds);
                        // Warm the same code and both BVHs with the same pose sequence before timing.
                        for (int i = 0; i < PosesPerRound * 4; i++)
                        {
                            tipOff.CheckDetailed(poses[i % poses.Length], out _, out _);
                            tipOn.CheckDetailed(poses[i % poses.Length], out _, out _);
                        }
                        var times = new[] { new List<double>(Rounds * PosesPerRound), new List<double>(Rounds * PosesPerRound) };
                        var batches = new[] { new List<double>(Rounds), new List<double>(Rounds) };
                        long[] allocations = new long[2];
                        var scenes = new[] { tipOff, tipOn };
                        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                        for (int round = 0; round < Rounds; round++)
                        {
                            timeout.Token.ThrowIfCancellationRequested();
                            for (int pass = 0; pass < 2; pass++)
                            {
                                int variant = (round + pass) % 2;
                                long allocated = GC.GetAllocatedBytesForCurrentThread();
                                long batchStart = Stopwatch.GetTimestamp();
                                for (int i = 0; i < poses.Length; i++)
                                {
                                    long start = Stopwatch.GetTimestamp();
                                    scenes[variant].CheckDetailed(poses[i], out _, out _);
                                    times[variant].Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                                }
                                batches[variant].Add(Stopwatch.GetElapsedTime(batchStart).TotalMilliseconds / poses.Length);
                                allocations[variant] += GC.GetAllocatedBytesForCurrentThread() - allocated;
                            }
                        }
                        int differentResults = 0, tipWithinPartBounds = 0;
                        foreach (float[] pose in poses)
                        {
                            tipOff.CheckDetailed(pose, out _, out CollisionContact[] off);
                            tipOn.CheckDetailed(pose, out _, out CollisionContact[] on);
                            if (!off.SequenceEqual(on)) differentResults++;
                            Vector3 tip = RobotKinematics.Forward(pose, Rest, WeldTorch.ToolTransform).Origin;
                            if (tipOn.PartBounds.HasPoint(tip)) tipWithinPartBounds++;
                        }
                        Measurements before = Summarize(times[0], batches[0], allocations[0]);
                        Measurements after = Summarize(times[1], batches[1], allocations[1]);
                        object result = new
                        {
                            model = Path.GetFileName(path), document.TriangleCount, capsules = capsules.Length,
                            placement = tcpInBounds ? "part AABB centered on home TCP (containment stress)" : "normal workpiece placement",
                            poses = moving ? (tcpInBounds ? "deterministic +/- 0.15 degree home neighborhood" : "deterministic +/- 80 degree home neighborhood") : "static home",
                            poseCount = poses.Length, tipWithinPartBounds, differentResults, tipOff = before, tipOn = after,
                            medianBatchDeltaMilliseconds = after.BatchMeanMedianMilliseconds - before.BatchMeanMedianMilliseconds,
                            medianBatchRatio = after.BatchMeanMedianMilliseconds / before.BatchMeanMedianMilliseconds
                        };
                        results.Add(result);
                        GD.Print(JsonSerializer.Serialize(result));
                    }
                }
            }
            string output = System.Environment.GetEnvironmentVariable("COLLISION_BENCHMARK_OUTPUT") ??
                ProjectSettings.GlobalizePath("res://artifacts/fps-collision-comparison.json");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(new
            {
                method = "Same optimized build, alternating order of tip-off / tip-on CheckDetailed blocks on identical deterministic poses, after warmup. CPU-only headless microbenchmark; excludes rendering, UI, planning, scene construction and CAD import. Per-check and block-average quantiles included. Harness sample lists are allocated before timing.",
                runtime = System.Environment.Version.ToString(), godot = Engine.GetVersionInfo()["string"].AsString(),
                logicalProcessors = System.Environment.ProcessorCount, rounds = Rounds, elapsedSeconds = elapsed.Elapsed.TotalSeconds,
                results
            }, new JsonSerializerOptions { WriteIndented = true }));
            GD.Print("Collision CPU comparison written to " + output);
            GetTree().Quit();
        }
        catch (Exception exception) { GD.PrintErr(exception); GetTree().Quit(1); }
    }

    private static Measurements Summarize(List<double> times, List<double> batches, long allocations)
    {
        times.Sort(); batches.Sort();
        double Percentile(List<double> values, double fraction) => values[(int)Math.Ceiling((values.Count - 1) * fraction)];
        return new(Percentile(times, .5), Percentile(times, .95), times.Average(), Percentile(batches, .5),
            Percentile(batches, .95), allocations / (double)times.Count, times.Count);
    }

    private static float[][] MakePoses(bool moving, bool nearby)
    {
        var random = new Random(932817);
        return Enumerable.Range(0, PosesPerRound).Select(_ => RobotController.HomeAngles.Select(a =>
            a + (moving ? (float)(random.NextDouble() * 2 - 1) * (nearby ? .15f : 80) : 0)).ToArray()).ToArray();
    }

    private static Dictionary<int, Vector3[]> ReadRobotTriangles()
    {
        using var reader = new BinaryReader(File.OpenRead(ProjectSettings.GlobalizePath(RobotModel.MeshPath)));
        reader.ReadBytes(4); int groups = reader.ReadInt32(); var source = new Dictionary<int, List<Vector3>>();
        for (int group = 0; group < groups; group++)
        {
            int link = reader.ReadInt32(); reader.ReadBytes(16); int count = reader.ReadInt32();
            if (!source.TryGetValue(link, out var vertices)) source[link] = vertices = new();
            for (int i = 0; i < count; i++)
            { vertices.Add(new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())); reader.ReadBytes(12); }
        }
        return source.ToDictionary(p => p.Key, p => p.Value.ToArray());
    }
}
