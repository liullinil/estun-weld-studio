using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace EstunStudio;

/// <summary>Immutable inputs are captured on the scene thread before planning on a worker.</summary>
public sealed class WeldPlanRequest
{
    public IReadOnlyList<CadSeam> Seams { get; init; } = Array.Empty<CadSeam>();
    public Transform3D PartTransform { get; init; } = Transform3D.Identity;
    public Transform3D[] JointRest { get; init; } = RobotKinematics.StandardRestTransforms();
    public Transform3D ToolTransform { get; init; } = Transform3D.Identity;
    public float[] StartAngles { get; init; } = (float[])RobotController.HomeAngles.Clone();
    public CollisionScene Collision { get; init; } = new(Array.Empty<Vector3>(), Array.Empty<RobotCapsule>());
    public float SampleSpacing { get; init; } = .008f;
    public float ArcGap { get; init; } = .003f;
    public float RetractDistance { get; init; } = .09f;
    public float WeldSpeed { get; init; } = .025f;
    public float TravelSpeed { get; init; } = .22f;
    public string PartName { get; init; } = "Workpiece";
}

public enum WeldSeamState { Ready, Unreachable, Collision, TransferBlocked, Deleted }

public sealed class PlannedSeam
{
    public string Id { get; init; } = "";
    public Vector3[] Points { get; init; } = Array.Empty<Vector3>();
    public WeldSeamState State { get; set; }
    public string Reason { get; set; } = "";
    public bool Reversed { get; set; }
    public float Length { get; set; }
    public int Order { get; set; } = -1;
}

/// <summary>Each target follows a validated, densely sampled joint interpolation from the preceding target.</summary>
public sealed class WeldMotion
{
    public float[] TargetAngles { get; init; } = new float[6];
    public Transform3D Tcp { get; init; }
    public Vector3 WeldPoint { get; init; }
    public Vector3 WorldApproach { get; init; }
    public bool ArcOn { get; init; }
    public string SeamId { get; init; } = "";
    public float DurationSeconds { get; init; }
    public string Kind => ArcOn ? "WELD" : "TRAVEL";
}

public sealed class WeldProgram
{
    public List<WeldMotion> Motions { get; } = new();
    public List<PlannedSeam> Seams { get; } = new();
    public float[] StartAngles { get; init; } = new float[6];
    public Transform3D ToolTransform { get; init; }
    public string PartName { get; init; } = "Workpiece";
    public DateTime CreatedUtc { get; } = DateTime.UtcNow;
    public string Method => "Nearest endpoint + 2-opt; multi-orientation IK; sampled collision validation with CAD slab bounds and torch capsules";
    public int ReadyCount => Seams.Count(s => s.State == WeldSeamState.Ready);
    public int BlockedCount => Seams.Count(s => s.State != WeldSeamState.Ready && s.State != WeldSeamState.Deleted);
    public float DurationSeconds => Motions.Sum(m => m.DurationSeconds);
    public string Summary => $"{ReadyCount} ready / {BlockedCount} blocked · {Motions.Count} moves · {DurationSeconds:0}s";

    public string ToJson() => JsonSerializer.Serialize(new
    {
        format = "ESTUN Studio offline welding program", version = 1,
        robot = "ESTUN S20-180 Pro / ENCY", part = PartName, createdUtc = CreatedUtc,
        units = new { position = "metre", joints = "degree", time = "second" },
        coordinateFrame = "robot base; right handed; Y up", method = Method,
        validation = "Offline geometric simulation. Source-mesh oriented slab bounds, torch capsules and tessellated CAD; sampled motion. Requires calibrated cell and controller postprocessing before hardware use.",
        tool = Pose(ToolTransform), startJoints = StartAngles,
        seams = Seams.Select(s => new { id = s.Id, state = s.State.ToString(), s.Reason, s.Order, s.Reversed, length = s.Length }),
        moves = Motions.Select((m, i) => new { index = i + 1, type = m.Kind, seam = m.SeamId, joints = m.TargetAngles, tcp = Pose(m.Tcp), arc = m.ArcOn, duration = m.DurationSeconds })
    }, new JsonSerializerOptions { WriteIndented = true });

    public string ToCsv()
    {
        var result = new StringBuilder("index,type,seam,arc,duration_s,x_m,y_m,z_m,qx,qy,qz,qw,a1_deg,a2_deg,a3_deg,a4_deg,a5_deg,a6_deg\r\n");
        for (int i = 0; i < Motions.Count; i++)
        {
            WeldMotion m = Motions[i]; Quaternion q = m.Tcp.Basis.GetRotationQuaternion();
            result.Append(i + 1).Append(',').Append(m.Kind).Append(',').Append('"').Append(m.SeamId.Replace("\"", "\"\"")).Append("\",").Append(m.ArcOn ? 1 : 0);
            foreach (float value in new[] { m.DurationSeconds, m.Tcp.Origin.X, m.Tcp.Origin.Y, m.Tcp.Origin.Z, q.X, q.Y, q.Z, q.W }.Concat(m.TargetAngles))
                result.Append(',').Append(value.ToString("G9", CultureInfo.InvariantCulture));
            result.Append("\r\n");
        }
        return result.ToString();
    }

    private static object Pose(Transform3D value)
    {
        Quaternion q = value.Basis.GetRotationQuaternion();
        return new { position = new[] { value.Origin.X, value.Origin.Y, value.Origin.Z }, quaternion = new[] { q.X, q.Y, q.Z, q.W } };
    }
}
