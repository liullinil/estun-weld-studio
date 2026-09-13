using Godot;
using System;
using System.Collections.Generic;
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
    public const string MeshPath = "res://Assets/Robot/OEM/S20-180-Pro.meshbin";
    private bool _built;
    private readonly Dictionary<string, Material> _materials = new();

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
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = vertices;
            arrays[(int)Mesh.ArrayType.Normal] = normals;
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            var instance = new MeshInstance3D {
                Name = $"CAD_Link{link}_Surface{group:000}", Mesh = mesh,
                MaterialOverride = CadMaterial(color),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.On
            };
            (link == 0 ? this : Joints[link - 1]).AddChild(instance);
            MeshPartCount++;
            TriangleCount += vertexCount / 3;
        }
        if (stream.Position != stream.Length)
            throw new InvalidDataException("Unexpected trailing source mesh data.");
    }

    private Material CadMaterial(Color color)
    {
        string key = color.ToHtml();
        if (_materials.TryGetValue(key, out var cached)) return cached;
        float max = Math.Max(color.R, Math.Max(color.G, color.B));
        float min = Math.Min(color.R, Math.Min(color.G, color.B));
        bool chromatic = max - min > .14f;
        bool dark = max < .20f;
        var material = new StandardMaterial3D {
            AlbedoColor = color,
            Metallic = dark ? .08f : chromatic ? .12f : .20f,
            Roughness = dark ? .43f : chromatic ? .31f : .43f,
            MetallicSpecular = .5f,
            ClearcoatEnabled = !dark,
            Clearcoat = chromatic ? .18f : .10f,
            ClearcoatRoughness = .40f,
            CullMode = BaseMaterial3D.CullModeEnum.Back
        };
        if (color.A < .99f) material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        _materials.Add(key, material);
        return material;
    }
}
