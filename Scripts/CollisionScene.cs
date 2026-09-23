using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace EstunStudio;

/// <param name="Link">0 = fixed base, 1..6 = articulated links, 7 = torch in flange coordinates.</param>
public readonly record struct RobotCapsule(int Link, Vector3 A, Vector3 B, float Radius, Aabb? LocalBounds = null);

public sealed record CollisionContact(string Kind, string Reason, int LinkA, int LinkB = -1)
{
    public int[] Links => LinkB >= 0 ? new[] { LinkA, LinkB } : LinkA >= 0 ? new[] { LinkA } : Array.Empty<int>();
}

/// <summary>Pure-math BVH against imported CAD; capsule broad phase and tight source-mesh slab bounds. All coordinates are robot-base metres.</summary>
public sealed class CollisionScene
{
    private readonly TriangleBvh _part;
    private readonly TriangleBvh _fixtures;
    private readonly RobotCapsule[] _capsules;
    private readonly Transform3D[] _rest;
    private readonly float _floorY;
    private readonly Vector3? _toolTip;
    private readonly CollisionPair[] _selfPairs;
    private readonly CollisionPairGroup[] _selfPairGroups;
    private readonly SourceSelfGeometry? _sourceSelf;
    private static readonly ConditionalWeakTable<IReadOnlyDictionary<int, Vector3[]>, SourceSelfGeometry> SourceCache = new();
    private sealed class SourceSelfGeometry
    {
        public readonly TriangleBvh Base, Shoulder;
        public SourceSelfGeometry(IReadOnlyDictionary<int, Vector3[]> source)
        {
            Base = new TriangleBvh(source[0], collectComponentSamples: true);
            Shoulder = new TriangleBvh(source[2], collectComponentSamples: true);
        }
    }
    private readonly record struct CollisionPair(int A, int B, int Group, float RadiusSquared);
    private readonly record struct CollisionPairGroup(int Start, int End, int LinkA, int LinkB);
    public float Margin { get; }
    public Aabb PartBounds => _part.Bounds;
    public int CapsuleCount => _capsules.Length;

    public CollisionScene(Vector3[] partTriangles, IEnumerable<RobotCapsule> robotCapsules,
        Transform3D[]? rest = null, float floorY = -.22f, float margin = .002f, Vector3[]? staticTriangles = null,
        IReadOnlyDictionary<int, Vector3[]>? robotTriangles = null, Vector3? toolTip = null)
    {
        _part = new TriangleBvh(partTriangles);
        _fixtures = new TriangleBvh(staticTriangles ?? Array.Empty<Vector3>());
        _capsules = robotCapsules.ToArray();
        if (_capsules.Any(c => c.Link < 0 || c.Link > 7 || c.Radius <= 0 || !c.A.IsFinite() || !c.B.IsFinite()))
            throw new ArgumentException("Invalid robot collision capsule.");
        _rest = rest ?? RobotKinematics.StandardRestTransforms();
        _floorY = floorY;
        _toolTip = toolTip;
        Margin = Math.Max(0, margin);
        // Base and A2 are not adjacent and must still collide. Their curved source housings
        // leave gaps inside the slab boxes, however, so confirm that pair using the CAD mesh.
        // Sharing the immutable source dictionary also shares the expensive source BVHs.
        if (robotTriangles != null && robotTriangles.ContainsKey(0) && robotTriangles.ContainsKey(2))
            _sourceSelf = SourceCache.GetValue(robotTriangles, source => new SourceSelfGeometry(source));
        var pairs = new List<CollisionPair>();
        for (int i = 0; i < _capsules.Length; i++)
            for (int j = i + 1; j < _capsules.Length; j++)
            {
                if (Math.Abs(_capsules[i].Link - _capsules[j].Link) <= 1) continue;
                float radius = _capsules[i].Radius + _capsules[j].Radius + Margin;
                pairs.Add(new CollisionPair(i, j, _capsules[i].Link * 8 + _capsules[j].Link, radius * radius));
            }
        _selfPairs = pairs.ToArray();
        var groups = new List<CollisionPairGroup>();
        for (int start = 0; start < _selfPairs.Length;)
        {
            int end = start + 1;
            while (end < _selfPairs.Length && _selfPairs[end].Group == _selfPairs[start].Group) end++;
            groups.Add(new CollisionPairGroup(start, end, _selfPairs[start].Group / 8, _selfPairs[start].Group % 8));
            start = end;
        }
        _selfPairGroups = groups.ToArray();
    }

    public bool Check(float[] angles, out string reason) => CheckPose(angles, out reason, null);

    /// <summary>Cheap rejection for a proposed torch pose before IK. The exact
    /// Link7 floor/part/fixture tests are shared with full checking; a clear result
    /// is only a prefilter and does not validate robot links or self collisions.</summary>
    public bool CheckToolPose(Transform3D tcp, Transform3D toolTransform, out string reason)
    {
        if (tcp.Origin.Y < _floorY) { reason = "Torch tip / floor collision"; return false; }
        if (_part.ContainsPoint(tcp.Origin)) { reason = "Torch tip inside workpiece"; return false; }
        if (_fixtures.ContainsPoint(tcp.Origin)) { reason = "Torch tip inside fixture"; return false; }
        Transform3D flange = tcp * toolTransform.AffineInverse();
        foreach (RobotCapsule local in _capsules)
        {
            if (local.Link != 7) continue;
            Vector3 a = flange * local.A, b = flange * local.B;
            bool hasBox = local.LocalBounds.HasValue;
            OrientedBounds box = hasBox ? new OrientedBounds(local.LocalBounds!.Value, flange) : default;
            float floor = hasBox ? box.MinimumY : Math.Min(a.Y, b.Y) - local.Radius;
            if (floor < _floorY + Margin) { reason = "Torch / floor collision"; return false; }
            if (hasBox ? _part.IntersectsBox(box, Margin) : _part.IntersectsCapsule(a, b, local.Radius + Margin))
            { reason = "Torch / workpiece collision"; return false; }
            if (hasBox ? _fixtures.IntersectsBox(box, Margin) : _fixtures.IntersectsCapsule(a, b, local.Radius + Margin))
            { reason = "Torch / fixture collision"; return false; }
        }
        reason = ""; return true;
    }

    /// <summary>All implicated links for pose visualization, using the exact same narrow phase and adjacency exclusions as planning.</summary>
    public bool CheckDetailed(float[] angles, out string reason, out CollisionContact[] contacts)
    {
        var hits = new List<CollisionContact>();
        bool clear = CheckPose(angles, out reason, hits);
        contacts = hits.Distinct().ToArray();
        return clear;
    }

    private bool CheckPose(float[] angles, out string reason, List<CollisionContact>? contacts)
    {
        // A motion contains thousands of samples. Scratch data lives on the stack, never in mutable scene state,
        // so concurrent planning/checks remain independent and sampling generates no per-pose garbage.
        Span<Transform3D> transforms = stackalloc Transform3D[7];
        Span<RobotCapsule> world = _capsules.Length <= 256 ? stackalloc RobotCapsule[_capsules.Length] : new RobotCapsule[_capsules.Length];
        Span<OrientedBounds> boxes = _capsules.Length <= 256 ? stackalloc OrientedBounds[_capsules.Length] : new OrientedBounds[_capsules.Length];
        Span<Aabb> bounds = _capsules.Length <= 256 ? stackalloc Aabb[_capsules.Length] : new Aabb[_capsules.Length];
        return Check(angles, transforms, world, boxes, bounds, out reason, contacts);
    }

    private bool Check(ReadOnlySpan<float> angles, Span<Transform3D> transforms, Span<RobotCapsule> world,
        Span<OrientedBounds> boxes, Span<Aabb> bounds, out string reason, List<CollisionContact>? contacts = null)
    {
        if (angles.Length != 6) { reason = "Joint travel limit"; contacts?.Add(new("joint-limit", reason, -1)); return false; }
        for (int i = 0; i < 6; i++)
            if (!float.IsFinite(angles[i]) || angles[i] < RobotController.JointMinimum[i] || angles[i] > RobotController.JointMaximum[i])
            { reason = "Joint travel limit"; contacts?.Add(new("joint-limit", reason, i + 1)); return false; }
        LinkTransforms(angles, _rest, transforms);
        Span<Vector3> linkMin = stackalloc Vector3[8], linkMax = stackalloc Vector3[8];
        linkMin.Fill(Vector3.One * float.PositiveInfinity); linkMax.Fill(Vector3.One * float.NegativeInfinity);
        for (int i = 0; i < _capsules.Length; i++)
        {
            RobotCapsule c = _capsules[i]; Transform3D pose = transforms[Math.Min(c.Link, 6)];
            world[i] = c = c with { A = pose * c.A, B = pose * c.B };
            bool hasBox = c.LocalBounds.HasValue;
            if (hasBox) boxes[i] = new OrientedBounds(c.LocalBounds!.Value, pose);
            float floor = hasBox ? boxes[i].MinimumY : Math.Min(c.A.Y, c.B.Y) - c.Radius;
            if (c.Link > 0 && floor < _floorY + Margin)
            { reason = $"{LinkName(c.Link)} / floor collision"; if (contacts == null) return false; contacts.Add(new("floor", reason, c.Link)); }
            bool partHit = hasBox ? _part.IntersectsBox(boxes[i], Margin) : _part.IntersectsCapsule(c.A, c.B, c.Radius + Margin);
            if (partHit)
            { reason = $"{LinkName(c.Link)} / workpiece collision"; if (contacts == null) return false; contacts.Add(new("workpiece", reason, c.Link)); }
            if (c.Link > 0 && (hasBox ? _fixtures.IntersectsBox(boxes[i], Margin) : _fixtures.IntersectsCapsule(c.A, c.B, c.Radius + Margin)))
            { reason = $"{LinkName(c.Link)} / fixture collision"; if (contacts == null) return false; contacts.Add(new("fixture", reason, c.Link)); }
            // This only rejects separated enclosing capsules. The original capsule/OBB narrow phase remains authoritative.
            Vector3 padding = Vector3.One * (c.Radius + Margin + .000001f);
            Vector3 low = c.A.Min(c.B) - padding;
            Vector3 high = c.A.Max(c.B) + padding;
            bounds[i] = new Aabb(low, high - low);
            linkMin[c.Link] = linkMin[c.Link].Min(low); linkMax[c.Link] = linkMax[c.Link].Max(high);
        }
        // The wire tip sits at the process arc gap; do not apply the body clearance
        // margin here. Containment must still be checked at every swept pose, not
        // just the few representative poses used to reject orientation candidates.
        if (_toolTip is Vector3 localTip)
        {
            Vector3 tip = transforms[6] * localTip;
            if (tip.Y < _floorY)
            { reason = "Torch tip / floor collision"; if (contacts == null) return false; contacts.Add(new("floor", reason, 7)); }
            if (_part.ContainsPoint(tip))
            { reason = "Torch tip inside workpiece"; if (contacts == null) return false; contacts.Add(new("workpiece", reason, 7)); }
            if (_fixtures.ContainsPoint(tip))
            { reason = "Torch tip inside fixture"; if (contacts == null) return false; contacts.Add(new("fixture", reason, 7)); }
        }
        bool? baseShoulderHit = null;
        foreach (CollisionPairGroup group in _selfPairGroups)
        {
            Vector3 lowA = linkMin[group.LinkA], highA = linkMax[group.LinkA], lowB = linkMin[group.LinkB], highB = linkMax[group.LinkB];
            if (lowA.X > highB.X || highA.X < lowB.X || lowA.Y > highB.Y || highA.Y < lowB.Y || lowA.Z > highB.Z || highA.Z < lowB.Z) continue;
            for (int pairIndex = group.Start; pairIndex < group.End; pairIndex++)
            {
                CollisionPair pair = _selfPairs[pairIndex]; int i = pair.A, j = pair.B;
                if (!bounds[i].Intersects(bounds[j])) continue;
                RobotCapsule a = world[i], b = world[j];
                if (DistanceSegmentSegmentSquared(a.A, a.B, b.A, b.B) >= pair.RadiusSquared) continue;
                bool hit;
                if (a.LocalBounds.HasValue && b.LocalBounds.HasValue) hit = boxes[i].Intersects(boxes[j], Margin);
                else if (a.LocalBounds.HasValue) hit = boxes[i].IntersectsCapsule(b.A, b.B, b.Radius + Margin);
                else if (b.LocalBounds.HasValue) hit = boxes[j].IntersectsCapsule(a.A, a.B, a.Radius + Margin);
                else hit = true;
                if (hit && _sourceSelf != null && ((a.Link == 0 && b.Link == 2) || (a.Link == 2 && b.Link == 0)))
                    hit = baseShoulderHit ??= _sourceSelf.Base.IntersectsMesh(_sourceSelf.Shoulder, transforms[2], Margin);
                if (hit)
                { reason = $"Self collision: {LinkName(a.Link)} / {LinkName(b.Link)}"; if (contacts == null) return false; contacts.Add(new("self", reason, a.Link, b.Link)); }
            }
        }
        reason = contacts is { Count: > 0 } ? contacts[0].Reason : "";
        return contacts == null || contacts.Count == 0;
    }

    /// <summary>Motion sampling limits a 2.7 m swept radius to about 1.5 mm per step (joint-angle sum maximum 0.032 degrees).</summary>
    public bool CheckMotion(float[] from, float[] to, out string reason, CancellationToken cancellation = default)
    {
        float sum = 0; for (int i = 0; i < 6; i++) sum += Math.Abs(to[i] - from[i]);
        int steps = Math.Max(1, (int)Math.Ceiling(sum / .032f));
        Span<float> pose = stackalloc float[6];
        Span<Transform3D> transforms = stackalloc Transform3D[7];
        Span<RobotCapsule> world = _capsules.Length <= 256 ? stackalloc RobotCapsule[_capsules.Length] : new RobotCapsule[_capsules.Length];
        Span<OrientedBounds> boxes = _capsules.Length <= 256 ? stackalloc OrientedBounds[_capsules.Length] : new OrientedBounds[_capsules.Length];
        Span<Aabb> bounds = _capsules.Length <= 256 ? stackalloc Aabb[_capsules.Length] : new Aabb[_capsules.Length];
        for (int sample = 0; sample <= steps; sample++)
        {
            if ((sample & 31) == 0) cancellation.ThrowIfCancellationRequested();
            float t = sample / (float)steps;
            for (int j = 0; j < 6; j++) pose[j] = Mathf.Lerp(from[j], to[j], t);
            if (!Check(pose, transforms, world, boxes, bounds, out reason)) return false;
        }
        reason = ""; return true;
    }

    public static Transform3D[] LinkTransforms(float[] angles, Transform3D[] rest)
    {
        var output = new Transform3D[7];
        LinkTransforms(angles, rest, output);
        return output;
    }

    private static void LinkTransforms(ReadOnlySpan<float> angles, Transform3D[] rest, Span<Transform3D> output)
    {
        output[0] = Transform3D.Identity;
        for (int i = 0; i < 6; i++)
        {
            Transform3D local = rest[i];
            local.Basis *= new Basis(RobotController.JointAxes[i], Mathf.DegToRad(angles[i]));
            output[i + 1] = output[i] * local;
        }
    }

    /// <summary>Source-mesh slices enclose all overlapping triangles, retaining actual link shape without one large link box.</summary>
    public static RobotCapsule[] CreateRobotCapsules(IReadOnlyDictionary<int, Vector3[]> linkTriangles, float sliceLength = .065f)
    {
        var output = new List<RobotCapsule>();
        Span<Vector3> triangle = stackalloc Vector3[3];
        Span<Vector3> clippedLow = stackalloc Vector3[6];
        Span<Vector3> clippedHigh = stackalloc Vector3[6];
        foreach (var item in linkTriangles)
        {
            Vector3[] vertices = item.Value;
            if (vertices.Length < 3) continue;
            Vector3 min = vertices[0], max = min;
            foreach (var v in vertices) { min = min.Min(v); max = max.Max(v); }
            Vector3 size = max - min;
            int axis = size.X > size.Y ? (size.X > size.Z ? 0 : 2) : (size.Y > size.Z ? 1 : 2);
            int count = Math.Max(1, (int)Math.Ceiling(size[axis] / Math.Max(.02f, sliceLength)));
            for (int slice = 0; slice < count; slice++)
            {
                float low = Mathf.Lerp(min[axis], max[axis], slice / (float)count);
                float high = Mathf.Lerp(min[axis], max[axis], (slice + 1f) / count);
                // Clip each triangle to this slab. Using unclipped triangle vertices would inflate long CAD tessellation faces.
                var points = new List<Vector3>();
                for (int i = 0; i + 2 < vertices.Length; i += 3)
                {
                    triangle[0] = vertices[i]; triangle[1] = vertices[i + 1]; triangle[2] = vertices[i + 2];
                    float triangleLow = Math.Min(triangle[0][axis], Math.Min(triangle[1][axis], triangle[2][axis]));
                    float triangleHigh = Math.Max(triangle[0][axis], Math.Max(triangle[1][axis], triangle[2][axis]));
                    if (triangleHigh < low || triangleLow > high) continue;
                    int lowCount = Clip(triangle, clippedLow, axis, low, true);
                    int highCount = Clip(clippedLow[..lowCount], clippedHigh, axis, high, false);
                    for (int point = 0; point < highCount; point++) points.Add(clippedHigh[point]);
                }
                if (points.Count == 0) continue;
                Vector3 lo = points[0], hi = lo;
                foreach (Vector3 v in points) { lo = lo.Min(v); hi = hi.Max(v); }
                Vector3 centre = (lo + hi) * .5f;
                float radius = 0;
                foreach (Vector3 v in points) { Vector3 delta = v - centre; delta[axis] = 0; radius = Math.Max(radius, delta.Length()); }
                Vector3 a = centre, b = centre;
                a[axis] = low; b[axis] = high;
                output.Add(new RobotCapsule(item.Key, a, b, Math.Max(.003f, radius), new Aabb(lo, hi - lo)));
            }
        }
        return output.ToArray();
    }

    private static int Clip(ReadOnlySpan<Vector3> polygon, Span<Vector3> output, int axis, float plane, bool above)
    {
        int count = 0; if (polygon.Length == 0) return count;
        Vector3 previous = polygon[^1]; bool previousIn = above ? previous[axis] >= plane : previous[axis] <= plane;
        foreach (var current in polygon)
        {
            bool currentIn = above ? current[axis] >= plane : current[axis] <= plane;
            if (currentIn != previousIn) output[count++] = previous.Lerp(current, (plane - previous[axis]) / (current[axis] - previous[axis]));
            if (currentIn) output[count++] = current;
            previous = current; previousIn = currentIn;
        }
        return count;
    }

    private static string LinkName(int link) => link == 0 ? "Base" : link == 7 ? "Torch" : $"A{link}";

    public static float DistanceSegmentSegmentSquared(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
    {
        Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
        float a = d1.Dot(d1), e = d2.Dot(d2), f = d2.Dot(r), s, t;
        if (a <= 1e-14f && e <= 1e-14f) return r.LengthSquared();
        if (a <= 1e-14f) { s = 0; t = Mathf.Clamp(f / e, 0, 1); }
        else
        {
            float c = d1.Dot(r);
            if (e <= 1e-14f) { t = 0; s = Mathf.Clamp(-c / a, 0, 1); }
            else
            {
                float b = d1.Dot(d2), denom = a * e - b * b;
                s = denom != 0 ? Mathf.Clamp((b * f - c * e) / denom, 0, 1) : 0;
                t = (b * s + f) / e;
                if (t < 0) { t = 0; s = Mathf.Clamp(-c / a, 0, 1); }
                else if (t > 1) { t = 1; s = Mathf.Clamp((b - c) / a, 0, 1); }
            }
        }
        return (p1 + s * d1 - p2 - t * d2).LengthSquared();
    }
}

/// <summary>Tight source-mesh slab bounds. SAT narrow phase avoids capsule inflation near the shoulder and wrist.</summary>
public readonly struct OrientedBounds
{
    public readonly Vector3 Centre, Half;
    public readonly Basis Rotation;
    public OrientedBounds(Aabb local, Transform3D pose) { Centre = pose * local.GetCenter(); Half = local.Size * .5f; Rotation = pose.Basis; }
    public float MinimumY => Centre.Y - Math.Abs(Rotation.X.Y) * Half.X - Math.Abs(Rotation.Y.Y) * Half.Y - Math.Abs(Rotation.Z.Y) * Half.Z;
    public Aabb WorldBounds
    {
        get { Vector3 extent = Rotation.X.Abs() * Half.X + Rotation.Y.Abs() * Half.Y + Rotation.Z.Abs() * Half.Z; return new Aabb(Centre - extent, extent * 2); }
    }
    private float Radius(Vector3 axis) => Math.Abs(axis.Dot(Rotation.X)) * Half.X + Math.Abs(axis.Dot(Rotation.Y)) * Half.Y + Math.Abs(axis.Dot(Rotation.Z)) * Half.Z;
    public bool Intersects(OrientedBounds other, float margin)
    {
        Vector3 delta = other.Centre - Centre;
        for (int i = 0; i < 3; i++)
        {
            if (Separated(Rotation[i], delta, other, margin) || Separated(other.Rotation[i], delta, other, margin)) return false;
            for (int j = 0; j < 3; j++) if (Separated(Rotation[i].Cross(other.Rotation[j]), delta, other, margin)) return false;
        }
        return true;
    }
    private bool Separated(Vector3 axis, Vector3 delta, OrientedBounds other, float margin) => axis.LengthSquared() > 1e-12f && Math.Abs(delta.Dot(axis)) > Radius(axis) + other.Radius(axis) + margin * axis.Length();
    public bool IntersectsTriangle(Vector3 a, Vector3 b, Vector3 c, float margin)
    {
        Basis inverse = Rotation.Transposed(); a = inverse * (a - Centre); b = inverse * (b - Centre); c = inverse * (c - Centre);
        Vector3 half = Half + Vector3.One * margin;
        Span<Vector3> edges = stackalloc Vector3[] { b - a, c - b, a - c };
        for (int i = 0; i < 3; i++)
        {
            Vector3 axis = i == 0 ? Vector3.Right : i == 1 ? Vector3.Up : Vector3.Back;
            if (TriangleSeparated(axis, a, b, c, half)) return false;
            foreach (Vector3 edge in edges) if (TriangleSeparated(axis.Cross(edge), a, b, c, half)) return false;
        }
        return !TriangleSeparated(edges[0].Cross(edges[1]), a, b, c, half);
    }
    private static bool TriangleSeparated(Vector3 axis, Vector3 a, Vector3 b, Vector3 c, Vector3 half)
    {
        float radius = axis.Abs().Dot(half), pa = axis.Dot(a), pb = axis.Dot(b), pc = axis.Dot(c);
        return Math.Min(pa, Math.Min(pb, pc)) > radius || Math.Max(pa, Math.Max(pb, pc)) < -radius;
    }
    public bool IntersectsCapsule(Vector3 a, Vector3 b, float radius)
    {
        Basis inverse = Rotation.Transposed(); a = inverse * (a - Centre); b = inverse * (b - Centre); Vector3 d = b - a;
        Span<float> cuts = stackalloc float[8]; cuts[0] = 0; cuts[1] = 1; int cutCount = 2;
        for (int axis = 0; axis < 3; axis++) if (Math.Abs(d[axis]) > 1e-12f)
            for (int sign = -1; sign <= 1; sign += 2) { float t = (sign * Half[axis] - a[axis]) / d[axis]; if (t > 0 && t < 1) cuts[cutCount++] = t; }
        cuts = cuts[..cutCount]; cuts.Sort(); float minimum = float.PositiveInfinity;
        for (int i = 1; i < cuts.Length; i++)
        {
            float lo = cuts[i - 1], hi = cuts[i], middle = (lo + hi) * .5f, aa = 0, ab = 0;
            for (int axis = 0; axis < 3; axis++)
            {
                float at = a[axis] + d[axis] * middle;
                if (Math.Abs(at) <= Half[axis]) continue;
                float offset = a[axis] - Math.Sign(at) * Half[axis]; aa += d[axis] * d[axis]; ab += d[axis] * offset;
            }
            float t = aa > 0 ? Mathf.Clamp(-ab / aa, lo, hi) : lo;
            Vector3 p = a + d * t; Vector3 outside = (p.Abs() - Half).Max(Vector3.Zero);
            minimum = Math.Min(minimum, outside.LengthSquared());
        }
        return minimum <= radius * radius;
    }
}

/// <summary>Triangle surface BVH with closed-solid containment, independent of Godot's physics server.</summary>
public sealed class TriangleBvh
{
    private readonly Vector3[] _vertices;
    private readonly int[] _order;
    private readonly Lazy<Vector3[]> _componentSamples;
    private readonly List<Branch> _nodes = new();
    private readonly record struct Branch(Vector3 Min, Vector3 Max, int Start, int Count, int Left, int Right);
    private readonly record struct RayHit(float Distance, float Orientation);
    public Aabb Bounds { get; }

    public TriangleBvh(Vector3[] triangles, bool collectComponentSamples = false)
    {
        if (triangles.Length % 3 != 0 || triangles.Any(p => !p.IsFinite())) throw new ArgumentException("Finite triangle triplets are required.");
        _vertices = (Vector3[])triangles.Clone(); _order = Enumerable.Range(0, triangles.Length / 3).ToArray();
        _componentSamples = new Lazy<Vector3[]>(() => ComponentSamples(_vertices));
        if (collectComponentSamples) _ = _componentSamples.Value;
        if (_order.Length == 0) { Bounds = new Aabb(); return; }
        Build(0, _order.Length);
        Bounds = new Aabb(_nodes[0].Min, _nodes[0].Max - _nodes[0].Min);
    }

    private int Build(int start, int count)
    {
        Vector3 min = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity), max = -min;
        for (int i = start; i < start + count; i++) for (int v = 0; v < 3; v++)
        { Vector3 p = _vertices[_order[i] * 3 + v]; min = min.Min(p); max = max.Max(p); }
        int index = _nodes.Count; _nodes.Add(new Branch(min, max, start, count, -1, -1));
        if (count > 10)
        {
            Vector3 size = max - min; int axis = size.X > size.Y ? (size.X > size.Z ? 0 : 2) : (size.Y > size.Z ? 1 : 2);
            Array.Sort(_order, start, count, Comparer<int>.Create((a, b) => Centroid(a, axis).CompareTo(Centroid(b, axis))));
            int left = Build(start, count / 2), right = Build(start + count / 2, count - count / 2);
            _nodes[index] = new Branch(min, max, start, 0, left, right);
        }
        return index;
    }

    private float Centroid(int triangle, int axis) => (_vertices[triangle * 3][axis] + _vertices[triangle * 3 + 1][axis] + _vertices[triangle * 3 + 2][axis]) / 3f;

    /// <summary>Source surface distance plus closed-solid containment; otherPose maps the other mesh into this mesh's frame.</summary>
    public bool IntersectsMesh(TriangleBvh other, Transform3D otherPose, float margin)
    {
        if (_nodes.Count == 0 || other._nodes.Count == 0) return false;
        if (MeshNodes(0, other, 0, otherPose, margin)) return true;
        // Test every disconnected CAD shell: one representative of an assembly does not
        // detect a small enclosed component when its other components remain outside.
        foreach (Vector3 point in other._componentSamples.Value)
            if (ContainsPoint(otherPose * point)) return true;
        Transform3D inverse = otherPose.AffineInverse();
        foreach (Vector3 point in _componentSamples.Value)
            if (other.ContainsPoint(inverse * point)) return true;
        return false;
    }

    private bool MeshNodes(int id, TriangleBvh other, int otherId, Transform3D pose, float margin)
    {
        Branch a = _nodes[id], b = other._nodes[otherId];
        var bBox = new OrientedBounds(new Aabb(b.Min, b.Max - b.Min), pose);
        Aabb worldB = bBox.WorldBounds.Grow(margin);
        if (!Overlap(a.Min, a.Max, worldB.Position, worldB.End)) return false;
        var aBox = new OrientedBounds(new Aabb(a.Min, a.Max - a.Min), Transform3D.Identity);
        if (!aBox.Intersects(bBox, margin)) return false;
        if (a.Left >= 0 && (b.Left < 0 || (a.Max - a.Min).LengthSquared() >= (b.Max - b.Min).LengthSquared()))
            return MeshNodes(a.Left, other, otherId, pose, margin) || MeshNodes(a.Right, other, otherId, pose, margin);
        if (b.Left >= 0)
            return MeshNodes(id, other, b.Left, pose, margin) || MeshNodes(id, other, b.Right, pose, margin);
        float distanceSquared = margin * margin;
        for (int i = b.Start; i < b.Start + b.Count; i++)
        {
            int v = other._order[i] * 3;
            Vector3 p = pose * other._vertices[v], q = pose * other._vertices[v + 1], r = pose * other._vertices[v + 2];
            if (!aBox.IntersectsTriangle(p, q, r, margin)) continue;
            for (int j = a.Start; j < a.Start + a.Count; j++)
            {
                int w = _order[j] * 3;
                Vector3 x = _vertices[w], y = _vertices[w + 1], z = _vertices[w + 2];
                if (DistanceSegmentTriangleSquared(p, q, x, y, z) <= distanceSquared ||
                    DistanceSegmentTriangleSquared(q, r, x, y, z) <= distanceSquared ||
                    DistanceSegmentTriangleSquared(r, p, x, y, z) <= distanceSquared ||
                    DistanceSegmentTriangleSquared(x, y, p, q, r) <= distanceSquared ||
                    DistanceSegmentTriangleSquared(y, z, p, q, r) <= distanceSquared ||
                    DistanceSegmentTriangleSquared(z, x, p, q, r) <= distanceSquared) return true;
            }
        }
        return false;
    }

    private static Vector3[] ComponentSamples(Vector3[] vertices)
    {
        var parent = Enumerable.Range(0, vertices.Length / 3).ToArray();
        int Root(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        var firstTriangle = new Dictionary<Vector3, int>();
        for (int i = 0; i < vertices.Length; i++)
        {
            int triangle = i / 3;
            if (firstTriangle.TryGetValue(vertices[i], out int previous)) parent[Root(triangle)] = Root(previous);
            else firstTriangle.Add(vertices[i], triangle);
        }
        return Enumerable.Range(0, parent.Length).Where(i => Root(i) == i).Select(i => vertices[i * 3]).ToArray();
    }

    public bool IntersectsCapsule(Vector3 a, Vector3 b, float radius)
    {
        if (_nodes.Count == 0) return false;
        Vector3 lo = a.Min(b) - Vector3.One * radius, hi = a.Max(b) + Vector3.One * radius;
        if (!Overlap(lo, hi, _nodes[0].Min, _nodes[0].Max)) return false;
        return CapsuleNode(0, a, b, radius * radius, lo, hi) || ContainsPoint((a + b) * .5f);
    }

    public bool IntersectsBox(OrientedBounds box, float margin)
    {
        if (_nodes.Count == 0) return false;
        Aabb bounds = box.WorldBounds.Grow(margin);
        if (!Overlap(bounds.Position, bounds.End, _nodes[0].Min, _nodes[0].Max)) return false;
        return BoxNode(0, box, margin, bounds) || ContainsPoint(box.Centre);
    }

    private bool BoxNode(int id, OrientedBounds box, float margin, Aabb bounds)
    {
        Branch node = _nodes[id]; if (!Overlap(bounds.Position, bounds.End, node.Min, node.Max)) return false;
        if (node.Left >= 0) return BoxNode(node.Left, box, margin, bounds) || BoxNode(node.Right, box, margin, bounds);
        for (int i = node.Start; i < node.Start + node.Count; i++)
        {
            int v = _order[i] * 3; if (box.IntersectsTriangle(_vertices[v], _vertices[v + 1], _vertices[v + 2], margin)) return true;
        }
        return false;
    }

    private bool CapsuleNode(int id, Vector3 a, Vector3 b, float radiusSq, Vector3 lo, Vector3 hi)
    {
        Branch node = _nodes[id]; if (!Overlap(lo, hi, node.Min, node.Max)) return false;
        if (node.Left >= 0) return CapsuleNode(node.Left, a, b, radiusSq, lo, hi) || CapsuleNode(node.Right, a, b, radiusSq, lo, hi);
        for (int i = node.Start; i < node.Start + node.Count; i++)
        {
            int v = _order[i] * 3;
            if (DistanceSegmentTriangleSquared(a, b, _vertices[v], _vertices[v + 1], _vertices[v + 2]) <= radiusSq) return true;
        }
        return false;
    }

    public bool ContainsPoint(Vector3 point)
    {
        if (_nodes.Count == 0 || !Bounds.HasPoint(point)) return false;
        Vector3 direction = new Vector3(.872317f, .331417f, .359071f).Normalized();
        var hits = new List<RayHit>(); RayNode(0, point, direction, hits); hits.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        // Signed winding handles intersecting assembly solids, whereas odd/even parity would treat their overlap as empty.
        int winding = 0;
        for (int i = 0; i < hits.Count;)
        {
            float distance = hits[i].Distance, positive = 0, negative = 0;
            int j = i;
            while (j < hits.Count && hits[j].Distance - distance <= .000001f)
            { if (hits[j].Orientation > 0) positive = 1; else negative = 1; j++; }
            if (distance > .000001f) winding += (int)(positive - negative);
            i = j;
        }
        return winding != 0;
    }

    private void RayNode(int id, Vector3 origin, Vector3 direction, List<RayHit> hits)
    {
        Branch node = _nodes[id]; if (!RayBounds(origin, direction, node.Min, node.Max)) return;
        if (node.Left >= 0) { RayNode(node.Left, origin, direction, hits); RayNode(node.Right, origin, direction, hits); return; }
        for (int i = node.Start; i < node.Start + node.Count; i++)
        {
            int v = _order[i] * 3;
            if (RayTriangle(origin, direction, _vertices[v], _vertices[v + 1], _vertices[v + 2], out float distance))
                hits.Add(new RayHit(distance, (_vertices[v + 1] - _vertices[v]).Cross(_vertices[v + 2] - _vertices[v]).Dot(direction)));
        }
    }

    private static bool RayBounds(Vector3 o, Vector3 d, Vector3 min, Vector3 max)
    {
        float near = 0, far = float.PositiveInfinity;
        for (int axis = 0; axis < 3; axis++)
        {
            if (Math.Abs(d[axis]) < 1e-12f) { if (o[axis] < min[axis] || o[axis] > max[axis]) return false; continue; }
            float a = (min[axis] - o[axis]) / d[axis], b = (max[axis] - o[axis]) / d[axis];
            near = Math.Max(near, Math.Min(a, b)); far = Math.Min(far, Math.Max(a, b)); if (near > far) return false;
        }
        return true;
    }

    private static bool Overlap(Vector3 a, Vector3 b, Vector3 c, Vector3 d) => a.X <= d.X && b.X >= c.X && a.Y <= d.Y && b.Y >= c.Y && a.Z <= d.Z && b.Z >= c.Z;

    public static float DistanceSegmentTriangleSquared(Vector3 p, Vector3 q, Vector3 a, Vector3 b, Vector3 c)
    {
        if (RayTriangle(p, q - p, a, b, c, out float t) && t <= 1) return 0;
        float result = Math.Min(DistancePointTriangleSquared(p, a, b, c), DistancePointTriangleSquared(q, a, b, c));
        result = Math.Min(result, CollisionScene.DistanceSegmentSegmentSquared(p, q, a, b));
        result = Math.Min(result, CollisionScene.DistanceSegmentSegmentSquared(p, q, b, c));
        return Math.Min(result, CollisionScene.DistanceSegmentSegmentSquared(p, q, c, a));
    }

    private static bool RayTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float distance)
    {
        distance = 0; Vector3 ab = b - a, ac = c - a, h = direction.Cross(ac); float det = ab.Dot(h);
        if (Math.Abs(det) < 1e-12f) return false;
        float inv = 1f / det; Vector3 s = origin - a; float u = inv * s.Dot(h);
        if (u < -1e-6f || u > 1 + 1e-6f) return false;
        Vector3 q = s.Cross(ab); float v = inv * direction.Dot(q);
        if (v < -1e-6f || u + v > 1 + 1e-6f) return false;
        distance = inv * ac.Dot(q); return distance >= 0;
    }

    public static float DistancePointTriangleSquared(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a; float d1 = ab.Dot(ap), d2 = ac.Dot(ap);
        if (d1 <= 0 && d2 <= 0) return ap.LengthSquared();
        Vector3 bp = p - b; float d3 = ab.Dot(bp), d4 = ac.Dot(bp);
        if (d3 >= 0 && d4 <= d3) return bp.LengthSquared();
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return (p - (a + d1 / (d1 - d3) * ab)).LengthSquared();
        Vector3 cp = p - c; float d5 = ab.Dot(cp), d6 = ac.Dot(cp);
        if (d6 >= 0 && d5 <= d6) return cp.LengthSquared();
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return (p - (a + d2 / (d2 - d6) * ac)).LengthSquared();
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return (p - (b + (d4 - d3) / ((d4 - d3) + d5 - d6) * (c - b))).LengthSquared();
        float denom = va + vb + vc;
        if (Math.Abs(denom) < 1e-20f) return Math.Min(CollisionScene.DistanceSegmentSegmentSquared(p, p, a, b), Math.Min(CollisionScene.DistanceSegmentSegmentSquared(p, p, b, c), CollisionScene.DistanceSegmentSegmentSquared(p, p, c, a)));
        return (p - (a + ab * (vb / denom) + ac * (vc / denom))).LengthSquared();
    }
}
