using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EstunStudio;

/// <summary>Verify indexed drawing and depth meshes against every original CAD triangle.</summary>
public partial class RobotMeshChecks : Node
{
    private int _checks;

    public override void _Ready()
    {
        try
        {
            var model = new RobotModel();
            AddChild(model);
            model.Build();
            var meshes = Descendants(model).OfType<MeshInstance3D>()
                .OrderBy(mesh => mesh.Name.ToString()[mesh.Name.ToString().LastIndexOf("Surface", StringComparison.Ordinal)..])
                .ToArray();
            var originalByLink = new Dictionary<int, List<Vector3>>();
            using var reader = new BinaryReader(new MemoryStream(Godot.FileAccess.GetFileAsBytes(RobotModel.MeshPath)));
            Require(System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4)) == "ESM1", "Original CAD binary is available");
            int groups = reader.ReadInt32();
            Require(groups == meshes.Length, "All original material surfaces remain present");
            int sourceVertices = 0, renderVertices = 0, depthVertices = 0;
            for (int group = 0; group < groups; group++)
            {
                int link = reader.ReadInt32();
                reader.ReadBytes(16);
                int count = reader.ReadInt32();
                var mesh = (ArrayMesh)meshes[group].Mesh;
                var surface = mesh.SurfaceGetArrays(0);
                var vertices = surface[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var normals = surface[(int)Mesh.ArrayType.Normal].AsVector3Array();
                var indices = surface[(int)Mesh.ArrayType.Index].AsInt32Array();
                Require(indices.Length == count, $"Surface {group}: every CAD triangle retained");
                Require(mesh.ShadowMesh != null, $"Surface {group}: fused depth/shadow mesh exists");
                var depth = mesh.ShadowMesh!.SurfaceGetArrays(0);
                var depthPoints = depth[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var depthIndices = depth[(int)Mesh.ArrayType.Index].AsInt32Array();
                Require(depthIndices.Length == count, $"Surface {group}: depth pass preserves complete triangle topology");
                if (!originalByLink.TryGetValue(link, out var original))
                    originalByLink[link] = original = new List<Vector3>();
                for (int i = 0; i < count; i++)
                {
                    var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    var normal = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    original.Add(position);
                    if (vertices[indices[i]] != position || depthPoints[depthIndices[i]] != position)
                        throw new InvalidOperationException($"Surface {group}, triangle vertex {i}: CAD position or winding changed");
                    // Godot packs normals in the same octahedral representation used by
                    // the old non-indexed mesh. Allow only that storage quantization.
                    if (normals[indices[i]].DistanceTo(normal) > .0002f)
                        throw new InvalidOperationException($"Surface {group}, triangle vertex {i}: a source normal was smoothed or changed");
                }
                Require(true, $"Surface {group}: all source positions, sharp normals and winding preserved");
                Require(meshes[group].MaterialOverride is ShaderMaterial material &&
                    material.Shader.ResourcePath == "res://Shaders/RobotPaint.gdshader",
                    $"Surface {group}: filtered dielectric paint uses native CAD shading");
                sourceVertices += count;
                renderVertices += vertices.Length;
                depthVertices += depthPoints.Length;
            }
            Require(reader.BaseStream.Position == reader.BaseStream.Length, "Entire original asset verified");
            var collisions = model.GetLinkTriangles();
            Require(collisions.Count == 7 && originalByLink.All(pair => collisions[pair.Key].SequenceEqual(pair.Value)),
                "Collision API preserves all original triangles and link frames independently of display indexing");
            Require(sourceVertices == 1544499 && model.TriangleCount == 514833,
                "Original manufacturer's CAD triangle count is unchanged");
            Require(model.SourceVertexCount == sourceVertices && model.RenderVertexCount == renderVertices && model.ShadowVertexCount == depthVertices,
                "Reported render and depth mesh metrics match actual GPU arrays");
            Require(renderVertices < sourceVertices * .23f && depthVertices < renderVertices,
                "Exact sharing removes over 77% of display vertices and further reduces depth/shadow vertices");
            var rest = RobotKinematics.StandardRestTransforms();
            foreach (float[] pose in new[] { RobotController.HomeAngles, new[] { -21f, 61f, -37f, 29f, 84f, -13f } })
            {
                for (int i = 0; i < 6; i++)
                    model.Joints[i].Transform = rest[i] * new Transform3D(new Basis(RobotController.JointAxes[i], Mathf.DegToRad(pose[i])), Vector3.Zero);
                Transform3D actual = model.GlobalTransform.AffineInverse() * model.ToolTip.GlobalTransform;
                Transform3D expected = RobotKinematics.Forward(pose, rest, RobotKinematics.StandardToolTransform);
                Require(actual.Origin.DistanceTo(expected.Origin) < .000001f &&
                    RobotKinematics.RotationError(actual.Basis, expected.Basis).Length() < .000001f,
                    "Indexed geometry preserves the original six-axis FK at home and articulated poses");
            }
            GD.Print($"CAD OPTIMIZATION: {sourceVertices:N0} source vertices → {renderVertices:N0} display / {depthVertices:N0} depth vertices; {model.TriangleCount:N0} triangles unchanged");
            GD.Print($"PASS: {_checks} complete source CAD / indexed rendering / collision preservation checks");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PrintErr("FAIL: " + error);
            GetTree().Quit(1);
        }
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            yield return child;
            foreach (Node descendant in Descendants(child)) yield return descendant;
        }
    }

    private void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
        GD.Print("  OK  " + message);
    }
}
