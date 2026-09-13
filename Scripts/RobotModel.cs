using Godot;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;

namespace EstunStudio;

/// <summary>
/// Loads the actual segmented CAD geometry distributed with the selected source model.
/// Meshes are converted once into joint-local Godot coordinates by the source importer.
/// There are no procedural replacement robot housings or invented robot accessories.
/// </summary>
public partial class RobotModel : Node3D
{
    public Node3D[] Joints { get; } = new Node3D[6];
    public Node3D ToolTip { get; private set; } = null!;
    public int MeshPartCount { get; private set; }
    public int TriangleCount { get; private set; }
    public int SourceVertexCount { get; private set; }
    public int RenderVertexCount { get; private set; }
    public int ShadowVertexCount { get; private set; }
    public const string MeshPath = "res://Assets/Robot/OEM/S20-180-Pro.meshbin";
    private bool _built;
    private readonly Dictionary<string, Material> _materials = new();
    private IReadOnlyDictionary<int, Vector3[]> _linkTriangles =
        new ReadOnlyDictionary<int, Vector3[]>(new Dictionary<int, Vector3[]>());
    private Shader? _paintShader;

    /// <summary>
    /// Original CAD triangle triplets in each link's local frame, independent of the
    /// indexed display mesh. Collision planning must use these unchanged source faces.
    /// Arrays are shared read-only data; callers must not modify them.
    /// </summary>
    public IReadOnlyDictionary<int, Vector3[]> GetLinkTriangles() => _linkTriangles;

    public void Build()
    {
        if (_built) return;
        Name = "ESTUN_S20_180_Pro";
        Transform3D[] rest = RobotKinematics.StandardRestTransforms();
        Node3D parent = this;
        for (int i = 0; i < 6; i++)
        {
            var joint = new Node3D { Name = $"A{i + 1}_Pivot", Transform = rest[i] };
            parent.AddChild(joint);
            Joints[i] = joint;
            parent = joint;
        }
        ToolTip = new Node3D { Name = "TCP", Transform = RobotKinematics.StandardToolTransform };
        Joints[5].AddChild(ToolTip);
        LoadCadMeshes(MeshPath);
        _built = true;
    }

    private void LoadCadMeshes(string path)
    {
        if (!Godot.FileAccess.FileExists(path))
            throw new FileNotFoundException("The original segmented robot mesh asset is missing.", path);
        byte[] bytes = Godot.FileAccess.GetFileAsBytes(path);
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream);
        string magic = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (magic != "ESM1") throw new InvalidDataException("Invalid source mesh asset format.");
        int groups = reader.ReadInt32();
        if (groups < 1 || groups > 50000) throw new InvalidDataException("Invalid source mesh group count.");
        var sourceLinks = new Dictionary<int, List<Vector3>>();
        for (int group = 0; group < groups; group++)
        {
            int link = reader.ReadInt32();
            Color color = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            int vertexCount = reader.ReadInt32();
            if (link < 0 || link > 6 || vertexCount < 3 || vertexCount % 3 != 0 || vertexCount > 12000000)
                throw new InvalidDataException($"Invalid geometry group {group}.");
            var vertices = new Vector3[vertexCount];
            var normals = new Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                vertices[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                normals[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                if (!vertices[i].IsFinite() || !normals[i].IsFinite())
                    throw new InvalidDataException("Non-finite CAD vertex encountered.");
            }
            if (!sourceLinks.TryGetValue(link, out var source))
                sourceLinks[link] = source = new List<Vector3>(vertexCount);
            source.AddRange(vertices);
            var mesh = CreateIndexedMesh(vertices, normals);
            var instance = new MeshInstance3D {
                Name = $"CAD_Link{link}_Surface{group:000}", Mesh = mesh,
                MaterialOverride = CadMaterial(color),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.On
            };
            (link == 0 ? this : Joints[link - 1]).AddChild(instance);
            MeshPartCount++;
            TriangleCount += vertexCount / 3;
            SourceVertexCount += vertexCount;
        }
        if (stream.Position != stream.Length)
            throw new InvalidDataException("Unexpected trailing source mesh data.");
        var triangles = new Dictionary<int, Vector3[]>();
        foreach (var pair in sourceLinks) triangles.Add(pair.Key, pair.Value.ToArray());
        _linkTriangles = new ReadOnlyDictionary<int, Vector3[]>(triangles);
    }

    private ArrayMesh CreateIndexedMesh(Vector3[] sourceVertices, Vector3[] sourceNormals)
    {
        // Do not round positions, merge sharp normals, or decimate the supplied CAD.
        // Exact position + normal keys share only genuinely identical source vertices.
        // All source triangles remain in their original order and winding, including
        // the occasional opposite-facing coincident faces used for thin CAD walls.
        var vertexLookup = new Dictionary<(Vector3 Position, Vector3 Normal), int>();
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var indices = new int[sourceVertices.Length];
        var shadowLookup = new Dictionary<Vector3, int>();
        var shadowVertices = new List<Vector3>();
        var shadowIndices = new int[sourceVertices.Length];
        for (int i = 0; i < sourceVertices.Length; i++)
        {
            var key = (sourceVertices[i], sourceNormals[i]);
            if (!vertexLookup.TryGetValue(key, out int index))
            {
                vertexLookup.Add(key, index = vertices.Count);
                vertices.Add(key.Item1);
                normals.Add(key.Item2);
            }
            indices[i] = index;
            // Godot uses this position-only mesh for depth prepass and shadow maps.
            // Normals need not split vertices in passes that never read normals.
            if (!shadowLookup.TryGetValue(sourceVertices[i], out int shadowIndex))
            {
                shadowLookup.Add(sourceVertices[i], shadowIndex = shadowVertices.Count);
                shadowVertices.Add(sourceVertices[i]);
            }
            shadowIndices[i] = shadowIndex;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        var shadowArrays = new Godot.Collections.Array();
        shadowArrays.Resize((int)Mesh.ArrayType.Max);
        shadowArrays[(int)Mesh.ArrayType.Vertex] = shadowVertices.ToArray();
        shadowArrays[(int)Mesh.ArrayType.Index] = shadowIndices;
        var shadowMesh = new ArrayMesh();
        shadowMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, shadowArrays);
        mesh.ShadowMesh = shadowMesh;
        RenderVertexCount += vertices.Count;
        ShadowVertexCount += shadowVertices.Count;
        return mesh;
    }

    private Material CadMaterial(Color color)
    {
        string key = color.ToHtml();
        if (_materials.TryGetValue(key, out var cached)) return cached;
        float max = Math.Max(color.R, Math.Max(color.G, color.B));
        float min = Math.Min(color.R, Math.Min(color.G, color.B));
        bool chromatic = max - min > .14f;
        bool dark = max < .36f;
        _paintShader ??= GD.Load<Shader>("res://Shaders/RobotPaint.gdshader");
        var material = new ShaderMaterial { Shader = _paintShader };
        material.SetShaderParameter("paint_color", color);
        material.SetShaderParameter("finish", dark ? .48f : chromatic ? .40f : .36f);
        material.SetShaderParameter("coat", dark ? .02f : .08f);
        _materials.Add(key, material);
        return material;
    }
}
