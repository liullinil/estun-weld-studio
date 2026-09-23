using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EstunStudio;

/// <summary>Reproducible timing and exact-result fingerprints, including independent motion revalidation.</summary>
public partial class PlannerPerformance : Node
{
    public override async void _Ready()
    {
        try
        {
            var watch = Stopwatch.StartNew();
            using var reader = new BinaryReader(File.OpenRead(ProjectSettings.GlobalizePath(RobotModel.MeshPath)));
            reader.ReadBytes(4); int groups = reader.ReadInt32(); var source = new Dictionary<int, List<Vector3>>();
            for (int group = 0; group < groups; group++)
            {
                int link = reader.ReadInt32(); reader.ReadBytes(16); int count = reader.ReadInt32();
                if (!source.TryGetValue(link, out var vertices)) source[link] = vertices = new();
                for (int i = 0; i < count; i++) { vertices.Add(new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())); reader.ReadBytes(12); }
            }
            watch.Restart();
            var capsules = CollisionScene.CreateRobotCapsules(source.ToDictionary(p => p.Key, p => p.Value.ToArray())).Concat(WeldTorch.CollisionVolumes()).ToArray();
            double collisionModelSeconds = watch.Elapsed.TotalSeconds;
            var document = await new CadImportService().ImportAsync(ProjectSettings.GlobalizePath("res://Assets/Samples/WeldingBracket.step"));
            var placement = new Transform3D(Basis.Identity, new Vector3(.85f, .15f, 0) - new Vector3(document.Bounds.GetCenter().X, document.Bounds.Position.Y, document.Bounds.GetCenter().Z));
            var plinth = new CylinderMesh { TopRadius = .7f, BottomRadius = .7f, Height = .2f, RadialSegments = 64 }.GetFaces().Select(p => p + new Vector3(0, -.11f, 0)).ToArray();
            var collision = new CollisionScene(document.Indices.Select(i => placement * document.Vertices[i]).ToArray(), capsules, staticTriangles: plinth, toolTip: WeldTorch.ToolTransform.Origin);
            var request = new WeldPlanRequest { Seams = document.Seams.Where(s => s.Kind == "Part contact").ToArray(), PartTransform = placement, ToolTransform = WeldTorch.ToolTransform, Collision = collision, PartName = document.Name };
            long allocated = GC.GetTotalAllocatedBytes(true);
            watch.Restart();
            var program = WeldPlanner.Plan(request, progress: (fraction, message) => GD.Print(message));
            double planSeconds = watch.Elapsed.TotalSeconds;
            long planAllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated;
            watch.Restart();
            float[] previous = request.StartAngles;
            foreach (var motion in program.Motions)
            {
                if (!collision.CheckMotion(previous, motion.TargetAngles, out string reason)) throw new InvalidOperationException(reason);
                previous = motion.TargetAngles;
            }
            double revalidationSeconds = watch.Elapsed.TotalSeconds;
            var random = new Random(71937); var samples = new StringBuilder();
            watch.Restart();
            for (int sample = 0; sample < 4096; sample++)
            {
                float[] angles = new float[6];
                for (int joint = 0; joint < 6; joint++) angles[joint] = Mathf.Lerp(RobotController.JointMinimum[joint], RobotController.JointMaximum[joint], (float)random.NextDouble());
                bool clear = collision.Check(angles, out string reason);
                samples.Append(clear).Append(':').Append(reason).Append('\n');
            }
            double poseCheckSeconds = watch.Elapsed.TotalSeconds;
            string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
            var result = new
            {
                collisionModelSeconds, planSeconds, planAllocatedBytes, revalidationSeconds, poseCheckSeconds,
                ready = program.ReadyCount, blocked = program.BlockedCount, moves = program.Motions.Count,
                motionSha256 = Hash(program.ToCsv()),
                seamSha256 = Hash(JsonSerializer.Serialize(program.Seams.Select(s => new { s.Id, s.State, s.Reason, s.Order, s.Reversed }))),
                poseCheckSha256 = Hash(samples.ToString())
            };
            string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            string output = System.Environment.GetEnvironmentVariable("PLANNER_BENCHMARK_OUTPUT") ?? ProjectSettings.GlobalizePath("res://artifacts/performance-planner-latest.json");
            File.WriteAllText(output, json); GD.Print(json); GetTree().Quit();
        }
        catch (Exception exception) { GD.PrintErr(exception); GetTree().Quit(1); }
    }
}
