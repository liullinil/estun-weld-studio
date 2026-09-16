using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace EstunStudio;

/// <summary>Offline planner. Endpoint ordering and 2-opt minimize a distance heuristic, with IK/collision-checked alternatives.</summary>
public static class WeldPlanner
{
    private sealed class RouteSeam
    {
        public required CadSeam Source;
        public required PlannedSeam Result;
        public required Vector3[] Points;
        public bool Reverse;
        public Vector3 Start => Reverse ? Points[^1] : Points[0];
        public Vector3 End => Reverse ? Points[0] : Points[^1];
    }
    private sealed class Candidate
    {
        public List<WeldMotion> Weld { get; } = new();
        public List<WeldMotion> Entry { get; } = new();
        public List<WeldMotion> Exit { get; } = new();
        public float[] EndAngles => Exit.Count > 0 ? Exit[^1].TargetAngles : Weld[^1].TargetAngles;
    }

    public static WeldProgram Plan(WeldPlanRequest request, CancellationToken cancellation = default, Action<float, string>? progress = null)
    {
        if (request.StartAngles.Length != 6 || request.JointRest.Length != 6 || request.SampleSpacing <= 0 || request.WeldSpeed <= 0 || request.TravelSpeed <= 0)
            throw new ArgumentException("Invalid welding plan input.");
        var program = new WeldProgram { StartAngles = (float[])request.StartAngles.Clone(), ToolTransform = request.ToolTransform, PartName = request.PartName };
        var route = new List<RouteSeam>();
        foreach (CadSeam seam in request.Seams)
        {
            cancellation.ThrowIfCancellationRequested();
            Vector3[] points = seam.Points.Select(p => request.PartTransform * p).ToArray();
            var result = new PlannedSeam { Id = seam.Id, Points = points, State = seam.Deleted ? WeldSeamState.Deleted : WeldSeamState.Unreachable, Length = Length(points) };
            program.Seams.Add(result);
            if (seam.Deleted) { result.Reason = "Removed by operator"; continue; }
            if (points.Length < 2 || result.Length < .0005f || points.Any(p => !p.IsFinite())) { result.Reason = "Seam is too short or invalid"; continue; }
            route.Add(new RouteSeam { Source = seam, Result = result, Points = points });
        }
        if (!request.Collision.Check(request.StartAngles, out string startReason))
        {
            foreach (RouteSeam seam in route) { seam.Result.State = WeldSeamState.TransferBlocked; seam.Result.Reason = "Start pose: " + startReason; }
            progress?.Invoke(1, "Move robot or workpiece clear of the starting collision"); return program;
        }
        Vector3 startPosition = RobotKinematics.Forward(request.StartAngles, request.JointRest, request.ToolTransform).Origin;
        route = OrderRoute(route, startPosition);
        float[] current = (float[])request.StartAngles.Clone();
        int ready = 0;
        for (int index = 0; index < route.Count; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            RouteSeam seam = route[index];
            progress?.Invoke(index / (float)Math.Max(1, route.Count), $"Checking {seam.Source.Id} ({index + 1}/{route.Count})");
            string reason = "No six-axis inverse kinematics solution";
            WeldSeamState state = WeldSeamState.Unreachable;
            Candidate? candidate = null;
            int orientationAttempt = 0;
            void ReportAttempt()
            {
                cancellation.ThrowIfCancellationRequested();
                progress?.Invoke((index + orientationAttempt / 12f) / Math.Max(1, route.Count),
                    $"Checking {seam.Source.Id} ({index + 1}/{route.Count}) · orientation {++orientationAttempt}/12");
            }
            foreach (bool reverse in new[] { seam.Reverse, !seam.Reverse })
            {
                SampleSeam(request, seam.Source, reverse, out Vector3[] points, out Vector3[] approaches);
                // Face normals determine the 45-degree fillet bisector. Roll chooses a wrist branch without changing the arc direction.
                foreach (float roll in new[] { 0f, 180f, 90f, -90f })
                {
                    ReportAttempt();
                    candidate = TrySeam(request, points, approaches, roll, current, seam.Source.Id, cancellation, out string failure, out WeldSeamState failureState);
                    if (candidate != null) break;
                    if ((int)failureState >= (int)state) { reason = failure; state = failureState; }
                }
                if (candidate != null) { seam.Result.Reversed = reverse; break; }
                // A small push/pull inclination can clear the bent torch neck while preserving the work angle.
                foreach (float lean in new[] { -12f, 12f })
                {
                    ReportAttempt();
                    Vector3[] inclined = approaches.Select((approach, i) =>
                    {
                        Vector3 tangent = (points[Math.Min(i + 1, points.Length - 1)] - points[Math.Max(0, i - 1)]).Normalized();
                        return (approach * Mathf.Cos(Mathf.DegToRad(lean)) + tangent * Mathf.Sin(Mathf.DegToRad(lean))).Normalized();
                    }).ToArray();
                    candidate = TrySeam(request, points, inclined, 0, current, seam.Source.Id, cancellation, out string failure, out WeldSeamState failureState);
                    if (candidate != null) { seam.Result.Reversed = reverse; break; }
                    if ((int)failureState >= (int)state) { reason = failure; state = failureState; }
                }
                if (candidate != null) break;
            }
            if (candidate == null) { seam.Result.State = state; seam.Result.Reason = reason; continue; }
            seam.Result.State = WeldSeamState.Ready;
            seam.Result.Reason = "Six-axis path and transfers validated";
            seam.Result.Order = ++ready;
            program.Motions.AddRange(candidate.Entry);
            program.Motions.AddRange(candidate.Weld);
            program.Motions.AddRange(candidate.Exit);
            current = candidate.EndAngles;
        }
        progress?.Invoke(1, program.Summary);
        return program;
    }

    private static Candidate? TrySeam(WeldPlanRequest request, Vector3[] points, Vector3[] approaches, float roll,
        float[] current, string seamId, CancellationToken cancellation, out string reason, out WeldSeamState state)
    {
        reason = "No six-axis solution at the seam"; state = WeldSeamState.Unreachable;
        var poses = new Transform3D[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            Vector3 tangent = points[Math.Min(i + 1, points.Length - 1)] - points[Math.Max(0, i - 1)];
            Basis basis = TorchBasis(tangent.Normalized(), approaches[i], roll);
            poses[i] = new Transform3D(basis, points[i] + basis.Y * request.ArcGap);
        }
        // Try distinct IK basins at the seam entrance; subsequent points stay in the same continuous basin.
        foreach (float[] seed in Seeds(current))
        {
            cancellation.ThrowIfCancellationRequested();
            var solved = RobotKinematics.Solve(poses[0], seed, request.JointRest, request.ToolTransform);
            if (!solved.Success) continue;
            if (!request.Collision.Check(solved.AnglesDegrees, out string collision)) { reason = collision; state = WeldSeamState.Collision; continue; }
            var candidate = new Candidate();
            float[] previous = solved.AnglesDegrees;
            candidate.Weld.Add(Motion(previous, poses[0], points[0], false, seamId, .04f));
            bool weldValid = true;
            for (int i = 1; i < poses.Length; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                solved = RobotKinematics.Solve(poses[i], previous, request.JointRest, request.ToolTransform);
                if (!solved.Success) { reason = $"Six-axis IK fails at {100f * i / (poses.Length - 1):0}% of seam"; weldValid = false; break; }
                if (MaximumJointChange(previous, solved.AnglesDegrees) > 18f)
                { reason = "Joint branch discontinuity / wrist singularity"; weldValid = false; break; }
                if (!request.Collision.CheckMotion(previous, solved.AnglesDegrees, out collision, cancellation))
                { reason = collision; state = WeldSeamState.Collision; weldValid = false; break; }
                // Check Cartesian chord deviation because playback follows the exact validated joint interpolation.
                float[] midpoint = Interpolate(previous, solved.AnglesDegrees, .5f);
                Vector3 actual = RobotKinematics.Forward(midpoint, request.JointRest, request.ToolTransform).Origin;
                if (actual.DistanceTo(poses[i - 1].Origin.Lerp(poses[i].Origin, .5f)) > .001f)
                { reason = "TCP deviation exceeds 1 mm near singularity"; weldValid = false; break; }
                float duration = MotionDuration(previous, solved.AnglesDegrees, points[i - 1].DistanceTo(points[i]) / request.WeldSpeed);
                candidate.Weld.Add(Motion(solved.AnglesDegrees, poses[i], points[i], true, seamId, duration));
                previous = solved.AnglesDegrees;
            }
            if (!weldValid) continue;
            Transform3D exit = poses[^1]; exit.Origin += exit.Basis.Y * request.RetractDistance;
            if (!CartesianLeg(request, previous, exit, seamId, candidate.Exit, cancellation, out collision))
            { reason = "Retraction blocked: " + collision; state = WeldSeamState.TransferBlocked; continue; }
            Transform3D entry = poses[0]; entry.Origin += entry.Basis.Y * request.RetractDistance;
            solved = RobotKinematics.Solve(entry, candidate.Weld[0].TargetAngles, request.JointRest, request.ToolTransform);
            if (!solved.Success || !request.Collision.Check(solved.AnglesDegrees, out collision))
            { reason = "No clear approach position"; state = WeldSeamState.TransferBlocked; continue; }
            if (!Transfer(request, current, solved.AnglesDegrees, seamId, candidate.Entry, cancellation, out collision))
            { reason = "Transfer blocked: " + collision; state = WeldSeamState.TransferBlocked; continue; }
            if (!CartesianLeg(request, solved.AnglesDegrees, poses[0], seamId, candidate.Entry, cancellation, out collision,
                candidate.Weld[0].TargetAngles))
            { reason = "Approach blocked: " + collision; state = WeldSeamState.TransferBlocked; continue; }
            return candidate;
        }
        return null;
    }

    public static Basis TorchBasis(Vector3 travel, Vector3 awayFromWeld, float rollDegrees = 0)
    {
        Vector3 y = awayFromWeld.Normalized();
        if (y.LengthSquared() < .5f) y = Vector3.Up;
        Vector3 x = travel - y * travel.Dot(y);
        if (x.LengthSquared() < .0001f) x = Math.Abs(y.Dot(Vector3.Right)) < .9f ? Vector3.Right.Cross(y) : Vector3.Back.Cross(y);
        x = x.Normalized(); Vector3 z = x.Cross(y).Normalized(); x = y.Cross(z).Normalized();
        return new Basis(y, Mathf.DegToRad(rollDegrees)) * new Basis(x, y, z);
    }

    private static bool Transfer(WeldPlanRequest request, float[] from, float[] to, string seamId,
        List<WeldMotion> result, CancellationToken cancellation, out string reason)
    {
        Transform3D start = RobotKinematics.Forward(from, request.JointRest, request.ToolTransform);
        Transform3D end = RobotKinematics.Forward(to, request.JointRest, request.ToolTransform);
        if (request.Collision.CheckMotion(from, to, out reason, cancellation))
        {
            result.Add(Motion(to, end, end.Origin, false, seamId, MotionDuration(from, to, start.Origin.DistanceTo(end.Origin) / request.TravelSpeed)));
            return true;
        }
        float top = Math.Max(request.Collision.PartBounds.End.Y, Math.Max(start.Origin.Y, end.Origin.Y));
        // Several lift heights and side corridors are tested; every connecting joint segment is checked.
        foreach (float lift in new[] { .15f, .30f, .50f })
            foreach (float side in new[] { 0f, -.24f, .24f })
            {
                cancellation.ThrowIfCancellationRequested();
                var trial = new List<WeldMotion>();
                Transform3D highStart = start, highEnd = end;
                highStart.Origin = new Vector3(start.Origin.X, top + lift, start.Origin.Z + side);
                highEnd.Origin = new Vector3(end.Origin.X, top + lift, end.Origin.Z + side);
                if (!CartesianLeg(request, from, highStart, seamId, trial, cancellation, out reason)) continue;
                if (!CartesianLeg(request, trial[^1].TargetAngles, highEnd, seamId, trial, cancellation, out reason)) continue;
                if (!CartesianLeg(request, trial[^1].TargetAngles, end, seamId, trial, cancellation, out reason, to)) continue;
                result.AddRange(trial); return true;
            }
        return false;
    }

    private static bool CartesianLeg(WeldPlanRequest request, float[] from, Transform3D target, string seamId,
        List<WeldMotion> result, CancellationToken cancellation, out string reason, float[]? requiredEnd = null)
    {
        Transform3D start = RobotKinematics.Forward(from, request.JointRest, request.ToolTransform);
        int count = Math.Max(1, (int)Math.Ceiling(Math.Max(start.Origin.DistanceTo(target.Origin) / .025f,
            RobotKinematics.RotationError(target.Basis, start.Basis).Length() / .10f)));
        var trial = new List<WeldMotion>(); float[] previous = from;
        for (int i = 1; i <= count; i++)
        {
            cancellation.ThrowIfCancellationRequested(); float t = i / (float)count;
            Transform3D pose = start.InterpolateWith(target, t);
            var solution = RobotKinematics.Solve(pose, previous, request.JointRest, request.ToolTransform);
            if (!solution.Success) { reason = "Clearance pose is unreachable"; return false; }
            float[] angles = i == count && requiredEnd != null ? requiredEnd : solution.AnglesDegrees;
            if (!request.Collision.CheckMotion(previous, angles, out reason, cancellation)) return false;
            float duration = MotionDuration(previous, angles, start.Origin.DistanceTo(target.Origin) / count / request.TravelSpeed);
            trial.Add(Motion(angles, pose, pose.Origin, false, seamId, duration)); previous = angles;
        }
        result.AddRange(trial); reason = ""; return true;
    }

    private static WeldMotion Motion(float[] angles, Transform3D tcp, Vector3 weldPoint, bool arc, string id, float duration) => new()
    {
        TargetAngles = (float[])angles.Clone(), Tcp = tcp, WeldPoint = weldPoint, WorldApproach = tcp.Basis.Y,
        ArcOn = arc, SeamId = id, DurationSeconds = Math.Max(.016f, duration)
    };

    private static float MotionDuration(float[] from, float[] to, float desired)
    {
        float[] speeds = { 70, 70, 90, 100, 100, 100 };
        for (int i = 0; i < 6; i++) desired = Math.Max(desired, Math.Abs(to[i] - from[i]) / speeds[i]);
        return Math.Max(.016f, desired);
    }

    private static IEnumerable<float[]> Seeds(float[] current)
    {
        yield return (float[])current.Clone();
        yield return (float[])RobotController.HomeAngles.Clone();
        foreach (float shoulder in new[] { 0f, 90f, -90f, 180f })
        {
            yield return new[] { shoulder, 65f, -45f, 0f, 75f, 0f };
            yield return new[] { shoulder, 115f, 45f, 0f, -75f, 0f };
        }
    }

    public static Vector3[] Resample(IReadOnlyList<Vector3> points, float spacing)
    {
        if (points.Count < 2) return points.ToArray();
        var output = new List<Vector3> { points[0] };
        for (int i = 1; i < points.Count; i++)
        {
            float length = points[i - 1].DistanceTo(points[i]);
            if (length < .000001f) continue;
            int count = Math.Max(1, (int)Math.Ceiling(length / Math.Max(.001f, spacing)));
            for (int j = 1; j <= count; j++) output.Add(points[i - 1].Lerp(points[i], j / (float)count));
        }
        return output.ToArray();
    }

    private static void SampleSeam(WeldPlanRequest request, CadSeam seam, bool reverse, out Vector3[] sampledPoints, out Vector3[] sampledApproaches)
    {
        var points = new List<Vector3>(); var normals = new List<Vector3>();
        Basis normalTransform = request.PartTransform.Basis.Inverse().Transposed();
        Vector3 NormalAt(int i)
        {
            Vector3 a = normalTransform * (seam.NormalsA.Length == seam.Points.Length ? seam.NormalsA[i] : seam.NormalA);
            Vector3 b = normalTransform * (seam.NormalsB.Length == seam.Points.Length ? seam.NormalsB[i] : seam.NormalB);
            Vector3 normal = a.Normalized() + b.Normalized();
            return normal.LengthSquared() > .0001f ? normal.Normalized() : a.LengthSquared() > .0001f ? a.Normalized() : Vector3.Up;
        }
        points.Add(request.PartTransform * seam.Points[0]); normals.Add(NormalAt(0));
        for (int i = 1; i < seam.Points.Length; i++)
        {
            Vector3 a = request.PartTransform * seam.Points[i - 1], b = request.PartTransform * seam.Points[i];
            Vector3 na = NormalAt(i - 1), nb = NormalAt(i);
            if (a.DistanceTo(b) < .000001f) continue;
            int steps = Math.Max(1, (int)Math.Ceiling(Math.Max(a.DistanceTo(b) / request.SampleSpacing, na.AngleTo(nb) / .08f)));
            for (int j = 1; j <= steps; j++) { float t = j / (float)steps; points.Add(a.Lerp(b, t)); normals.Add(na.Slerp(nb, t).Normalized()); }
        }
        if (reverse) { points.Reverse(); normals.Reverse(); }
        sampledPoints = points.ToArray(); sampledApproaches = normals.ToArray();
    }

    private static List<RouteSeam> OrderRoute(List<RouteSeam> source, Vector3 origin)
    {
        var remaining = new List<RouteSeam>(source); var ordered = new List<RouteSeam>(); Vector3 cursor = origin;
        while (remaining.Count > 0)
        {
            RouteSeam best = remaining[0]; float distance = float.PositiveInfinity; bool reverse = false;
            foreach (RouteSeam seam in remaining)
            {
                float a = cursor.DistanceSquaredTo(seam.Points[0]), b = cursor.DistanceSquaredTo(seam.Points[^1]);
                if (Math.Min(a, b) < distance) { best = seam; distance = Math.Min(a, b); reverse = b < a; }
            }
            best.Reverse = reverse; ordered.Add(best); remaining.Remove(best); cursor = best.End;
        }
        // Open-route 2-opt also reverses each seam direction in the reversed section.
        for (int pass = 0; pass < 6; pass++)
        {
            bool improved = false;
            for (int i = 0; i < ordered.Count; i++) for (int j = i + 1; j < ordered.Count; j++)
            {
                Vector3 before = i == 0 ? origin : ordered[i - 1].End;
                float oldDistance = before.DistanceTo(ordered[i].Start), newDistance = before.DistanceTo(ordered[j].End);
                if (j + 1 < ordered.Count) { oldDistance += ordered[j].End.DistanceTo(ordered[j + 1].Start); newDistance += ordered[i].Start.DistanceTo(ordered[j + 1].Start); }
                if (newDistance + .0001f >= oldDistance) continue;
                ordered.Reverse(i, j - i + 1); for (int k = i; k <= j; k++) ordered[k].Reverse = !ordered[k].Reverse;
                improved = true;
            }
            if (!improved) break;
        }
        return ordered;
    }

    private static float Length(Vector3[] points) { float sum = 0; for (int i = 1; i < points.Length; i++) sum += points[i - 1].DistanceTo(points[i]); return sum; }
    private static float MaximumJointChange(float[] a, float[] b) { float max = 0; for (int i = 0; i < 6; i++) max = Math.Max(max, Math.Abs(a[i] - b[i])); return max; }
    private static float[] Interpolate(float[] a, float[] b, float t) { var result = new float[6]; for (int i = 0; i < 6; i++) result[i] = Mathf.Lerp(a[i], b[i], t); return result; }
}
