using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
namespace EstunStudio;

public partial class CadPart : Node3D
{
    public CadDocument Document { get; private set; } = null!;
    public event Action? SeamSelectionChanged;
    public string? SelectedSeam { get; private set; }
    public SeamSelection Selection { get; } = new();
    public float MinimumAngle { get; set; } = 85;
    public float MaximumAngle { get; set; } = 95;
    public float MinimumLength { get; set; }
    public float MaximumLength { get; set; } = float.PositiveInfinity;
    public bool ConcaveOnly { get; set; }
    private readonly Dictionary<string, MeshInstance3D> _seamMeshes = new();
    private readonly Dictionary<string, PlannedSeam> _results = new();
    private Camera3D _camera = null!;
    private bool _overlaysVisible = true;
    public bool OverlaysVisible { get => _overlaysVisible; set { _overlaysVisible = value; foreach (var mesh in _seamMeshes.Values) mesh.Visible = value; } }
    public void Build(CadDocument document, Camera3D camera)
    {
        Document = document; _camera = camera;
        var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = document.Vertices; arrays[(int)Mesh.ArrayType.Normal] = document.Normals; arrays[(int)Mesh.ArrayType.Index] = document.Indices;
        var mesh = new ArrayMesh(); mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        AddChild(new MeshInstance3D { Name = "CAD workpiece", Mesh = mesh, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color("778997"), Metallic = .68f, Roughness = .36f, CullMode = BaseMaterial3D.CullModeEnum.Disabled } });
        UpdateCandidates();
    }
    public List<CadSeam> Candidates() => Document.Seams.Where(s => s.MatchesAngleRange(MinimumAngle, MaximumAngle) && s.Length >= MinimumLength - .000001f && s.Length <= MaximumLength + .000001f && (!ConcaveOnly || s.Concave || s.Kind.Contains("contact", StringComparison.OrdinalIgnoreCase))).ToList();
    public List<CadSeam> AssignedSeams() => Candidates().Where(s => Selection.Selected.Contains(s.Id)).ToList();
    public void UpdateCandidates() { Selection.SetCandidates(Candidates()); RefreshSeams(); }
    public void ResetResults() { _results.Clear(); RefreshSeams(); }
    public void SetResult(string id, bool reachable) => _results[id] = new PlannedSeam { Id = id, State = reachable ? WeldSeamState.Ready : WeldSeamState.Warning, Reason = reachable ? "" : "Unavailable path" };
    public void SetResult(PlannedSeam result) => _results[result.Id] = result;
    public PlannedSeam? Result(string id) => _results.GetValueOrDefault(id);
    public bool IsWarning(string id) => Selection.Selected.Contains(id) && _results.TryGetValue(id, out var result) && result.State != WeldSeamState.Ready && result.State != WeldSeamState.Deleted;
    public string WarningHint(string? id) => id != null && IsWarning(id) ? _results[id].Reason : "";
    public void RefreshSeams()
    {
        foreach (var mesh in _seamMeshes.Values) { RemoveChild(mesh); mesh.QueueFree(); } _seamMeshes.Clear();
        foreach (var seam in Candidates())
        {
            bool selected = Selection.Selected.Contains(seam.Id);
            Color color = IsWarning(seam.Id) ? new Color("ff405a") : selected ? new Color("43fff0") : new Color("b7c9d3");
            var material = new StandardMaterial3D { AlbedoColor = color, EmissionEnabled = true, Emission = color, EmissionEnergyMultiplier = selected ? 1.8f : .35f, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
            var mesh = Tube(seam.Points, selected ? .0017f : .0007f, material); mesh.Visible = OverlaysVisible; AddChild(mesh); _seamMeshes.Add(seam.Id, mesh);
        }
    }
    public void Select(string? id) { if (id == null) return; Selection.Begin(id); SelectedSeam = id; RefreshSeams(); SeamSelectionChanged?.Invoke(); }
    public void ApplyPropagation(int scope) { Selection.ApplyScope(scope); RefreshSeams(); SeamSelectionChanged?.Invoke(); }
    public void SelectAll() { Selection.SelectAll(); RefreshSeams(); SeamSelectionChanged?.Invoke(); }
    public void UnselectAll() { Selection.Clear(); RefreshSeams(); SeamSelectionChanged?.Invoke(); }
    public void DisableWarnings() { Selection.Finish(); Selection.Selected.RemoveWhere(IsWarning); RefreshSeams(); SeamSelectionChanged?.Invoke(); }
    // Retained for older integrations. Candidates are never deleted by the current UI.
    public bool DeleteSelected() => false;
    public void Restore() => SelectAll();
    public string? SeamAt(Vector2 mouse)
    {
        if (!OverlaysVisible) return null;
        float best = 9; string? id = null;
        foreach (var seam in Candidates()) for (int i = 1; i < seam.Points.Length; i++)
        {
            Vector3 a = GlobalTransform * seam.Points[i - 1], b = GlobalTransform * seam.Points[i];
            if (_camera.IsPositionBehind(a) || _camera.IsPositionBehind(b)) continue;
            float distance = TransformGizmo.SegmentDistance(mouse, _camera.UnprojectPosition(a), _camera.UnprojectPosition(b));
            if (distance < best) { best = distance; id = seam.Id; }
        }
        return id;
    }
    public bool PickSeam(Vector2 mouse) { string? id = SeamAt(mouse); if (id == null) return false; Select(id); return true; }
    public static MeshInstance3D Tube(Vector3[] points, float radius, Material material)
    {
        const int sides = 8; var vertices = new List<Vector3>(); var normals = new List<Vector3>();
        for (int i = 1; i < points.Length; i++)
        {
            Vector3 tangent = (points[i] - points[i - 1]).Normalized(); if (tangent.LengthSquared() < .1f) continue;
            Vector3 u = tangent.Cross(Mathf.Abs(tangent.Dot(Vector3.Up)) < .95f ? Vector3.Up : Vector3.Right).Normalized(), v = tangent.Cross(u);
            for (int j = 0; j < sides; j++)
            {
                Vector3 n0 = u * Mathf.Cos(j * Mathf.Tau / sides) + v * Mathf.Sin(j * Mathf.Tau / sides), n1 = u * Mathf.Cos((j + 1) * Mathf.Tau / sides) + v * Mathf.Sin((j + 1) * Mathf.Tau / sides);
                Vector3 a = points[i - 1] + n0 * radius, b = points[i] + n0 * radius, c = points[i] + n1 * radius, d = points[i - 1] + n1 * radius;
                vertices.AddRange(new[] { a, c, b, a, d, c }); normals.AddRange(new[] { n0, n1, n0, n0, n1, n1 });
            }
        }
        var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max); arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray(); arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
        var mesh = new ArrayMesh(); if (vertices.Count > 0) mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return new MeshInstance3D { Mesh = mesh, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
    }
}
