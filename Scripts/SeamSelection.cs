using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EstunStudio;

/// <summary>Explicit work assignment with stable breadth-first, one-edge-at-a-time propagation.</summary>
public sealed class SeamSelection
{
    public HashSet<string> Selected { get; private set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> Scopes { get; private set; } = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _graph = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _order = new(StringComparer.Ordinal);
    public SelectionTransaction? Transaction { get; private set; }
    public sealed class SelectionTransaction
    {
        public required string Id;
        public bool Adding;
        public int Scope;
        public required string[] Order;
        public required HashSet<string> Before;
        public required Dictionary<string, int> BeforeScopes;
        public string[] Affected => Order.Take(Scope + 1).ToArray();
    }
    public void SetCandidates(IReadOnlyList<CadSeam> seams)
    {
        Finish(); _graph.Clear(); _order.Clear();
        var buckets = new Dictionary<(int, int, int), List<(string Id, Vector3 Point)>>();
        const float tolerance = .00002f;
        for (int i = 0; i < seams.Count; i++) { _graph[seams[i].Id] = new(); _order[seams[i].Id] = i; }
        foreach (var seam in seams)
        {
            if (seam.Points.Length < 2) continue;
            foreach (Vector3 point in new[] { seam.Points[0], seam.Points[^1] })
            {
                int x = (int)Math.Floor(point.X / tolerance), y = (int)Math.Floor(point.Y / tolerance), z = (int)Math.Floor(point.Z / tolerance);
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                    if (buckets.TryGetValue((x + dx, y + dy, z + dz), out var neighbors))
                        foreach (var other in neighbors)
                            if (other.Id != seam.Id && other.Point.DistanceSquaredTo(point) <= tolerance * tolerance)
                            { if (!_graph[seam.Id].Contains(other.Id)) _graph[seam.Id].Add(other.Id); if (!_graph[other.Id].Contains(seam.Id)) _graph[other.Id].Add(seam.Id); }
                if (!buckets.TryGetValue((x, y, z), out var bucket)) buckets[(x, y, z)] = bucket = new();
                bucket.Add((seam.Id, point));
            }
        }
        Selected.RemoveWhere(id => !_graph.ContainsKey(id));
    }
    public string[] Component(string id)
    {
        if (!_graph.ContainsKey(id)) return Array.Empty<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { id }; var ordered = new List<string> { id }; var frontier = new[] { id };
        while (frontier.Length > 0)
        {
            var next = frontier.SelectMany(key => _graph[key]).Where(key => !visited.Contains(key)).Distinct().OrderBy(key => _order[key]).ToArray();
            foreach (var key in next) { visited.Add(key); ordered.Add(key); } frontier = next;
        }
        return ordered.ToArray();
    }
    public SelectionTransaction? Begin(string id)
    {
        Finish(); var order = Component(id); if (order.Length == 0) return null;
        Transaction = new SelectionTransaction { Id = id, Adding = !Selected.Contains(id), Order = order, Before = new(Selected, StringComparer.Ordinal), BeforeScopes = new(Scopes, StringComparer.Ordinal) };
        ApplyScope(Scopes.GetValueOrDefault(id)); return Transaction;
    }
    public void ApplyScope(int additional)
    {
        var t = Transaction; if (t == null) return;
        t.Scope = Math.Clamp(additional, 0, t.Order.Length - 1); Selected = new(t.Before, StringComparer.Ordinal); Scopes = new(t.BeforeScopes, StringComparer.Ordinal);
        foreach (string id in t.Affected)
            if (t.Adding) { Selected.Add(id); if (!t.Before.Contains(id)) Scopes[id] = t.Scope; } else Selected.Remove(id);
        if (t.Adding) Scopes[t.Id] = t.Scope;
    }
    public void Finish() => Transaction = null;
    public void SelectAll() { Finish(); foreach (string id in _graph.Keys) { Selected.Add(id); Scopes[id] = 0; } }
    public void Clear() { Finish(); Selected.Clear(); }
}
