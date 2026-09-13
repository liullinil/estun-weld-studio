using Godot;
using System;
using System.Collections.Generic;

namespace EstunStudio;

/// <summary>A triangulated STEP assembly and BRep-derived edge candidates. All coordinates are local, metres, Y-up.</summary>
public sealed class CadDocument
{
    public string Name { get; set; } = "CAD part";
    public string SourcePath { get; set; } = "";
    public string SourceUnit { get; set; } = "mm";
    public Vector3[] Vertices { get; set; } = Array.Empty<Vector3>();
    public Vector3[] Normals { get; set; } = Array.Empty<Vector3>();
    public int[] Indices { get; set; } = Array.Empty<int>();
    public List<CadSeam> Seams { get; set; } = new();
    public Aabb Bounds { get; set; }
    public int SolidCount { get; set; }
    public int FaceCount { get; set; }
    public int TriangleCount => Indices.Length / 3;
    public string[] Warnings { get; set; } = Array.Empty<string>();
}

/// <summary>An angular edge candidate, not a guarantee that a physical weld is required. Users approve/delete candidates.</summary>
public sealed class CadSeam
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "Topology edge";
    public Vector3[] Points { get; set; } = Array.Empty<Vector3>();
    public Vector3 NormalA { get; set; } = Vector3.Up;
    public Vector3 NormalB { get; set; } = Vector3.Right;
    public Vector3[] NormalsA { get; set; } = Array.Empty<Vector3>();
    public Vector3[] NormalsB { get; set; } = Array.Empty<Vector3>();
    public float AngleDegrees { get; set; }
    public float MinAngleDegrees { get; set; }
    public float MaxAngleDegrees { get; set; }
    public bool Concave { get; set; }
    public bool Deleted { get; set; }
    public bool Enabled { get => !Deleted; set => Deleted = !value; }
    public float Length
    {
        get
        {
            float length = 0;
            for (int i = 1; i < Points.Length; i++) length += Points[i - 1].DistanceTo(Points[i]);
            return length;
        }
    }
    public bool MatchesAngleRange(float minimum, float maximum) =>
        !Deleted && minimum <= maximum && MinAngleDegrees >= minimum - .05f && MaxAngleDegrees <= maximum + .05f;
}
